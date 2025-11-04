# EF Core Bulk Extension

## Usage

```cs
DbContext db = GetYourDbContext();
await db.BulkInsertAsync(entities);
```

## Roadmap

- [ ] BulkMerge
- [x] Identity output
