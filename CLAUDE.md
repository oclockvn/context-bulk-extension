# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

ContextBulkExtension is a high-performance Entity Framework Core extension library for bulk insert/upsert.
Supports **SQL Server** (`SqlBulkCopy` + `MERGE`) and **PostgreSQL** (binary `COPY` + staging upsert).

**Target Frameworks:** .NET 8.0 / .NET 10.0  
**Packages:** `ContextBulkExtension.Core`, `.SqlServer`, `.PostgreSql`

## Build Commands

```bash
dotnet build ContextBulkExtension.SqlServer/ContextBulkExtension.SqlServer.csproj
dotnet build ContextBulkExtension.PostgreSql/ContextBulkExtension.PostgreSql.csproj
dotnet test ContextBulkExtension.Tests/ContextBulkExtension.Tests.csproj -v q
```

Optional LocalDB for SQL Server tests without Docker:
`BULK_TEST_SQL_CONNECTION=Server=(localdb)\\MSSQLLocalDB;Database=ContextBulkExtensionTests;Trusted_Connection=True;TrustServerCertificate=True`

## Architecture

1. **Core** — public `BulkInsertAsync` / `BulkUpsert*` / `BulkConfig`; `BulkProviderRegistry`; metadata helpers
2. **SqlServer** — `SqlServerBulkProvider` (SqlBulkCopy + MERGE)
3. **PostgreSql** — `PostgreSqlBulkProvider` (COPY + UPDATE/INSERT staging)

Provider assemblies auto-register when loaded from app base directory.
