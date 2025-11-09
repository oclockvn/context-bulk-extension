# EF Core Bulk Extension

High-performance bulk operations for Entity Framework Core using SQL Server's SqlBulkCopy.

## Usage

### 1. Insert Only

```cs
DbContext db = GetYourDbContext();
await db.BulkInsertAsync(entities);
```

### 2. Upsert with Default Compare

```cs
DbContext db = GetYourDbContext();
await db.BulkMergeAsync(entities);
```

Compares by primary key and updates all properties.

### 3. Upsert with Advanced Usage

```cs
DbContext db = GetYourDbContext();

await db.BulkMergeAsync(
    entities,
    onCompare: x => new { x.Email, x.Username },
    updateProperties: x => new { x.LastLogin, x.Status }
);
```

Compares by Email and Username, updates only LastLogin and Status properties.

## Roadmap

- [ ] BulkMerge
- [x] Identity output
- [ ] Benchmark with large dataset and table with 20+ columns
