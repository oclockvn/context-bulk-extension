# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

ContextBulkExtension is a high-performance Entity Framework Core extension library that provides bulk insert operations using SQL Server's SqlBulkCopy. It's designed for efficiently inserting millions of records.

**Target Framework:** .NET 8.0
**Dependencies:** Microsoft.EntityFrameworkCore.SqlServer 8.0.20

## Build Commands

Build the project:
```bash
dotnet build ContextBulkExtension/ContextBulkExtension.csproj
```

Build with specific configuration:
```bash
dotnet build ContextBulkExtension/ContextBulkExtension.csproj --configuration Release
```

Clean build artifacts:
```bash
dotnet clean ContextBulkExtension/ContextBulkExtension.csproj
```

## Architecture

### Core Components

1. **DbContextBulkExtension** (DbContextBulkExtension.cs)
   - Main entry point providing `BulkInsertAsync<T>()` extension methods on DbContext
   - Validates SQL Server connection type
   - Integrates with EF Core's transaction management
   - Configures and executes SqlBulkCopy operations

2. **EntityMetadataHelper** (EntityMetadataHelper.cs)
   - Extracts and caches entity metadata from EF Core model
   - Uses `ConcurrentDictionary` for thread-safe caching by (EntityType, ContextType)
   - Builds compiled expression delegates for fast property access
   - Handles complex properties, value converters, and EF Core 8+ features
   - Automatically detects and excludes:
     - Shadow properties (unless foreign keys)
     - Computed columns
     - Properties with OnAddOrUpdate/OnUpdate value generation
     - Identity columns (unless KeepIdentity=true)
   - Escapes SQL Server identifiers properly

3. **EntityDataReader** (EntityDataReader.cs)
   - Memory-efficient DbDataReader implementation
   - Streams entities to SqlBulkCopy without materializing full dataset
   - Uses compiled getters from metadata for fast property access

4. **BulkInsertOptions** (BulkInsertOptions.cs)
   - Configuration class for bulk operations
   - Default BatchSize: 10,000
   - Default Timeout: 300 seconds (5 minutes)
   - Default UseTableLock: true (set to false for memory-optimized tables)

5. **CachedEntityMetadata** (CachedEntityMetadata.cs)
   - Internal cache structure storing column metadata and table names
   - Maintains two column lists: with and without identity columns

### Key Design Patterns

- **Metadata Caching:** Entity metadata is cached per (EntityType, ContextType) to avoid repeated reflection
- **Compiled Expressions:** Property getters are compiled once using Expression trees for optimal performance
- **Streaming:** Uses IDataReader pattern to stream data without loading all entities into memory
- **Transaction Support:** Automatically participates in existing EF Core transactions
- **Type Safety:** Generic methods ensure compile-time type checking

### Identity Column Detection

Identity columns are detected when a property has:
- `ValueGenerated.OnAdd` AND any of:
  - DefaultValueSql contains "IDENTITY"
  - Has a value generator factory
  - Is a primary key with int/long CLR type

## Important Implementation Notes

### SQL Server Only
The library only supports SQL Server. It validates the connection type and throws `InvalidOperationException` if not SqlConnection.

### Column Exclusion Logic
When building metadata, the following are automatically excluded:
- Shadow properties (unless they're foreign keys)
- Computed columns (GetComputedColumnSql() != null)
- Properties with AfterSaveBehavior (OnAddOrUpdate/OnUpdate)
- Identity columns (when KeepIdentity=false)

### Memory-Optimized Tables
When working with In-Memory OLTP tables, set `UseTableLock = false` in BulkInsertOptions.

### Complex Properties (EF Core 8+)
The library supports complex properties by building nested property access expressions: `instance.ComplexProp.Property`.

### Value Converters
EF Core value converters are automatically applied during property access via the compiled getter.
