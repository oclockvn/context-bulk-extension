# Provider-Agnostic Split Review Fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix all six findings from `docs/code-review-provider-agnostic-split.md` (connection leak, PG identity guard, bracket→quote rewrite, UpsertRequest bundling, SqlServer fail-loud hooks, IdentityOutput docs) without changing happy-path behavior for existing serial/int identity use cases.

**Architecture:** Keep template-method orchestration in `BulkProviderBase`. Put pure, testable helpers (identity guard, SQL Server bracket→Postgres quote rewrite) in `BulkProviderHelpers` so unit tests hit them via `InternalsVisibleTo`. Bundle upsert hook args into one `UpsertRequest<T>` record. SqlServer hooks that must never run throw instead of no-op.

**Tech Stack:** .NET 8 / .NET 10, EF Core, xUnit (`ContextBulkExtension.Tests`), SqlServer + Postgres providers.

## Global Constraints

- Real paths (review doc is stale): `ContextBulkExtension.Postgres/PostgresBulkProvider.cs` (not `PostgreSql/PostgreSql…`).
- Core is internal; tests already see Core internals via `ContextBulkExtension.Core/Properties/AssemblyInfo.cs`.
- Do not publish Core as a separate NuGet; providers embed it.
- Preserve existing integration tests (54/54 per TFM). No silent behavior change for int/long serial identity + IdentityOutput.
- Run tests with quiet verbosity: `rtk dotnet test ContextBulkExtension.Tests/ContextBulkExtension.Tests.csproj -v q`.
- YAGNI: no new packages; no dialect-agnostic ExpressionHelper rewrite (bracket unescape is enough for #3).
- Commits: one commit per task after that task’s tests pass.

## File Structure

| File | Responsibility |
|------|----------------|
| `ContextBulkExtension.Core/Abstractions/BulkProviderBase.cs` | Move open/begin inside try (#1); build/pass `UpsertRequest<T>` (#4) |
| `ContextBulkExtension.Core/Abstractions/UpsertRequest.cs` | **Create** — shared upsert/delete/sync request record (#4) |
| `ContextBulkExtension.Core/Helpers/BulkProviderHelpers.cs` | Add `EnsurePostgresIdentityOutputSupported` (#2) + `ToPostgresTargetQualified` (#3) |
| `ContextBulkExtension.Core/BulkConfig.cs` | Expand `IdentityOutput` XML doc (#6) |
| `ContextBulkExtension.Core/DbContextBulkExtension.cs` | Optional one-line XML note on `BulkInsertAsync` overload (#6) |
| `ContextBulkExtension.Postgres/PostgresBulkProvider.cs` | Call helpers (#2/#3); take `UpsertRequest<T>` (#4) |
| `ContextBulkExtension.SqlServer/SqlServerBulkProvider.cs` | Fail-loud hooks (#5); take `UpsertRequest<T>` (#4) |
| `ContextBulkExtension.Tests/BulkProviderHelpersTests.cs` | Unit tests for #2 and #3 |

---

### Task 1: Connection leak window (Issue #1)

**Files:**
- Modify: `ContextBulkExtension.Core/Abstractions/BulkProviderBase.cs:166-274`
- Test: regression — full suite (no isolated unit test; EF connection ref-count needs live provider)

**Interfaces:**
- Consumes: existing `OwnsTransactionWhenMissing`, `OpenConnectionAsync` / `BeginTransactionAsync` / `CloseConnectionAsync`
- Produces: same public `BulkUpsertAsync` behavior; open+begin both covered by outer `finally`

- [ ] **Step 1: Confirm current leak shape**

Open `BulkProviderBase.cs` and verify `OpenConnectionAsync` + `BeginTransactionAsync` sit above `try` (around lines 166–170). Outer `finally` that closes the connection only runs if that `try` is entered.

- [ ] **Step 2: Move begin inside try (open stays before try)**

Replace the upsert connection/transaction preamble so `BeginTransactionAsync` cannot skip the `finally`. Keep `OpenConnectionAsync` immediately before `try` (if open itself throws, there is nothing to close). Exact shape:

```csharp
        await context.Database.OpenConnectionAsync(cancellationToken);

        IDbContextTransaction? ownedTransaction = null;
        try
        {
            if (OwnsTransactionWhenMissing && context.Database.CurrentTransaction == null)
                ownedTransaction = await context.Database.BeginTransactionAsync(cancellationToken);

            await using (var createCmd = CreateCommand(connection, context, BuildCreateStagingSql(stagingTable, columns, needsIdentitySync), config.TimeoutSeconds))
            {
                await createCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            // ... rest of body unchanged (inner try/finally for drop staging, catch, etc.) ...
        }
        catch (Exception ex) when (IsProviderException(ex))
        {
            if (ownedTransaction != null)
                await ownedTransaction.RollbackAsync(cancellationToken);
            throw WrapProviderException(ex, "upsert", typeof(T));
        }
        catch
        {
            if (ownedTransaction != null)
                await ownedTransaction.RollbackAsync(cancellationToken);
            throw;
        }
        finally
        {
            if (ownedTransaction != null)
                await ownedTransaction.DisposeAsync();
            await context.Database.CloseConnectionAsync();
        }
```

Do **not** change the inner staging-drop `try/finally` or catch filters.

- [ ] **Step 3: Verify by inspection**

Checklist (all must be true):
1. `BeginTransactionAsync` is inside the same `try` that has the close `finally`.
2. If begin throws, `CloseConnectionAsync` still runs.
3. Commit still only happens after successful upsert body.
4. `BulkInsertAsync` path (open/try/finally around L120–132) left unchanged.

- [ ] **Step 4: Regression tests**

Run:

```bash
rtk dotnet test ContextBulkExtension.Tests/ContextBulkExtension.Tests.csproj -v q
```

Expected: all tests pass (Docker/LocalDB as usual). No `error TESTERROR`.

- [ ] **Step 5: Commit**

```bash
git add ContextBulkExtension.Core/Abstractions/BulkProviderBase.cs
git commit -m "$(cat <<'EOF'
fix: close connection if BeginTransactionAsync throws

Begin was outside the try/finally that closes the connection, so a
failed owned-transaction begin left the EF connection open.
EOF
)"
```

---

### Task 2: SqlServer fail-loud hooks (Issue #5)

**Files:**
- Modify: `ContextBulkExtension.SqlServer/SqlServerBulkProvider.cs:219-241`
- Test: regression suite (hooks unreachable when flags correct; throw is defensive)

**Interfaces:**
- Consumes: base guards `!BundlesDeleteInUpsert` / `!BundlesIdentityInUpsert` (`BulkProviderBase` ~213–238)
- Produces: SqlServer overrides throw `NotSupportedException` instead of `Task.CompletedTask`

- [ ] **Step 1: Replace no-ops with throws**

In `SqlServerBulkProvider.cs`, replace both overrides:

```csharp
    protected override Task ExecuteDeleteNotMatchedAsync(
        DbContext context,
        DbConnection connection,
        string targetTable,
        string stagingTable,
        IReadOnlyList<ColumnMetadata> matchColumns,
        string? deleteScopeSql,
        List<DbParameter>? deleteScopeParameters,
        BulkConfig config,
        CancellationToken cancellationToken)
        => throw new NotSupportedException(
            "SqlServer bundles WHEN NOT MATCHED BY SOURCE delete into MERGE; ExecuteDeleteNotMatchedAsync must not be called.");

    protected override Task SyncIdentitiesAsync<T>(
        DbContext context,
        DbConnection connection,
        string targetTable,
        string stagingTable,
        IReadOnlyList<ColumnMetadata> matchColumns,
        IReadOnlyList<ColumnMetadata> identityColumns,
        IList<T> entities,
        BulkConfig config,
        CancellationToken cancellationToken) where T : class
        => throw new NotSupportedException(
            "SqlServer bundles identity OUTPUT into MERGE; SyncIdentitiesAsync must not be called.");
```

**Note:** Task 5 will change these signatures to `UpsertRequest<T>`. If implementing tasks in order, use the long signatures here; Task 5 updates them. If doing Task 5 first, write the throw versions against `UpsertRequest<T>` instead.

- [ ] **Step 2: Confirm SqlServer still bundles**

Verify flags remain:

```csharp
    protected override bool BundlesDeleteInUpsert => true;
    protected override bool BundlesIdentityInUpsert => true;
```

- [ ] **Step 3: Run regression**

```bash
rtk dotnet test ContextBulkExtension.Tests/ContextBulkExtension.Tests.csproj -v q
```

Expected: PASS (hooks still unreachable for SqlServer).

- [ ] **Step 4: Commit**

```bash
git add ContextBulkExtension.SqlServer/SqlServerBulkProvider.cs
git commit -m "$(cat <<'EOF'
fix: fail loud if SqlServer delete/identity hooks called

No-op Task.CompletedTask hid flag/override desync; throw instead so
bundled MERGE paths cannot silently skip deletes or identity sync.
EOF
)"
```

---

### Task 3: PostgreSQL IdentityOutput guard (Issue #2)

**Files:**
- Modify: `ContextBulkExtension.Core/Helpers/BulkProviderHelpers.cs`
- Modify: `ContextBulkExtension.Postgres/PostgresBulkProvider.cs` (`BuildUpsertSql` identity branch ~246)
- Test: `ContextBulkExtension.Tests/BulkProviderHelpersTests.cs`

**Interfaces:**
- Consumes: `ColumnMetadata.ClrType`, `IsIdentity` list from `EntityMetadataHelper.GetIdentityColumns`
- Produces: `BulkProviderHelpers.EnsurePostgresIdentityOutputSupported(IReadOnlyList<ColumnMetadata> identityColumns)` — throws `NotSupportedException` unless exactly one `int`/`long` identity column

**Background:** New-row pairing uses two `ROW_NUMBER()` sequences (`ORDER BY identity` vs `ORDER BY __RowIndex`). That is correct for monotonic serial/bigserial assignment in insert order. It is **not** correct for Guid/random identities. Sequence `CACHE` gaps do **not** reverse order within one `INSERT … SELECT … ORDER BY __RowIndex` — do not treat CACHE as a break case.

- [ ] **Step 1: Write failing unit tests**

Add to `BulkProviderHelpersTests.cs`:

```csharp
    [Fact]
    public void EnsurePostgresIdentityOutputSupported_SingleInt_Ok()
    {
        var id = Col(nameof(Sample.Id), "Id", isIdentity: true, isPk: true);
        BulkProviderHelpers.EnsurePostgresIdentityOutputSupported([id]);
    }

    [Fact]
    public void EnsurePostgresIdentityOutputSupported_SingleLong_Ok()
    {
        var prop = typeof(Sample).GetProperty(nameof(Sample.Id))!;
        var id = new ColumnMetadata
        {
            ColumnName = "Id",
            SqlType = "bigint",
            PropertyInfo = prop,
            ClrType = typeof(long),
            ProviderClrType = typeof(long),
            PropertyName = "Id",
            CompiledGetter = _ => null,
            CompiledSetter = (_, _) => { },
            IsIdentity = true,
            IsPrimaryKey = true
        };
        BulkProviderHelpers.EnsurePostgresIdentityOutputSupported([id]);
    }

    [Fact]
    public void EnsurePostgresIdentityOutputSupported_Guid_Throws()
    {
        var prop = typeof(Sample).GetProperty(nameof(Sample.Id))!;
        var id = new ColumnMetadata
        {
            ColumnName = "Id",
            SqlType = "uuid",
            PropertyInfo = prop,
            ClrType = typeof(Guid),
            ProviderClrType = typeof(Guid),
            PropertyName = "Id",
            CompiledGetter = _ => null,
            CompiledSetter = (_, _) => { },
            IsIdentity = true,
            IsPrimaryKey = true
        };
        var ex = Assert.Throws<NotSupportedException>(
            () => BulkProviderHelpers.EnsurePostgresIdentityOutputSupported([id]));
        Assert.Contains("serial/bigserial", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnsurePostgresIdentityOutputSupported_Multiple_Throws()
    {
        var id = Col(nameof(Sample.Id), "Id", isIdentity: true, isPk: true);
        var points = Col(nameof(Sample.Points), "Points", isIdentity: true);
        var ex = Assert.Throws<NotSupportedException>(
            () => BulkProviderHelpers.EnsurePostgresIdentityOutputSupported([id, points]));
        Assert.Contains("single", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnsurePostgresIdentityOutputSupported_Empty_Throws()
    {
        Assert.Throws<NotSupportedException>(
            () => BulkProviderHelpers.EnsurePostgresIdentityOutputSupported([]));
    }
```

- [ ] **Step 2: Run tests — expect fail**

```bash
rtk dotnet test ContextBulkExtension.Tests/ContextBulkExtension.Tests.csproj -v q --filter "FullyQualifiedName~EnsurePostgresIdentityOutputSupported"
```

Expected: FAIL — method missing / compile error.

- [ ] **Step 3: Implement helper**

Add to `BulkProviderHelpers.cs`:

```csharp
    /// <summary>
    /// PostgreSQL IdentityOutput pairs RETURNING rows to staging via ROW_NUMBER ordered by
    /// the identity column vs __RowIndex. That requires a single monotonic int/long identity
    /// (serial/bigserial / GENERATED … AS IDENTITY). Guid/random/composite identities are unsupported.
    /// </summary>
    public static void EnsurePostgresIdentityOutputSupported(IReadOnlyList<ColumnMetadata> identityColumns)
    {
        if (identityColumns.Count != 1)
        {
            throw new NotSupportedException(
                "PostgreSQL IdentityOutput requires a single serial/bigserial (int/long) identity column.");
        }

        var clr = Nullable.GetUnderlyingType(identityColumns[0].ClrType) ?? identityColumns[0].ClrType;
        if (clr != typeof(int) && clr != typeof(long))
        {
            throw new NotSupportedException(
                $"PostgreSQL IdentityOutput requires a single serial/bigserial identity column (int/long). Got '{clr.Name}'.");
        }
    }
```

- [ ] **Step 4: Call from Postgres upsert identity branch**

In `PostgresBulkProvider.BuildUpsertSql`, at the start of `if (writeIdentityToStaging)` (before building the WITH CTE):

```csharp
        if (writeIdentityToStaging)
        {
            BulkProviderHelpers.EnsurePostgresIdentityOutputSupported(identityColumns!);
            var identities = identityColumns!;
            // ... existing CTE SQL unchanged ...
```

Add a short comment above the call if useful:

```csharp
            // Ceiling: ROW_NUMBER pairing assumes identity assigned in __RowIndex insert order (serial/bigserial).
```

Keep the existing ponytail comment at L211–212 (concurrent unique race); do not remove it.

- [ ] **Step 5: Run unit + identity integration tests**

```bash
rtk dotnet test ContextBulkExtension.Tests/ContextBulkExtension.Tests.csproj -v q --filter "FullyQualifiedName~EnsurePostgresIdentityOutputSupported|FullyQualifiedName~PostgresBulkIdentityOutput"
```

Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add ContextBulkExtension.Core/Helpers/BulkProviderHelpers.cs \
        ContextBulkExtension.Postgres/PostgresBulkProvider.cs \
        ContextBulkExtension.Tests/BulkProviderHelpersTests.cs
git commit -m "$(cat <<'EOF'
fix: guard Postgres IdentityOutput to single int/long identity

ROW_NUMBER pairing is only valid for monotonic serial/bigserial keys;
throw NotSupportedException for Guid/composite identities instead of
silently mis-assigning ids.
EOF
)"
```

---

### Task 4: Bracket → quote rewrite for deleteScope (Issue #3)

**Files:**
- Modify: `ContextBulkExtension.Core/Helpers/BulkProviderHelpers.cs`
- Modify: `ContextBulkExtension.Postgres/PostgresBulkProvider.cs:379-381`
- Test: `ContextBulkExtension.Tests/BulkProviderHelpersTests.cs`

**Interfaces:**
- Consumes: SQL Server-shaped WHERE from `ExpressionHelper.BuildWhereClauseFromExpression` (`target.[Col]` / `target.[Col]]]` for `]` in name via `EscapeSqlIdentifier`)
- Produces: `BulkProviderHelpers.ToPostgresTargetQualified(string sqlServerStyleWhere)` — correct `"…"` quoting including `]]` unescape

- [ ] **Step 1: Write failing unit tests**

```csharp
    [Theory]
    [InlineData("target.[AccountId] = @p0", "target.\"AccountId\" = @p0")]
    [InlineData("(target.[A] = @p0 AND target.[B] = @p1)", "(target.\"A\" = @p0 AND target.\"B\" = @p1)")]
    [InlineData("target.[Col]]] = @p0", "target.\"Col]\" = @p0")]
    [InlineData("target.[Weird\"Name] = @p0", "target.\"Weird\"\"Name\" = @p0")]
    public void ToPostgresTargetQualified_RewritesBrackets(string input, string expected)
    {
        Assert.Equal(expected, BulkProviderHelpers.ToPostgresTargetQualified(input));
    }
```

Note on `Weird\"Name`: if ExpressionHelper never emits `"` inside brackets today, still escape `"` when rewriting so Postgres quoting stays valid.

- [ ] **Step 2: Run tests — expect fail**

```bash
rtk dotnet test ContextBulkExtension.Tests/ContextBulkExtension.Tests.csproj -v q --filter "FullyQualifiedName~ToPostgresTargetQualified"
```

Expected: FAIL — method missing.

- [ ] **Step 3: Implement helper**

Add to `BulkProviderHelpers.cs` (needs `using System.Text.RegularExpressions;`):

```csharp
    private static readonly Regex SqlServerBracketIdentifier = new(
        @"\[((?:\]\]|[^\]])*)\]",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Rewrites ExpressionHelper SQL Server bracket identifiers to Postgres double-quoted
    /// identifiers, unescaping ]] → ] and escaping " → "".
    /// </summary>
    public static string ToPostgresTargetQualified(string sqlServerStyleWhere)
    {
        ArgumentNullException.ThrowIfNull(sqlServerStyleWhere);
        return SqlServerBracketIdentifier.Replace(sqlServerStyleWhere, static match =>
        {
            var raw = match.Groups[1].Value.Replace("]]", "]", StringComparison.Ordinal);
            var escaped = raw.Replace("\"", "\"\"", StringComparison.Ordinal);
            return $"\"{escaped}\"";
        });
    }
```

- [ ] **Step 4: Wire Postgres provider**

Replace private method body in `PostgresBulkProvider.cs`:

```csharp
    // ExpressionHelper emits SQL Server bracket identifiers; map to Postgres quotes for deleteScope
    private static string ToPostgresTargetQualified(string sqlServerStyleWhere)
        => BulkProviderHelpers.ToPostgresTargetQualified(sqlServerStyleWhere);
```

Or delete the private wrapper and call `BulkProviderHelpers.ToPostgresTargetQualified` directly from `AdaptDeleteScopeSql`:

```csharp
    protected override string AdaptDeleteScopeSql(string sqlServerStyleWhere)
        => BulkProviderHelpers.ToPostgresTargetQualified(sqlServerStyleWhere);
```

Prefer the direct call (fewer lines).

- [ ] **Step 5: Run unit + delete-scope Postgres tests**

```bash
rtk dotnet test ContextBulkExtension.Tests/ContextBulkExtension.Tests.csproj -v q --filter "FullyQualifiedName~ToPostgresTargetQualified|FullyQualifiedName~PostgresBulkUpsertWithDeleteScope"
```

Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add ContextBulkExtension.Core/Helpers/BulkProviderHelpers.cs \
        ContextBulkExtension.Postgres/PostgresBulkProvider.cs \
        ContextBulkExtension.Tests/BulkProviderHelpersTests.cs
git commit -m "$(cat <<'EOF'
fix: unescape SQL Server ]] when adapting deleteScope to Postgres

Blind [ → \" replace broke column names containing ]; parse brackets
and emit proper Postgres quoted identifiers.
EOF
)"
```

---

### Task 5: Bundle upsert hooks into `UpsertRequest<T>` (Issue #4)

**Files:**
- Create: `ContextBulkExtension.Core/Abstractions/UpsertRequest.cs`
- Modify: `ContextBulkExtension.Core/Abstractions/BulkProviderBase.cs` (abstract hooks + call sites)
- Modify: `ContextBulkExtension.Postgres/PostgresBulkProvider.cs` (three overrides)
- Modify: `ContextBulkExtension.SqlServer/SqlServerBulkProvider.cs` (three overrides)
- Test: full regression suite (refactor only)

**Interfaces:**
- Consumes: all former ExecuteUpsert / ExecuteDeleteNotMatched / SyncIdentities parameters
- Produces:

```csharp
namespace ContextBulkExtension.Core.Abstractions;

internal sealed class UpsertRequest<T> where T : class
{
    public required DbContext Context { get; init; }
    public required DbConnection Connection { get; init; }
    public required string TargetTable { get; init; }
    public required string StagingTable { get; init; }
    public required IReadOnlyList<ColumnMetadata> Columns { get; init; }
    public required IReadOnlyList<ColumnMetadata> MatchColumns { get; init; }
    public List<string>? UpdateColumnNames { get; init; }
    public required BulkConfig Config { get; init; }
    public IReadOnlyList<ColumnMetadata>? IdentityColumns { get; init; }
    public bool NeedsIdentitySync { get; init; }
    /// <summary>True when delete must run inside ExecuteUpsert (SqlServer MERGE bundle).</summary>
    public bool BundleDeleteNotMatched { get; init; }
    public string? DeleteScopeSql { get; init; }
    public List<DbParameter>? DeleteScopeParameters { get; init; }
    public required IList<T> Entities { get; init; }
}
```

New abstract signatures on `BulkProviderBase`:

```csharp
    protected abstract Task ExecuteUpsertAsync<T>(UpsertRequest<T> request, CancellationToken cancellationToken)
        where T : class;

    protected abstract Task ExecuteDeleteNotMatchedAsync<T>(UpsertRequest<T> request, CancellationToken cancellationToken)
        where T : class;

    protected abstract Task SyncIdentitiesAsync<T>(UpsertRequest<T> request, CancellationToken cancellationToken)
        where T : class;
```

(`ExecuteDeleteNotMatchedAsync` gains type param `T` so one request type covers all hooks.)

- [ ] **Step 1: Add `UpsertRequest.cs`**

Create the file with the record/class above. Add usings:

```csharp
using System.Data.Common;
using ContextBulkExtension.Core.Helpers;
using Microsoft.EntityFrameworkCore;
```

- [ ] **Step 2: Update `BulkProviderBase` abstract methods + call site**

Replace the three abstract method declarations with the short signatures above.

In `BulkUpsertAsync`, after building `updateColumnNames` / `deleteScopeSql` / `deleteScopeParameters`, construct one request and pass it:

```csharp
                var request = new UpsertRequest<T>
                {
                    Context = context,
                    Connection = connection,
                    TargetTable = tableName,
                    StagingTable = stagingTable,
                    Columns = columns,
                    MatchColumns = matchColumns,
                    UpdateColumnNames = updateColumnNames,
                    Config = config,
                    IdentityColumns = identityColumns,
                    NeedsIdentitySync = needsIdentitySync,
                    BundleDeleteNotMatched = deleteNotMatchedBySource && BundlesDeleteInUpsert,
                    DeleteScopeSql = deleteScopeSql,
                    DeleteScopeParameters = deleteScopeParameters,
                    Entities = entities
                };

                await ExecuteUpsertAsync(request, cancellationToken);

                if (deleteNotMatchedBySource && !BundlesDeleteInUpsert)
                    await ExecuteDeleteNotMatchedAsync(request, cancellationToken);

                if (needsIdentitySync && !BundlesIdentityInUpsert)
                    await SyncIdentitiesAsync(request, cancellationToken);
```

Remove the old long argument lists at those call sites.

- [ ] **Step 3: Update Postgres overrides**

`ExecuteUpsertAsync`: read fields from `request` (`request.TargetTable`, `request.StagingTable`, …). `deleteNotMatchedBySource` on the old signature was the **bundled** flag — use `request.BundleDeleteNotMatched` (Postgres ignores it today; keep ignoring).

`ExecuteDeleteNotMatchedAsync` / `SyncIdentitiesAsync`: same field mapping; keep bodies.

Example upsert signature:

```csharp
    protected override async Task ExecuteUpsertAsync<T>(UpsertRequest<T> request, CancellationToken cancellationToken)
    {
        var upsertSql = BuildUpsertSql(
            request.TargetTable,
            request.StagingTable,
            request.Columns,
            request.MatchColumns,
            request.UpdateColumnNames,
            request.Config,
            request.IdentityColumns);
        await using var upsertCmd = CreateCommand(request.Connection, request.Context, upsertSql, request.Config.TimeoutSeconds);
        await upsertCmd.ExecuteNonQueryAsync(cancellationToken);
    }
```

- [ ] **Step 4: Update SqlServer overrides**

Map MERGE path to `request.*`. Preserve fail-loud hooks from Task 2:

```csharp
    protected override Task ExecuteDeleteNotMatchedAsync<T>(UpsertRequest<T> request, CancellationToken cancellationToken)
        => throw new NotSupportedException(
            "SqlServer bundles WHEN NOT MATCHED BY SOURCE delete into MERGE; ExecuteDeleteNotMatchedAsync must not be called.");

    protected override Task SyncIdentitiesAsync<T>(UpsertRequest<T> request, CancellationToken cancellationToken)
        => throw new NotSupportedException(
            "SqlServer bundles identity OUTPUT into MERGE; SyncIdentitiesAsync must not be called.");
```

For `ExecuteUpsertAsync`, pass `request.BundleDeleteNotMatched` into whatever previously received `deleteNotMatchedBySource`.

- [ ] **Step 5: Build**

```bash
rtk dotnet build ContextBulkExtension.sln -v q
```

Expected: `0 errors, 0 warnings`.

- [ ] **Step 6: Full regression**

```bash
rtk dotnet test ContextBulkExtension.Tests/ContextBulkExtension.Tests.csproj -v q
```

Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add ContextBulkExtension.Core/Abstractions/UpsertRequest.cs \
        ContextBulkExtension.Core/Abstractions/BulkProviderBase.cs \
        ContextBulkExtension.Postgres/PostgresBulkProvider.cs \
        ContextBulkExtension.SqlServer/SqlServerBulkProvider.cs
git commit -m "$(cat <<'EOF'
refactor: bundle upsert hook args into UpsertRequest

Cuts 14-parameter ExecuteUpsertAsync and shared delete/identity hooks
to a single request object so providers cannot mis-thread arguments.
EOF
)"
```

---

### Task 6: Document IdentityOutput performance cliff (Issue #6)

**Files:**
- Modify: `ContextBulkExtension.Core/BulkConfig.cs:50-58`
- Modify: `ContextBulkExtension.Core/DbContextBulkExtension.cs:18-21` (XML on config overload)

**Interfaces:**
- Consumes: existing `IdentityOutput` + `BulkInsertAsync` reroute (`DbContextBulkExtension.cs:30-55`)
- Produces: docs only — no runtime change

- [ ] **Step 1: Expand `BulkConfig.IdentityOutput` XML**

Replace the property doc with:

```csharp
    /// <summary>
    /// When true, syncs identity values back to the original entities after upsert (or after
    /// <c>BulkInsertAsync</c>, which routes through the staging-based upsert path when this is set).
    /// Useful when matching on non-identity columns (e.g. Email) and you need generated keys populated.
    /// - INSERT: Syncs newly generated identity values
    /// - UPDATE: Syncs existing identity values from the database to entities
    /// Adds ~10-20% overhead for tracking and mapping. On <c>BulkInsertAsync</c>, prefer the raw
    /// bulk load (SqlBulkCopy / COPY) when you do not need keys back — enabling this flag is slower
    /// than a plain insert because it creates a staging table and runs MERGE/CTE upsert.
    /// Default is false (no identity synchronization).
    /// </summary>
    public bool IdentityOutput { get; set; } = false;
```

- [ ] **Step 2: Note on `BulkInsertAsync` overload**

Update the XML summary for the `BulkInsertAsync(…, BulkConfig, …)` overload:

```csharp
    /// <summary>
    /// Performs a high-performance bulk insert of entities with custom options.
    /// When <see cref="BulkConfig.IdentityOutput"/> is true, routes through staging upsert
    /// (InsertOnly) so generated identity values can be read back.
    /// </summary>
```

- [ ] **Step 3: Build (docs only)**

```bash
rtk dotnet build ContextBulkExtension.Core/ContextBulkExtension.Core.csproj -v q
```

Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add ContextBulkExtension.Core/BulkConfig.cs \
        ContextBulkExtension.Core/DbContextBulkExtension.cs
git commit -m "$(cat <<'EOF'
docs: note IdentityOutput forces staging upsert on BulkInsertAsync

Callers expecting a raw COPY/SqlBulkCopy path need the tradeoff called
out on BulkConfig and the BulkInsertAsync overload.
EOF
)"
```

---

## Self-Review Checklist

| Spec item (review issue) | Task |
|--------------------------|------|
| #1 Connection leak before try | Task 1 |
| #2 PG identity ROW_NUMBER assumption | Task 3 |
| #3 Bracket replace / `]]` | Task 4 |
| #4 Parameter explosion / UpsertRequest | Task 5 |
| #5 SqlServer no-op hooks | Task 2 |
| #6 IdentityOutput docs | Task 6 |

- No TBD/placeholder steps.
- Task 5 signatures match Task 2 throws after refactor.
- Paths use `Postgres` / `PostgresBulkProvider` (not stale `PostgreSql` names in the review doc).
- Issue #2 text corrects review’s CACHE-gap overclaim: guard targets non-monotonic identity types only.

## Suggested fix order (matches review)

1. Task 1 — leak (pre-merge)
2. Task 2 — fail-loud (cheap)
3. Task 3 — identity guard
4. Task 4 → 5 → 6 — correctness polish, refactor, docs
