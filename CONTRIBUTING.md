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

Only `ContextBulkExtension.Core` (the `ContextBulkExtension.Core` namespace plus the
`DbContext` extension methods) is a supported surface. Provider internals are `internal`.
Some `Helpers/*` members are `public` for cross-assembly use by providers — treat them as
semi-internal; changing them still warrants a deliberate review.
