# Architecture

## Request flow

All four public entry points live on `DbContextBulkExtension` (static class, extension
methods on `DbContext`).

```
db.BulkInsertAsync(entities [, config])
db.BulkUpsertAsync(entities [, matchOn] [, updateColumns] [, config])
db.BulkUpsertWithDeleteScopeAsync(entities [, matchOn] [, updateColumns] [, deleteScope] [, config])
        │
        ▼
DbContextBulkExtension
  • null / empty-list guards
  • BulkInsertAsync with config.IdentityOutput == true is rewritten into an
    InsertOnly upsert so generated keys can be read back (slower path — see gotchas)
        │
        ▼
BulkProviderRegistry.Resolve(context.Database.GetDbConnection())
  • lazily loads ContextBulkExtension.SqlServer.dll / ContextBulkExtension.Postgres.dll
    from AppContext.BaseDirectory and calls their *BulkProviderRegistration.Initialize()
  • returns the first IBulkProvider whose Supports(connection) is true
  • throws InvalidOperationException (with remediation text) if none match
        │
        ▼
BulkProviderBase  (template method — shared orchestration)
        │
        ▼
SqlServerBulkProvider / PostgresBulkProvider  (dialect hooks)
```

### `BulkProviderBase.BulkInsertAsync`

1. `Supports` re-check.
2. Column metadata (identity **excluded**).
3. Open connection → `BulkLoadToTargetAsync` → close connection.
4. Provider exceptions wrapped via `IsProviderException` / `WrapProviderException`.

### `BulkProviderBase.BulkUpsertAsync`

1. `Supports` re-check.
2. Resolve match columns (`matchOn` expression, or primary key by default).
3. Column metadata (identity **included**).
4. If `OwnsTransactionWhenMissing` and no ambient EF transaction → begin one.
5. Create staging table (`BuildCreateStagingSql`; includes a row-index column when identity
   sync is needed).
6. `BulkLoadToStagingAsync` — bulk-load the batch into staging.
7. Build an `UpsertRequest<T>` (target/staging table names, columns, match columns, update
   column names, compiled `deleteScope` WHERE clause + parameters, identity columns).
8. `ExecuteUpsertAsync` — the provider's MERGE / staged upsert.
9. If deleting unmatched rows and `!BundlesDeleteInUpsert` → `ExecuteDeleteNotMatchedAsync`.
10. If identity sync needed and `!BundlesIdentityInUpsert` → `SyncIdentitiesAsync`.
11. Commit owned transaction (if any); always drop the staging table in `finally`.

## `BulkProviderBase` hooks — what a new provider implements

| Member | Kind | SQL Server | PostgreSQL |
|--------|------|-----------|------------|
| `Supports(DbConnection)` | capability | `connection is SqlConnection` | `connection is NpgsqlConnection` |
| `OwnsTransactionWhenMissing` | flag | `false` | `true` (COPY + staged DML need one tx) |
| `BundlesDeleteInUpsert` | flag | `true` (`WHEN NOT MATCHED BY SOURCE` in MERGE) | `false` (separate `DELETE`) |
| `BundlesIdentityInUpsert` | flag | `true` (`OUTPUT INSERTED.*` in MERGE) | `false` (separate identity sync) |
| `QuoteIdentifier` | dialect | `[ident]` | `"ident"` |
| `GetQualifiedTableName<T>` | dialect | schema-qualified table name | same |
| `NewStagingTableName` | dialect | `#tmp_<guid>` | `temp_staging_<guid>` |
| `BuildCreateStagingSql` | dialect | `CREATE TABLE #... ` | `CREATE TEMP TABLE ...` |
| `BuildDropStagingSql` | dialect | `IF OBJECT_ID(...) DROP TABLE` | `DROP TABLE IF EXISTS` |
| `BulkLoadToTargetAsync` | bulk load | `SqlBulkCopy` → target | binary `COPY` → target |
| `BulkLoadToStagingAsync` | bulk load | `SqlBulkCopy` (KeepIdentity) → staging | binary `COPY` → staging |
| `ExecuteUpsertAsync` | dialect | one `MERGE` statement | staged `UPDATE ... FROM` then `INSERT ... SELECT ... WHERE NOT EXISTS` |
| `ExecuteDeleteNotMatchedAsync` | dialect | throws (bundled in MERGE) | `DELETE ... WHERE NOT EXISTS (SELECT 1 FROM staging ...)` + optional scope |
| `SyncIdentitiesAsync` | dialect | throws (bundled in MERGE) | read generated keys back into entities |
| `AdaptDeleteScopeSql(string)` | dialect | pass-through (clause already SQL-Server-style) | rewrite `[x]` → `"x"` etc. |
| `CreateCommand` | dialect | `SqlCommand` bound to the EF `SqlTransaction` | `NpgsqlCommand` bound to the EF tx |
| `IsProviderException` | error map | `ex is SqlException` | `ex is PostgresException` / `NpgsqlException` |
| `WrapProviderException` | error map | `InvalidOperationException` with entity + message | same shape |

The `deleteScope` predicate is compiled once (`ExpressionHelper.BuildWhereClauseFromExpression`)
into a **SQL-Server-flavored** WHERE clause + `DbParameter` list; each provider's
`AdaptDeleteScopeSql` translates it to its own dialect.

## Provider auto-discovery

`BulkProviderRegistry.EnsureProviderAssembliesLoaded` runs once, lazily, on first `Resolve`.
`ModuleInitializer` in a library is unreliable, so instead it:

1. looks for an already-loaded `ContextBulkExtension.SqlServer` / `.Postgres` assembly,
2. else `Assembly.LoadFrom(AppContext.BaseDirectory + "<name>.dll")`,
3. invokes the assembly's `*BulkProviderRegistration.Initialize()` which calls
   `BulkProviderRegistry.Register`.

**Trimmed / AOT / single-file** apps must keep the provider assembly deployed beside the
executable or `Resolve` throws.

## Gotchas

- **`BulkInsertAsync` + `IdentityOutput = true` is slow.** It is rewritten into a staging
  MERGE/CTE upsert so keys can be returned — roughly 10–20% slower than a plain
  `SqlBulkCopy` / `COPY`. Only set it when you actually need the generated keys back.
- **`BulkUpsertWithDeleteScopeAsync` with `deleteScope: null` deletes every target row not
  in the source batch.** Always pass a scoped predicate unless you truly mean "make the
  table match this batch."
- **PostgreSQL custom `matchOn` needs a unique index/constraint** on those columns for
  correct upsert semantics. Primary-key match works out of the box.
- **SQL Server bulk ops require a real `SqlTransaction`.** If an ambient EF transaction
  exists it must be a `SqlTransaction` (not, e.g., a `TransactionScope` wrapper) or the
  provider throws.
- **`BulkConfig` flags `CheckConstraints`, `FireTriggers`, `EnableStreaming`,
  `UseTableLock` are SQL Server only.** The PostgreSQL provider ignores them silently.
- Entities are matched back to identity output by a **row-index column** injected into the
  staging table, not by position in the input list.

## Adding a new provider (e.g. MySQL, SQLite)

1. `ContextBulkExtension.MySql/ContextBulkExtension.MySql.csproj` + `.Net8.csproj` twin
   (copy an existing provider pair; `Directory.Build.props` handles the rest).
2. `internal sealed class MySqlBulkProvider : BulkProviderBase` — implement the hook table
   above.
3. `internal static class MySqlBulkProviderRegistration { internal static void Initialize() => BulkProviderRegistry.Register(new MySqlBulkProvider()); }`
4. Add a `TryLoadAndRegister("ContextBulkExtension.MySql", …)` line in
   `BulkProviderRegistry.EnsureProviderAssembliesLoaded`.
5. Add a `MySqlDatabaseFixture` (+ `BULK_TEST_MYSQL_CONNECTION` override) and a test class.
6. Add the package to `scripts/publish-nuget.ps1` and the CI publish workflow.
