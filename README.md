# EF Core Bulk Extension

High-performance bulk operations for Entity Framework Core using SQL Server's SqlBulkCopy.

## Installation

Install the NuGet package that matches your EF Core version:

[![Downloads](https://img.shields.io/nuget/dt/ContextBulkExtension.SqlServer.v8)](https://www.nuget.org/packages/ContextBulkExtension.SqlServer.v8/)

```bash
# For EF Core 8.x
dotnet add package ContextBulkExtension.SqlServer.v8
```

## Package Structure

This library provides separate NuGet packages for different EF Core versions and providers:

- **ContextBulkExtension.SqlServer.v8** - For Entity Framework Core 8.x with SQL Server
- **ContextBulkExtension.SqlServer.v9** - For Entity Framework Core 9.x with SQL Server (coming soon)
- **ContextBulkExtension.Postgres.v8** - For Entity Framework Core 8.x with PostgreSQL (coming soon)

Each package pins a specific EF Core version to ensure compatibility. Choose the package that matches your EF Core version.

## Usage

### 1. Insert Only

```cs
DbContext db = GetYourDbContext();
await db.BulkInsertAsync(entities);
```

Uses SQL Server's `SqlBulkCopy` for high-performance bulk inserts. No SQL statement is generated - data is streamed directly to the server using the binary protocol.

### 2. Upsert with Default Compare

```cs
DbContext db = GetYourDbContext();
await db.BulkUpsertAsync(entities);
```

Compares by primary key and updates all non-key properties.

Generated sql:

```sql
-- Creates temporary staging table
CREATE TABLE #TempStaging_... (
    [Id] INT,
    [Name] NVARCHAR(200),
    [Value] INT,
    [CreatedAt] DATETIME2
);

-- Bulk insert into staging table (using SqlBulkCopy)

-- MERGE statement
MERGE [SimpleEntities] AS target
USING #TempStaging_... AS source
ON target.[Id] = source.[Id]
WHEN MATCHED THEN
    UPDATE SET [Name] = source.[Name], [Value] = source.[Value], [CreatedAt] = source.[CreatedAt]
WHEN NOT MATCHED BY TARGET THEN
    INSERT ([Name], [Value], [CreatedAt])
    VALUES (source.[Name], source.[Value], source.[CreatedAt]);
```

### 3. Upsert with Advanced Usage

```cs
DbContext db = GetYourDbContext();

await db.BulkUpsertAsync(
    entities,
    matchOn: x => new { x.Email, x.Username },
    updateColumns: x => new { x.LastLogin, x.Status }
);
```

Compares by Email and Username, updates only LastLogin and Status properties.

Generated sql:

```sql
-- Creates temporary staging table
CREATE TABLE #TempStaging_... (
    [Id] INT,
    [Email] NVARCHAR(255),
    [Username] NVARCHAR(100),
    [FirstName] NVARCHAR(100),
    [LastName] NVARCHAR(100),
    [LastLogin] DATETIME2,
    [Status] NVARCHAR(50),
    [RegisteredAt] DATETIME2
);

-- Bulk insert into staging table (using SqlBulkCopy)

-- MERGE statement with custom match and update columns
MERGE [UserEntities] AS target
USING #TempStaging_... AS source
ON target.[Email] = source.[Email] AND target.[Username] = source.[Username]
WHEN MATCHED THEN
    UPDATE SET [LastLogin] = source.[LastLogin], [Status] = source.[Status]
WHEN NOT MATCHED BY TARGET THEN
    INSERT ([Email], [Username], [FirstName], [LastName], [LastLogin], [Status], [RegisteredAt])
    VALUES (source.[Email], source.[Username], source.[FirstName], source.[LastName], source.[LastLogin], source.[Status], source.[RegisteredAt]);
```

### 4. Upsert with deletion

Performs upsert operations and optionally deletes records in the target table that don't exist in the source batch.

```cs
DbContext db = GetYourDbContext();

// Delete records not in source, scoped to specific account
await db.BulkUpsertWithDeleteScopeAsync(
    entities,
    matchOn: x => new { x.AccountId, x.Metric, x.Date },
    deleteScope: x => x.AccountId == 123 && x.Category == "Energy"
);
```

**⚠️ Important:** When `deleteScope` is `null`, ALL records in the target table that don't match ANY row in the source batch will be deleted. Always provide a `deleteScope` to limit deletions to a specific subset (e.g., specific account, date range, or category).

Generated sql:

```sql
-- Creates temporary staging table
CREATE TABLE #TempStaging_... (
    [Id] INT,
    [AccountId] INT,
    [Metric] NVARCHAR(100),
    [Date] DATETIME2,
    [Value] DECIMAL(18,2),
    [Category] NVARCHAR(100)
);

-- Bulk insert into staging table (using SqlBulkCopy)

-- MERGE statement with deletion
MERGE [MetricEntities] AS target
USING #TempStaging_... AS source
ON target.[AccountId] = source.[AccountId] 
   AND target.[Metric] = source.[Metric] 
   AND target.[Date] = source.[Date]
WHEN MATCHED THEN
    UPDATE SET [Value] = source.[Value], [Category] = source.[Category]
WHEN NOT MATCHED BY TARGET THEN
    INSERT ([AccountId], [Metric], [Date], [Value], [Category])
    VALUES (source.[AccountId], source.[Metric], source.[Date], source.[Value], source.[Category])
WHEN NOT MATCHED BY SOURCE AND [AccountId] = @p0 AND [Category] = @p1 THEN
    DELETE;
```

**Parameters:**
- `matchOn`: Expression specifying which columns to match on (defaults to primary key)
- `updateColumns`: Expression specifying which columns to update on match (defaults to all non-key columns)
- `deleteScope`: **Optional** expression to scope which records can be deleted (e.g., `x => x.AccountId == 123`)

## Roadmap

- ~~[ ] BulkMerge~~ cancelled, use BulkUpsert instead
- [x] Identity output
- [x] Upsert with deletion
- [ ] Benchmark with large dataset and table with 20+ columns
