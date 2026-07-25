## Publishing New Versions

Consumer-facing packages (only these are published):

- `ContextBulkExtension.SqlServer`
- `ContextBulkExtension.Postgres`

`ContextBulkExtension.Core` is an internal project. Its DLL is embedded inside each provider package (not published separately). If an old Core package exists on nuget.org, unlist/deprecate it manually.

### Versioning

From `Directory.Build.props`:

| Property | Role | Example |
|----------|------|---------|
| `BaseVersion` | Middle segment (aligned with EF patch line) | `0.20` |
| `PatchNumber` | Hotfix at same BaseVersion | `0`, `1`, … |

Resulting package versions:

- .NET 8 / EF 8: `8.{BaseVersion}.{PatchNumber}` → e.g. `8.0.20.0`
- .NET 10 / EF 10: `10.{BaseVersion}.{PatchNumber}` → e.g. `10.0.20.0`

Tags (`v*`) trigger CI but **do not** set the NuGet version. Bump `BaseVersion` in props (and commit) when the EF line moves; use `patch_number` for hotfixes.

### Prerequisites

1. **Trusted Publishing Setup on nuget.org** (for GitHub Actions):
   - Log into [nuget.org](https://www.nuget.org)
   - Account settings → **Trusted Publishing**
   - Add policy:
     - **Repository Owner:** your GitHub user/org
     - **Repository:** `context-bulk-extension` (or actual repo name)
     - **Workflow File:** `publish-nuget.yml`
     - **Environment:** leave empty if unused

2. **Local push only:** set `NUGET_API_KEY` to a nuget.org API key (or use Trusted Publishing via CI instead).

### Option 1: PowerShell script (recommended)

```powershell
# Dry run
.\scripts\publish-nuget.ps1 -DryRun

# Bump BaseVersion (EF line moved), pack both TFMs, push tag → CI publish
.\scripts\publish-nuget.ps1 -BaseVersion "0.21" -PatchNumber 0

# Hotfix at same BaseVersion; local push with API key
.\scripts\publish-nuget.ps1 -PatchNumber 1 -LocalPush

# Pack only (no tag, no push)
.\scripts\publish-nuget.ps1 -SkipTag -SkipPush
```

Script will:

1. Optionally update `BaseVersion` in `Directory.Build.props`
2. Build and pack SqlServer + Postgres for net8 and net10 into `Nugets/`
3. Either create/push a `v*` tag (CI Trusted Publishing) **or** `-LocalPush` with `NUGET_API_KEY`

### Option 2: Tag → GitHub Actions

1. Commit any `BaseVersion` change.
2. Create and push a tag:

```bash
git tag -a v8.0.20.0 -m "Release 8.0.20.0 / 10.0.20.0"
git push origin v8.0.20.0
```

3. Workflow packs SqlServer + Postgres (net8 + net10) and pushes via OIDC.

### Option 3: Manual workflow dispatch

1. Actions → **Publish NuGet Package** → **Run workflow**
2. Enter `patch_number` (default `0`)
3. Both jobs publish SqlServer + Postgres

### Option 4: Local CLI push

```powershell
dotnet build ContextBulkExtension.SqlServer/ContextBulkExtension.SqlServer.Net8.csproj -c Release -p:PatchNumber=0
dotnet pack  ContextBulkExtension.SqlServer/ContextBulkExtension.SqlServer.Net8.csproj -c Release --no-build -p:PatchNumber=0
# repeat for Postgres.Net8, SqlServer.csproj, Postgres.csproj

dotnet nuget push Nugets/net8/*.nupkg  -k $env:NUGET_API_KEY -s https://api.nuget.org/v3/index.json --skip-duplicate
dotnet nuget push Nugets/net10/*.nupkg -k $env:NUGET_API_KEY -s https://api.nuget.org/v3/index.json --skip-duplicate
```

Or: `.\scripts\publish-nuget.ps1 -LocalPush`
