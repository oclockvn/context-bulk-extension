# BulkUpsertAsync + IdentityOutput: identity not synced when all rows already exist and no UPDATE runs

**Status:** Validated (branch `bugfix/upsert-single-item`, code review + regression tests added)

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
// Actual:   items[0].Id == 0
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

`BuildMergeSql` only emits `WHEN MATCHED` when `!InsertOnly` **and** `updateColumns.Count > 0` (~514–545).

**Extra blast radius:** `BulkInsertAsync` with `IdentityOutput` redirects to `BulkUpsertInternalAsync` with `InsertOnly = true` (~53–77). Pure inserts (PK=0) never match, so inserts still work; the bug hits upsert InsertOnly / ensure-exists paths.

## Why “1 item” matters

Mixed batch (1 exist + 1 new): OUTPUT has an insert row → looks “half OK”.  
Only existing / no update: **empty OUTPUT** → total IdentityOutput miss.

## Validation

| Check | Result |
|-------|--------|
| Code path review | Confirmed — `WHEN MATCHED` omitted under `InsertOnly`; sync reads OUTPUT only |
| Regression tests added | `BulkUpsertAsync_WithIdentityOutputAndInsertOnly_SingleExisting_ShouldSyncId`, `..._AllExisting_ShouldSyncAllIds` in [`BulkUpsertTests.cs`](../ContextBulkExtension.Tests/BulkUpsertTests.cs) |
| Test run (local) | Compiles; requires Docker (Testcontainers). When Docker available, tests **expected to fail** on `Assert.Equal(dbId, upsertUsers[0].Id)` with actual `0` |

## Gap in tests (before fix)

`BulkUpsertAsync_WithIdentityOutputAndInsertOnly_ShouldSyncOnlyInserted` uses 1 exist + 1 new; asserts **only** the new Id. No case: **all matched + no UPDATE + IdentityOutput**.

## Expected behavior

With `IdentityOutput=true`, matched rows still get identity back even when no column UPDATE.

## Proposed solutions (max 3)

### Solution 1 — Dummy `WHEN MATCHED` when `IdentityOutput` and no real update (recommended)

When `IdentityOutput && identityColumns.Count > 0` and (`InsertOnly` or `updateColumns.Count == 0`), emit:

```sql
WHEN MATCHED THEN UPDATE SET @dummy = 0
```

Forces OUTPUT `UPDATE` rows so existing sync loop works unchanged.

| Pros | Cons |
|------|------|
| Tiny change in `BuildMergeSql` only | Dummy UPDATE may fire UPDATE triggers / bump rowversion |
| Reuses OUTPUT + existing reader loop | Extra write intent vs pure no-op |
| Covers InsertOnly + empty update set | Must avoid invalid `SET col = col` on computed/rowversion cols |

### Solution 2 — Post-MERGE SELECT for rows missing from OUTPUT

After MERGE reader, if any entity still has default identity, `SELECT` join temp→target on match keys, map `__RowIndex` → Id.

| Pros | Cons |
|------|------|
| No dummy UPDATE / no trigger side effects | Second round-trip; more code paths |
| True no-op for matched rows | Must keep temp table until after SELECT |
| Clear semantics | Easy to get wrong with composite match / value converters |

### Solution 3 — Split path: INSERT-only MERGE + separate identity lookup

Keep MERGE insert-only; for `IdentityOutput`, always `SELECT` identities for all source rows via temp join (ignore `$action`). Optionally skip OUTPUT for InsertOnly.

| Pros | Cons |
|------|------|
| One consistent sync mechanism for InsertOnly | Bigger refactor; may duplicate sync logic |
| Avoids MERGE OUTPUT quirks | Extra SQL on every IdentityOutput call |
| Fits `BulkInsertAsync` IdentityOutput redirect | Overkill if only InsertOnly edge needed |

**Recommendation:** Solution 1 for a follow-up fix PR. Use Solution 2 if UPDATE triggers / rowversion must not fire on ensure-exists.

## Severity

Medium — silent wrong Id on common “ensure exists / InsertOnly” path; easy to miss in mixed batches.
