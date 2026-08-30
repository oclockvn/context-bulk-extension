# Contributing

## Before you start

- **[AGENTS.md](AGENTS.md)** — project layout, the twin-csproj net8/net10 convention, build
  and test commands, versioning rules, code conventions. Read it first.
- **[docs/architecture.md](docs/architecture.md)** — how a bulk operation flows through the
  code, the `BulkProviderBase` hook table, and the gotchas list.

## Workflow

1. Branch from `master`.
2. `dotnet build` both providers (net10 and net8 twins) — see AGENTS.md.
3. `dotnet test ContextBulkExtension.Tests/ContextBulkExtension.Tests.csproj` (needs Docker
   or the `BULK_TEST_*` connection overrides). A green run covers SQL Server **and**
   PostgreSQL.
4. For a quick manual check: `dotnet run --project samples/QuickStart`.
5. Open a PR to `master`. CI (`.github/workflows/ci.yml`) builds both TFMs and runs the
   suite.
6. Do **not** hand-edit `Version` / package numbers — that is done at release time via
   `Directory.Build.props` + `scripts/publish-nuget.ps1` ([PUBLISH.md](PUBLISH.md)).

## Adding a new database provider

Step-by-step checklist in
[docs/architecture.md § Adding a new provider](docs/architecture.md#adding-a-new-provider-eg-mysql-sqlite).
In short: new `ContextBulkExtension.<Db>` project pair, `internal sealed class
<Db>BulkProvider : BulkProviderBase` implementing the hook table, a
`<Db>BulkProviderRegistration.Initialize()` entrypoint, a `TryLoadAndRegister(...)` line in
`BulkProviderRegistry`, a test fixture + test class, and wiring into the publish script and
CI.

## Public API

The entire supported surface is two types in `ContextBulkExtension.Core`:
`DbContextBulkExtension` (the `BulkInsertAsync` / `BulkUpsertAsync` /
`BulkUpsertWithDeleteScopeAsync` extension methods) and `BulkConfig`. Everything else —
providers, metadata helpers, the `BulkProviderBase` template — is `internal` (providers see
Core internals via `InternalsVisibleTo`).

That surface is locked by `Microsoft.CodeAnalysis.PublicApiAnalyzers`:

- `ContextBulkExtension.Core/PublicAPI.Shipped.txt` — API already released in packages on
  nuget.org. Do not edit except to move entries up from Unshipped at release time.
- `ContextBulkExtension.Core/PublicAPI.Unshipped.txt` — API added since the last release.

`RS0016` (new public symbol not listed) and `RS0017` (listed symbol removed) are **build
errors** for Core (see `.editorconfig`). If you intentionally change the public API:

1. Build once; the analyzer's message names the exact line to add.
2. Add it to `PublicAPI.Unshipped.txt` (there is an IDE code-fix for this).
3. Call out the API change in your PR description.
