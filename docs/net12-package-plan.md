# .NET 12 Package Plan

This document outlines how to add .NET 12 package support to this repo using the existing multi-target pattern. It assumes .NET 12 becomes the "latest" default while .NET 8 remains supported.

## Scope

- Keep .NET 8 packages as LTS.
- Move the non-suffixed projects to .NET 12.
- Keep PackageId the same (`ContextBulkExtension.SqlServer`) and separate by version numbers.

If you want to keep .NET 10 in parallel, see "Optional: Keep .NET 10" below.

## Step-by-step

### 1) Update Directory.Build.props

File: `NugetPackages/Directory.Build.props`

Change the default (non-Net8) block to .NET 12:
- `TargetFramework` -> `net12.0`
- Version -> `12.$(BaseVersion).$(PatchNumber)`
- Output -> `Nugets/net12`
- Define constants -> `V12`

Keep the Net8 block unchanged.

### 2) Update main library project

File: `ContextBulkExtension/ContextBulkExtension.csproj`

Update to .NET 12 and EF Core 12:
- `TargetFramework` -> `net12.0`
- `PackageReference` -> `Microsoft.EntityFrameworkCore.SqlServer` v12.x

Leave `ContextBulkExtension/ContextBulkExtension.Net8.csproj` unchanged.

### 3) Update SqlServer package project

File: `NugetPackages/ContextBulkExtension.SqlServer/ContextBulkExtension.SqlServer.csproj`

Update to .NET 12 and EF Core 12:
- `TargetFramework` -> `net12.0`
- `PackageReference` -> `Microsoft.EntityFrameworkCore.SqlServer` v12.x

Leave `ContextBulkExtension.SqlServer.Net8.csproj` unchanged.

### 4) Update solutions

File: `ContextBulkExtension.sln`
- Keeps the "latest" projects, now .NET 12.

File: `ContextBulkExtension.Net8.sln`
- Remains unchanged.

### 5) Update CI workflow

File: `.github/workflows/publish-nuget.yml`

Update matrix for net12:
- Replace net10 entry with net12 entry
- SDK: `12.0.x`
- Output: `Nugets/net12`

### 6) Update README

File: `README.md`

Add/update install guidance:
- `ContextBulkExtension.SqlServer` version `8.*` for .NET 8
- `ContextBulkExtension.SqlServer` version `12.*` for .NET 12

### 7) Add output folder (optional)

Create `Nugets/net12` if you want it present ahead of first build. The build also creates it automatically.

## Optional: Keep .NET 10

If you want to keep .NET 10 alongside 8 and 12:

1) Add `.Net10` project variants for both `ContextBulkExtension` and `ContextBulkExtension.SqlServer`.
2) Extend `Directory.Build.props` conditions:
   - `EndsWith('Net8')` -> net8
   - `EndsWith('Net10')` -> net10
   - default -> net12
3) Add `ContextBulkExtension.Net10.sln`.
4) Extend CI matrix for net8, net10, net12.

## Versioning

- Net8 packages: `8.x.x`
- Net12 packages: `12.x.x`
- PackageId remains the same (`ContextBulkExtension.SqlServer`).

## Checklist

- [ ] Update `NugetPackages/Directory.Build.props` default block to net12
- [ ] Update main library to net12 + EF Core 12
- [ ] Update SqlServer package to net12 + EF Core 12
- [ ] Update CI matrix to net12
- [ ] Update README install guidance
- [ ] Publish and verify packages
