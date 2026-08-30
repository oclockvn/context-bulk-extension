# CLAUDE.md

**Primary guidance for AI agents and contributors lives in [AGENTS.md](AGENTS.md).** Read
it first. This file is a quick index; it must not drift from AGENTS.md.

## Project Overview

ContextBulkExtension is a high-performance Entity Framework Core extension library for bulk
insert/upsert. Supports **SQL Server** (`SqlBulkCopy` + `MERGE`) and **PostgreSQL**
(binary `COPY` + staging upsert).

**Target Frameworks:** .NET 8.0 / .NET 10.0 (twin csproj per project — see AGENTS.md)
**Published packages:** `ContextBulkExtension.SqlServer`, `ContextBulkExtension.Postgres`
(`ContextBulkExtension.Core` DLL is embedded in both, never published separately). There is
no package named `ContextBulkExtension`.

## Build Commands

```bash
# net10
dotnet build ContextBulkExtension.SqlServer/ContextBulkExtension.SqlServer.csproj
dotnet build ContextBulkExtension.Postgres/ContextBulkExtension.Postgres.csproj
# net8: append .Net8 before .csproj

dotnet test ContextBulkExtension.Tests/ContextBulkExtension.Tests.csproj -v q
```

Tests need a running Docker daemon (Testcontainers) OR the `BULK_TEST_SQL_CONNECTION` /
`BULK_TEST_PG_CONNECTION` env overrides. Details in AGENTS.md.

## Architecture

- **Core** — public `BulkInsertAsync` / `BulkUpsert*` / `BulkConfig`; `BulkProviderBase`
  template; `BulkProviderRegistry`; EF metadata helpers. Internal project, DLL packed into
  providers.
- **SqlServer** — `SqlServerBulkProvider` (SqlBulkCopy + MERGE)
- **Postgres** — `PostgresBulkProvider` (COPY + UPDATE/INSERT staging)

Provider assemblies auto-register when loaded from the app base directory. Full request
flow, the hook table, and gotchas: [docs/architecture.md](docs/architecture.md).
