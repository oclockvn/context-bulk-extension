# AGENTS.md

Guidance for AI coding agents (Claude Code, Copilot, Cursor, Codex, Aider, …) working in
this repository. Humans should read it too. `CLAUDE.md` defers here.

## What this is

`ContextBulkExtension` is a high-performance Entity Framework Core extension for bulk
insert / upsert. Two database providers:

- **SQL Server** — `SqlBulkCopy` + `MERGE` (temp staging table)
- **PostgreSQL** — binary `COPY` + staging `UPDATE`/`INSERT` (+ optional `DELETE ... NOT EXISTS`)

Public API (unchanged across provider split): `BulkInsertAsync`, `BulkUpsertAsync`,
`BulkUpsertWithDeleteScopeAsync`, `BulkConfig`.

## Project layout

| Project | Published? | Purpose |
|---------|-----------|---------|
| `ContextBulkExtension.Core` | **No** — DLL embedded into each provider package | Public API surface, `BulkProviderBase` template, EF metadata helpers, `BulkProviderRegistry` |
| `ContextBulkExtension.SqlServer` | `ContextBulkExtension.SqlServer` on nuget.org | `SqlServerBulkProvider` |
| `ContextBulkExtension.Postgres` | `ContextBulkExtension.Postgres` on nuget.org | `PostgresBulkProvider` |
| `ContextBulkExtension.Tests` | No | xUnit + Testcontainers |

There is **no** package called `ContextBulkExtension` (a former single-package layout was
removed — ignore any doc that still describes it). Core is never installed on its own; its
DLL is packed inside both provider packages.

## The twin-csproj convention (read before touching any .csproj)

Every project has **two** csproj files sharing one folder:

- `X.csproj` → targets **net10.0**, EF Core 10, package version `10.x`
- `X.Net8.csproj` → targets **net8.0**, EF Core 8, package version `8.x`

`Directory.Build.props` branches on `$(MSBuildProjectName.EndsWith('Net8'))` to set
`TargetFramework`, `Version`, `PackageOutputPath`, and an isolated `obj/net8` vs `obj/net10`
so the two `project.assets.json` files don't collide. When you add a source file it is
picked up by both twins automatically — do not add per-twin `<Compile>` items.

Solutions:

| Solution | Projects | TFM |
|----------|----------|-----|
| `ContextBulkExtension.sln` / `.slnx` | `*.csproj` | net10 |
| `ContextBulkExtension.Net8.sln` | `*.Net8.csproj` | net8 |

## Build

```bash
# net10
dotnet build ContextBulkExtension.SqlServer/ContextBulkExtension.SqlServer.csproj
dotnet build ContextBulkExtension.Postgres/ContextBulkExtension.Postgres.csproj

# net8
dotnet build ContextBulkExtension.SqlServer/ContextBulkExtension.SqlServer.Net8.csproj
dotnet build ContextBulkExtension.Postgres/ContextBulkExtension.Postgres.Net8.csproj
```

## Test

```bash
dotnet test ContextBulkExtension.Tests/ContextBulkExtension.Tests.csproj -v q
```

Tests need a real database. By default the fixtures
(`ContextBulkExtension.Tests/Fixtures/`) spin up **Testcontainers** images
(`mcr.microsoft.com/mssql/server:2022-latest`, `postgres:16-alpine`), so a **running Docker
daemon is the only requirement**. First run pulls images.

To point at an existing database instead of Docker, set either env var:

| Var | Used by | Example |
|-----|---------|---------|
| `BULK_TEST_SQL_CONNECTION` | `DatabaseFixture` (SQL Server) | `Server=(localdb)\MSSQLLocalDB;Database=ContextBulkExtensionTests;Trusted_Connection=True;TrustServerCertificate=True` |
| `BULK_TEST_PG_CONNECTION` | `PostgresDatabaseFixture` (PostgreSQL) | `Host=localhost;Port=5432;Database=bulk_test;Username=postgres;Password=postgres` |

If neither Docker nor an override is available, the fixtures throw on startup (they do not
silently skip). A green run means every SQL Server **and** PostgreSQL test passed.

## Versioning — do not hand-edit `Version`

Package versions are computed in `Directory.Build.props`:

- `BaseVersion` (currently `0.20`) — middle segment, tracks the EF patch line
- `PatchNumber` (default `0`) — hotfix counter, passed as `-p:PatchNumber=N`
- Result: net8 → `8.{BaseVersion}.{PatchNumber}`, net10 → `10.{BaseVersion}.{PatchNumber}`

To release: bump `BaseVersion` (EF line moved) or `PatchNumber` (hotfix) and run
`scripts/publish-nuget.ps1`. Git `v*` tags trigger CI publish but never set the version.
See [PUBLISH.md](PUBLISH.md).

## Do not edit / do not commit

- `Nugets/` — build output (git-ignored)
- `**/bin/`, `**/obj/`
- `.github/upgrades/` — tooling-generated
- `ContextBulkExtension/` — stray build-output folder from the old layout; safe to `rm -rf`

## Conventions

- File-scoped namespaces, `var`, nullable enable, expression-bodied members where they read
  cleanly. Match the surrounding file.
- Provider-internal types are `internal`; only `ContextBulkExtension.Core` (namespace
  `ContextBulkExtension.Core`, plus the `DbContext` extension) is public API. Some
  `Helpers/*` members are `public` for cross-assembly use by the providers — treat them as
  semi-internal, not a supported surface.
- Inline comments prefixed `// ponytail:` are deliberate design notes explaining a
  non-obvious choice. Keep the style if you add one.
- XML doc comments on public members ship in the package (`GenerateDocumentationFile=true`,
  `CS1591` suppressed). Add them when you add public members.

## Architecture & request flow

See [docs/architecture.md](docs/architecture.md) for the call path, the `BulkProviderBase`
hook table, and the gotchas list (identity-output cost, `deleteScope: null` danger,
Postgres unique-index requirement, transaction ownership).

## Docs map

See [docs/README.md](docs/README.md). Only files under `docs/` **not** in `docs/archive/`
are current.
