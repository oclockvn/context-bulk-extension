# EF Core Bulk Extension

High-performance bulk operations for Entity Framework Core (SQL Server and PostgreSQL).

## Breaking change (provider packages)

Package id `ContextBulkExtension` is **removed**. Install the provider you use:

| Provider | Package |
|----------|---------|
| SQL Server | `ContextBulkExtension.SqlServer` |
| PostgreSQL | `ContextBulkExtension.Postgres` |

Core is embedded in each provider package (not installed separately). Public API is unchanged: `BulkInsertAsync`, `BulkUpsertAsync`, `BulkUpsertWithDeleteScopeAsync`, `BulkConfig`.

## Installation

```bash
# SQL Server — EF Core 8.x (.NET 8)
dotnet add package ContextBulkExtension.SqlServer --version 8.0.20

# SQL Server — EF Core 10.x (.NET 10)
dotnet add package ContextBulkExtension.SqlServer --version 10.0.20

# PostgreSQL — EF Core 8.x (.NET 8)
dotnet add package ContextBulkExtension.Postgres --version 8.0.20

# PostgreSQL — EF Core 10.x (.NET 10)
dotnet add package ContextBulkExtension.Postgres --version 10.0.20
```

Version prefix matches EF Core major: `8.x` → net8 / EF8, `10.x` → net10 / EF10.

## How it works

- **SQL Server:** `SqlBulkCopy` + `MERGE` (temp staging)
- **PostgreSQL:** binary `COPY` + staging `UPDATE`/`INSERT` (+ optional `DELETE NOT EXISTS` for delete-scope)

Postgres note: custom `matchOn` columns should have a unique index/constraint when you rely on uniqueness semantically (PK match works by default).

## Usage

### 1. Insert Only

```cs
DbContext db = GetYourDbContext();
await db.BulkInsertAsync(entities);
```

### 2. Upsert with Default Compare

```cs
DbContext db = GetYourDbContext();
await db.BulkUpsertAsync(entities);
```

Compares by primary key and updates all non-key properties.

### 3. Upsert with Advanced Usage

```cs
DbContext db = GetYourDbContext();

await db.BulkUpsertAsync(
    entities,
    matchOn: x => new { x.Email, x.Username },
    updateColumns: x => new { x.LastLogin, x.Status }
);
```

### 4. Upsert with deletion

```cs
DbContext db = GetYourDbContext();

await db.BulkUpsertWithDeleteScopeAsync(
    entities,
    matchOn: x => new { x.AccountId, x.Metric, x.Date },
    deleteScope: x => x.AccountId == 123 && x.Category == "Energy"
);
```

**Warning:** When `deleteScope` is `null`, ALL target rows not present in the source batch are deleted. Prefer a scoped predicate.

**Parameters:**
- `matchOn`: match columns (default: primary key)
- `updateColumns`: columns to update on match (default: all non-key)
- `deleteScope`: optional filter for which unmatched target rows may be deleted

## Package layout

- `ContextBulkExtension.SqlServer` — SQL Server provider (embeds Core)
- `ContextBulkExtension.Postgres` — PostgreSQL provider (embeds Core)
- `ContextBulkExtension.Core` — shared API + metadata (**internal project**, not published)

## Publishing

```powershell
.\scripts\publish-nuget.ps1 -DryRun
.\scripts\publish-nuget.ps1 -LocalPush   # needs NUGET_API_KEY
```

See [PUBLISH.md](PUBLISH.md) for versioning, CI tags, and other flags.

## Roadmap

- ~~BulkMerge~~ cancelled, use BulkUpsert instead
- [x] Identity output
- [x] Upsert with deletion
- [x] PostgreSQL provider
- [ ] Benchmark with large dataset and table with 20+ columns
