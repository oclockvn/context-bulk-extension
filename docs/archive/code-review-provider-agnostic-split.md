# Code Review — `feature/provider-agnostic-split`

**Scope:** Core template-method base + SqlServer/PostgreSql provider ports + shared helpers.
**Base:** `master` → **Head:** `184abff`
**Build:** 5 projects, 0 errors, 0 warnings. **Tests:** net10 54/54, net8 54/54 (Docker up).
**Verdict:** merge-ready. Only Issue #1 (connection leak window) is worth fixing before merge; the rest are hardening/readability.

Severity legend: 🔴 critical · 🟡 risk · 🔵 design/nit

---

## Critical

None. Orchestration is correct: transaction begin/commit/rollback/dispose ordering is sound, `deleteScope` values are parameterized (no SQL injection), and all identity paths are covered by integration tests.

---

## Issue #1 — 🟡 Connection leak window before `try`

**Location:** `ContextBulkExtension.Core/Abstractions/BulkProviderBase.cs:166-172`

**Problem:** The connection is opened and the owned transaction is begun *outside* the guarding `try`. If `BeginTransactionAsync` throws (PostgreSQL owned-transaction path), the connection is left open and is never closed — there is no `finally` covering that window.

**Proof:**

```166:174:ContextBulkExtension.Core/Abstractions/BulkProviderBase.cs
        await context.Database.OpenConnectionAsync(cancellationToken);

        IDbContextTransaction? ownedTransaction = null;
        if (OwnsTransactionWhenMissing && context.Database.CurrentTransaction == null)
            ownedTransaction = await context.Database.BeginTransactionAsync(cancellationToken);

        try
        {
```

The matching `finally` that calls `CloseConnectionAsync` (L269-274) only runs if control reaches the `try` at L172. An exception from L170 skips it.

**Suggestion:** Move the open + begin inside the `try`, so the outer `finally` always closes:

```csharp
IDbContextTransaction? ownedTransaction = null;
await context.Database.OpenConnectionAsync(cancellationToken);
try
{
    if (OwnsTransactionWhenMissing && context.Database.CurrentTransaction == null)
        ownedTransaction = await context.Database.BeginTransactionAsync(cancellationToken);
    // ... rest unchanged ...
}
finally
{
    if (ownedTransaction != null)
        await ownedTransaction.DisposeAsync();
    await context.Database.CloseConnectionAsync();
}
```

---

## Issue #2 — 🟡 PostgreSQL identity pairing assumes serial monotonic with insert order

**Location:** `ContextBulkExtension.PostgreSql/PostgreSqlBulkProvider.cs:279-299`

**Problem:** New-row identities are paired back to staging rows by matching two independent `ROW_NUMBER()` sequences: one ordered by the identity column, the other by `__RowIndex`. This is only correct when the DB-generated identity is assigned in the same ascending order as the insert (`ORDER BY __RowIndex`). It holds for `serial`/`int identity`, but breaks **silently** for non-monotonic identities (Guid PK, sequences with `CACHE` gaps across sessions, composite identity).

**Proof:**

```279:299:ContextBulkExtension.PostgreSql/PostgreSqlBulkProvider.cs
            sql.AppendLine("ins_ordered AS (");
            sql.Append("    SELECT ");
            sql.Append(string.Join(", ", identities.Select(c => QuoteIdentifier(c.ColumnName))));
            sql.Append(", ROW_NUMBER() OVER (ORDER BY ");
            sql.Append(QuoteIdentifier(identities[0].ColumnName));
            sql.AppendLine(") AS rn FROM ins");
            sql.AppendLine("),");
            sql.AppendLine("src_ordered AS (");
            sql.AppendLine($"    SELECT {rowIndex}, ROW_NUMBER() OVER (ORDER BY {rowIndex}) AS rn FROM to_insert");
            sql.AppendLine(")");
            sql.AppendLine($"UPDATE {sourceTableName} AS source SET");
            for (int i = 0; i < identities.Count; i++)
            {
                var col = QuoteIdentifier(identities[i].ColumnName);
                sql.Append($"    {col} = ins_ordered.{col}");
                sql.AppendLine(i < identities.Count - 1 ? "," : string.Empty);
            }
```

The existing ceiling is acknowledged in the code comment:

```211:212:ContextBulkExtension.PostgreSql/PostgreSqlBulkProvider.cs
        // ponytail: UPDATE+INSERT avoids ON CONFLICT + identity OVERRIDING when Id=0 on new rows.
        // Ceiling: not one-statement atomic vs concurrent writers (unique race); owned txn covers multi-statement only.
```

**Suggestion:** Guard the assumption instead of failing silently — when `IdentityOutput` is requested, assert a single integral (`int`/`long`) identity column and throw a clear `NotSupportedException` otherwise (e.g. "PostgreSQL IdentityOutput requires a single serial/bigserial identity column"). Document the serial-ordering requirement on the method.

---

## Issue #3 — 🔵 `AdaptDeleteScopeSql` naive bracket replacement

**Location:** `ContextBulkExtension.PostgreSql/PostgreSqlBulkProvider.cs:380-381`

**Problem:** SQL Server bracket identifiers are converted to Postgres double-quotes with a blind character replace. `ExpressionHelper` escapes a `]` inside a column name to `]]`, producing `[Col]]]`; this replace then yields `"Col"""` — malformed quoting. Values are parameterized, so this is *not* an injection vector; only pathological column names containing `]` break.

**Proof:**

```380:381:ContextBulkExtension.PostgreSql/PostgreSqlBulkProvider.cs
    private static string ToPostgresTargetQualified(string sqlServerStyleWhere)
        => sqlServerStyleWhere.Replace("[", "\"", StringComparison.Ordinal).Replace("]", "\"", StringComparison.Ordinal);
```

Escaping source that feeds it:

```246:247:ContextBulkExtension.Core/Helpers/EntityMetadataHelper.cs
        // Replace ] with ]] (SQL Server escape sequence for brackets)
        return $"[{identifier.Replace("]", "]]")}]";
```

**Suggestion:** Low likelihood; either document the "no `]` in column names" constraint, or build the delete-scope WHERE clause dialect-agnostically (emit column tokens the provider quotes itself) rather than string-rewriting SQL-Server-shaped SQL.

---

## Issue #4 — 🔵 `ExecuteUpsertAsync` parameter explosion

**Location:** `ContextBulkExtension.Core/Abstractions/BulkProviderBase.cs:46-83`

**Problem:** `ExecuteUpsertAsync` takes 14 parameters; the delete/sync hooks take 8-9. This is template-method leakage — PostgreSQL ignores several args (`entities`, delete args in the upsert hook) while SqlServer ignores others. High churn surface and easy to mis-thread an argument.

**Proof:**

```46:61:ContextBulkExtension.Core/Abstractions/BulkProviderBase.cs
    protected abstract Task ExecuteUpsertAsync<T>(
        DbContext context,
        DbConnection connection,
        string targetTable,
        string stagingTable,
        IReadOnlyList<ColumnMetadata> columns,
        IReadOnlyList<ColumnMetadata> matchColumns,
        List<string>? updateColumnNames,
        BulkConfig config,
        IReadOnlyList<ColumnMetadata>? identityColumns,
        bool needsIdentitySync,
        bool deleteNotMatchedBySource,
        string? deleteScopeSql,
        List<DbParameter>? deleteScopeParameters,
        IList<T> entities,
        CancellationToken cancellationToken) where T : class;
```

**Suggestion:** Bundle the shared state into a single `UpsertRequest<T>` record (context, connection, table names, columns, matchColumns, config, identity info, delete info, entities) and pass that to all hooks. Cuts signatures to `(UpsertRequest<T> request, CancellationToken ct)` and makes per-provider usage explicit.

---

## Issue #5 — 🔵 SqlServer no-op overrides mask the contract

**Location:** `ContextBulkExtension.SqlServer/SqlServerBulkProvider.cs:219-241`

**Problem:** `ExecuteDeleteNotMatchedAsync` and `SyncIdentitiesAsync` return `Task.CompletedTask` because SqlServer bundles both into the MERGE. The base already guards these calls with `!BundlesDeleteInUpsert` / `!BundlesIdentityInUpsert`, so for SqlServer they are dead code. If a flag or override ever desyncs from the bundling behavior, deletes/identity sync are **silently skipped** — a data-loss class of bug with no error.

**Proof:**

```219:241:ContextBulkExtension.SqlServer/SqlServerBulkProvider.cs
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
        => Task.CompletedTask;

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
        => Task.CompletedTask;
```

Base guard that makes them unreachable:

```213:239:ContextBulkExtension.Core/Abstractions/BulkProviderBase.cs
                if (deleteNotMatchedBySource && !BundlesDeleteInUpsert)
                {
                    await ExecuteDeleteNotMatchedAsync(...);
                }

                if (needsIdentitySync && !BundlesIdentityInUpsert)
                {
                    await SyncIdentitiesAsync(...);
                }
```

**Suggestion:** Make the invariant fail loud: `throw new NotSupportedException("SqlServer bundles delete/identity into MERGE; this hook must not be called.");` instead of `Task.CompletedTask`. If a future flag change reaches them, it surfaces immediately rather than dropping data.

---

## Issue #6 — 🔵 `BulkInsertAsync` + `IdentityOutput` silently reroutes through upsert

**Location:** `ContextBulkExtension.Core/DbContextBulkExtension.cs:31-55`

**Problem:** Correct behavior, but a surprising performance cliff: a caller invoking `BulkInsertAsync` with `IdentityOutput = true` expects a plain `COPY`/`SqlBulkCopy`, but instead gets the full staging + MERGE/CTE upsert path (create staging, load, upsert, identity sync). No doc signals the tradeoff.

**Proof:**

```30:55:ContextBulkExtension.Core/DbContextBulkExtension.cs
        // If identity output is requested, use BulkUpsert with InsertOnly mode
        if (config.IdentityOutput)
        {
            var upsertConfig = new BulkConfig
            {
                ...
                IdentityOutput = true,
                InsertOnly = true
            };

            await BulkUpsertInternalAsync(
                context, entities, matchOn: null, updateColumns: null,
                deleteScope: null, upsertConfig, deleteNotMatchedBySource: false, cancellationToken);
            return;
        }
```

**Suggestion:** Add one line to the `BulkConfig.IdentityOutput` XML doc: enabling it on `BulkInsertAsync` routes through the staging-based upsert (slower than a raw bulk load) so generated keys can be read back.

---

## Performance

No hot-path regressions from the refactor.

- `EntityMetadataHelper` caches per `(EntityType, ContextType)` via `ConcurrentDictionary`, so repeated `GetColumnMetadata` / `ResolveMatchColumns` calls are O(1) after warm-up (`EntityMetadataHelper.cs:163-170`).
- `FilterUpdateColumns` allocates two `HashSet`s per upsert — negligible against DB I/O (`BulkProviderHelpers.cs:14-25`).
- `BulkProviderRegistry.Resolve` takes a lock and linearly scans 2 providers per call — trivial (`BulkProviderRegistry.cs:29-36`).

---

## Suggested fix order

1. **Issue #1** — connection leak window (pre-merge).
2. **Issue #5** — no-op overrides → explicit `NotSupportedException` (cheap hardening).
3. **Issue #2** — PostgreSQL identity guard (prevents silent wrong keys).
4. **Issues #3, #4, #6** — readability/docs, no rush.
