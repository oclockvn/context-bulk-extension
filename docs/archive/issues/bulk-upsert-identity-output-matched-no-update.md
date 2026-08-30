# BulkUpsertAsync + IdentityOutput: identity not synced when all rows already exist and no UPDATE runs

**Status:** Fixed (Sol 1 on branch `bugfix/upsert-single-item`)

## Summary

Upsert a collection where **every** source row already matches the target, and the MERGE performs **no** UPDATE (`InsertOnly`, empty update column set, or only match/identity columns left). The call succeeds, but entity identity properties stay at their default (`0`). Worst with a **1-item** collection — zero OUTPUT rows.

## Repro

```csharp
// DB already has Email = "a@test.com"
var items = new List<UserEntity>
{
    new() { Email = "a@test.com", Username = "x", /* Id = 0 */ }
};

await context.BulkUpsertAsync(
    items,
    matchOn: x => x.Email,
    config: new BulkConfig { InsertOnly = true, IdentityOutput = true });

// Expected: items[0].Id == DB id
// Actual (before fix): items[0].Id == 0
```

Same with a normal upsert if `BuildMergeSql` skips `WHEN MATCHED` (`updateColumns.Count == 0`).

## Root cause

```sql
MERGE ...
ON match
-- WHEN MATCHED omitted when InsertOnly OR no update cols
WHEN NOT MATCHED BY TARGET THEN INSERT ...
OUTPUT source.__RowIndex, INSERTED.Id, $action
```

| Path | `WHEN MATCHED` | OUTPUT row? | Identity sync |
|------|----------------|-------------|---------------|
| INSERT new | n/a | yes (`INSERT`) | works |
| UPDATE existing | present | yes (`UPDATE`) | works |
| Match + no update | **omitted** | **no** | **broken** |

Sync loop only runs on OUTPUT rows ([`DbContextBulkExtension.cs`](../ContextBulkExtension/DbContextBulkExtension.cs) ~381–416). No action → no row → Id never set.

`BuildMergeSql` only emits a real `WHEN MATCHED` when `!InsertOnly` **and** `updateColumns.Count > 0` (~514–545).

**Extra blast radius:** `BulkInsertAsync` with `IdentityOutput` redirects to `BulkUpsertInternalAsync` with `InsertOnly = true` (~53–77). Pure inserts (PK=0) never match, so inserts still work; the bug hits upsert InsertOnly / ensure-exists paths.

## Why “1 item” matters

Mixed batch (1 exist + 1 new): OUTPUT has an insert row → looks “half OK”.  
Only existing / no update: **empty OUTPUT** → total IdentityOutput miss.

## Fix (Solution 1 — implemented)

When `IdentityOutput && identityColumns.Count > 0` and no real `WHEN MATCHED` was added, emit:

```sql
DECLARE @dummy INT;
...
WHEN MATCHED THEN UPDATE SET @dummy = 0
```

Forces OUTPUT `UPDATE` rows so existing sync loop works unchanged. See `needsDummyMatchedForIdentityOutput` in [`DbContextBulkExtension.cs`](../ContextBulkExtension/DbContextBulkExtension.cs).

### Limitation

Dummy `WHEN MATCHED` UPDATE can fire AFTER UPDATE triggers on the target table. MERGE `OUTPUT` without `INTO` also fails when the target has triggers. Workaround: avoid `IdentityOutput` on trigger tables for ensure-exists paths, or later adopt Solution 2 (post-MERGE SELECT).

## Validation

| Check | Result |
|-------|--------|
| Code path review | Confirmed — dummy MATCHED when IdentityOutput and no real update |
| Regression tests | `..._InsertOnly_SingleExisting_ShouldSyncId`, `..._AllExisting_ShouldSyncAllIds`, mixed batch asserts matched Id, `..._EmptyUpdateColumns_ShouldSyncId` in [`BulkUpsertTests.cs`](../ContextBulkExtension.Tests/BulkUpsertTests.cs) |
| Test run | Requires Docker (Testcontainers) |

## Expected behavior

With `IdentityOutput=true`, matched rows still get identity back even when no column UPDATE.

## Alternate solutions (not implemented)

### Solution 2 — Post-MERGE SELECT for rows missing from OUTPUT

After MERGE reader, if any entity still has default identity, `SELECT` join temp→target on match keys, map `__RowIndex` → Id. Prefer if UPDATE triggers / rowversion must not fire on ensure-exists.

### Solution 3 — Split path: INSERT-only MERGE + separate identity lookup

Keep MERGE insert-only; for `IdentityOutput`, always `SELECT` identities for all source rows via temp join. Bigger refactor.

## Severity

Medium — silent wrong Id on common “ensure exists / InsertOnly” path; easy to miss in mixed batches.
