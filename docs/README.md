# docs/

## Current reference

| Doc | Purpose |
|-----|---------|
| [architecture.md](architecture.md) | Request flow, `BulkProviderBase` hook table, provider auto-discovery, gotchas, how to add a new provider |
| [../AGENTS.md](../AGENTS.md) | Build / test / versioning / conventions for contributors and AI agents |
| [../PUBLISH.md](../PUBLISH.md) | Authoritative release + versioning procedure |
| [../README.md](../README.md) | User-facing: install, usage examples, package matrix |
| [../samples/README.md](../samples/README.md) | Runnable `QuickStart` — all four entry points against a real DB |

## archive/

Historical work logs and superseded plans. **Do not treat as current.** Kept for context
on *why* things are the way they are.

| File | Status |
|------|--------|
| `archive/build-and-versioning.md` | Twin-csproj / `Directory.Build.props` deep-dive is still informative; project-structure sections predate the SqlServer/Postgres split and describe a single `ContextBulkExtension` library project that no longer exists |
| `archive/multi-target-nuget-package.md` | Design exploration for the old single-package layout — superseded |
| `archive/implementation-progress.md` | Progress log for the old single-package layout — superseded |
| `archive/workflow-improvements-summary.md` | CI fixes for the old layout — superseded |
| `archive/net12-package-plan.md` | Speculative .NET 12 plan |
| `archive/code-review-provider-agnostic-split.md` | Code-review notes from the provider split |
| `archive/plans/` | Old performance / memory improvement plans |
| `archive/superpowers/` | Old plan drafts |
| `archive/issues/` | Investigated-and-resolved issue notes |
