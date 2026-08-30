# samples/

Runnable examples. Not part of the release build and not in the package solutions
(`ContextBulkExtension.sln` / `.Net8.sln`).

## QuickStart

End-to-end exercise of all four public entry points against a real PostgreSQL database:

```bash
dotnet run --project samples/QuickStart
```

- Needs a **running Docker daemon** — it starts a throwaway `postgres:16-alpine`
  container via Testcontainers. First run pulls the image.
- To use an existing database instead: `QUICKSTART_PG_CONNECTION=Host=...;Database=...;Username=...;Password=...`
- Exit code `0` = every assertion passed. Use it to sanity-check a change without
  running the full xUnit suite.

What it demonstrates:

| Step | API | Point |
|------|-----|-------|
| 1 | `BulkInsertAsync` | raw fast load, no keys back |
| 2 | `BulkInsertAsync` + `BulkConfig { IdentityOutput = true }` | generated keys written back to entities (slower path) |
| 3 | `BulkUpsertAsync(matchOn, updateColumns)` | match on a non-PK unique column, update a subset |
| 4 | `BulkUpsertWithDeleteScopeAsync(matchOn, deleteScope)` | reconcile a scoped slice of the table to a batch |
