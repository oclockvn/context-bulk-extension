# Multi-Target NuGet Package Implementation - Progress Report

**Date:** 2026-01-12  
**Status:** 87.5% Complete (7/8 tasks)

## ✅ Completed Tasks

### 1. Updated Directory.Build.props ✅
**File:** `NugetPackages\Directory.Build.props`

**Changes Made:**
- ✅ Changed from `Contains('v8')` to `EndsWith('Net8')` pattern
- ✅ Configured version logic: 8.x.x for Net8, 10.x.x for .NET 10
- ✅ Set package output paths: `$(SolutionDir)Nugets\net8` and `$(SolutionDir)Nugets\net10`
- ✅ Added `GeneratePackageOnBuild=True` property
- ✅ Removed version suffix from PackageId (now consistent: `ContextBulkExtension.SqlServer`)
- ✅ Moved provider-specific PackageId logic before version-specific logic

### 2. Created New Project Structure ✅
**Directory:** `NugetPackages\ContextBulkExtension.SqlServer\`

**Files Created:**
- ✅ `ContextBulkExtension.SqlServer.csproj` (targets net10.0, EF Core 10.0.1)
- ✅ `ContextBulkExtension.SqlServer.Net8.csproj` (targets net8.0, EF Core 8.0.20)

**Key Properties:**
- Both use same PackageId: `ContextBulkExtension.SqlServer`
- Version separation handled by Directory.Build.props
- ProjectReference paths: `..\..\ContextBulkExtension\ContextBulkExtension.csproj` (or `.Net8.csproj`)

### 3. Updated Main Library ✅
**Files:**
- ✅ Created `ContextBulkExtension\ContextBulkExtension.Net8.csproj` (net8.0, EF Core 8.0.20)
- ✅ Updated `ContextBulkExtension\ContextBulkExtension.csproj` (net10.0, EF Core 10.0.1)

**Note:** Both projects reference the same source files (no duplication)

### 4. Fixed Project References ✅
**Changes:**
- ✅ `ContextBulkExtension.SqlServer.Net8.csproj` → references `ContextBulkExtension.Net8.csproj`
- ✅ `ContextBulkExtension.SqlServer.csproj` → references `ContextBulkExtension.csproj`

### 5. Created Solution Files ✅
**Files:**
- ✅ Updated `ContextBulkExtension.sln` - references .NET 10 projects
- ✅ Created `ContextBulkExtension.Net8.sln` - references .NET 8 projects

**Project References:**
- Main solution: `ContextBulkExtension.csproj`, `ContextBulkExtension.SqlServer.csproj`
- Net8 solution: `ContextBulkExtension.Net8.csproj`, `ContextBulkExtension.SqlServer.Net8.csproj`

### 6. Updated slnx File ✅
**File:** `ContextBulkExtension.slnx`

**Changes:**
- ✅ Removed old `v8/v10` folder references
- ✅ Added new project paths: `NugetPackages/ContextBulkExtension.SqlServer/ContextBulkExtension.SqlServer.csproj`
- ✅ Added: `NugetPackages/ContextBulkExtension.SqlServer/ContextBulkExtension.SqlServer.Net8.csproj`

### 7. Created Output Directories ✅
**Directories:**
- ✅ Created `Nugets\net8\`
- ✅ Created `Nugets\net10\`

---

## ⏳ Remaining Tasks (1/8)

### 8. Update Documentation ⏳

#### 📝 README.md - PENDING
**Current Issues:**
- Line 9: Badge URL still references `ContextBulkExtension.SqlServer.v8`
- Line 13: Installation command shows `ContextBulkExtension.SqlServer.v8`
- Lines 20-22: Package structure section lists `.v8` and `.v9` suffixes

**Required Changes:**
```markdown
# Current (lines 9-13):
[![Downloads](https://img.shields.io/nuget/dt/ContextBulkExtension.SqlServer.v8)](https://www.nuget.org/packages/ContextBulkExtension.SqlServer.v8/)

```bash
# For EF Core 8.x
dotnet add package ContextBulkExtension.SqlServer.v8
```

# Should be:
[![Downloads](https://img.shields.io/nuget/dt/ContextBulkExtension.SqlServer)](https://www.nuget.org/packages/ContextBulkExtension.SqlServer/)

```bash
# For EF Core 8.x
dotnet add package ContextBulkExtension.SqlServer --version 8.*

# For EF Core 10.x
dotnet add package ContextBulkExtension.SqlServer --version 10.*
```

# Package Structure section (lines 16-24):
This library provides separate NuGet packages for different EF Core versions:

- **ContextBulkExtension.SqlServer** - SQL Server provider
  - Version 8.x.x for Entity Framework Core 8.x
  - Version 10.x.x for Entity Framework Core 10.x
- **ContextBulkExtension.Postgres** - PostgreSQL provider (coming soon)

The package version number corresponds to the EF Core version it supports. Choose the version that matches your EF Core version.
```

#### 🔧 .github/workflows/publish-nuget.yml - PENDING
**Current Issues:**
- Line 25: Matrix package references `ContextBulkExtension.SqlServer.v8`
- Line 52: Path references `NugetPackages/${{ matrix.package }}.csproj`
- Line 98: Build command uses old path
- Line 106: Pack command uses old path
- Workflow only builds .NET 8, needs to support both .NET 8 and .NET 10

**Required Changes:**
```yaml
# Current (line 22-25):
strategy:
  matrix:
    package:
      - ContextBulkExtension.SqlServer.v8

# Should be:
strategy:
  matrix:
    include:
      - solution: ContextBulkExtension.Net8.sln
        dotnet-version: '8.0.x'
        package-name: ContextBulkExtension.SqlServer
        output-path: Nugets/net8
      - solution: ContextBulkExtension.sln
        dotnet-version: '10.0.x'
        package-name: ContextBulkExtension.SqlServer
        output-path: Nugets/net10

# Update build step (line 96-102):
- name: Build solution
  run: |
    dotnet build ${{ matrix.solution }} \
      --configuration Release \
      --no-incremental \
      -p:BaseVersion=${{ steps.version.outputs.BaseVersion }} \
      -p:PatchNumber=${{ steps.version.outputs.PatchNumber }}

# Update pack step (line 104-111):
- name: Pack NuGet package
  run: |
    # Packages are automatically generated by GeneratePackageOnBuild
    # Just copy from output path
    mkdir -p ./artifacts
    cp ${{ matrix.output-path }}/*.nupkg ./artifacts/ || echo "No packages found"
```

#### 📄 .gitignore - PENDING
**Required Change:**
Add `Nugets/` directory to ignore generated packages:

```gitignore
# Add after line 198 (after *.snupkg):
# NuGet package output directories
Nugets/
```

---

## 🗑️ Cleanup Tasks (Optional)

### Old Files to Remove (After Verification)
Once the new structure is verified working:
- `NugetPackages\v8\` directory and contents
- `NugetPackages\v10\` directory and contents

**⚠️ Important:** Do NOT delete these until:
1. New packages build successfully
2. Tests pass with new structure
3. At least one successful publish to NuGet.org

---

## 📊 Build & Test Commands

### Building Packages

**Build .NET 8 packages:**
```bash
dotnet build ContextBulkExtension.Net8.sln --configuration Release
# Output: Nugets\net8\ContextBulkExtension.SqlServer.8.0.20.0.nupkg
```

**Build .NET 10 packages:**
```bash
dotnet build ContextBulkExtension.sln --configuration Release
# Output: Nugets\net10\ContextBulkExtension.SqlServer.10.0.20.0.nupkg
```

### Verifying Package Metadata

```bash
# Extract and view package metadata
dotnet nuget verify Nugets\net8\*.nupkg --all
dotnet nuget verify Nugets\net10\*.nupkg --all

# Or use NuGet Package Explorer (GUI)
```

### Testing Locally

```bash
# Add local package source
dotnet nuget add source E:\projects\ContextBulkExtension\Nugets\net8 -n "Local-Net8"
dotnet nuget add source E:\projects\ContextBulkExtension\Nugets\net10 -n "Local-Net10"

# Test installation
dotnet add package ContextBulkExtension.SqlServer --version 8.0.20.0 --source Local-Net8
dotnet add package ContextBulkExtension.SqlServer --version 10.0.20.0 --source Local-Net10
```

---

## 🎯 Next Steps

1. **Update README.md** - Fix package names and installation instructions
2. **Update GitHub Actions workflow** - Support building both .NET 8 and .NET 10
3. **Update .gitignore** - Add Nugets/ directory
4. **Test build** - Verify packages generate correctly
5. **Test locally** - Install and test packages in a sample project
6. **Cleanup** - Remove old v8/v10 directories after verification
7. **Publish** - Push new packages to NuGet.org

---

## 📝 Notes

### Package Naming Convention
- **PackageId:** `ContextBulkExtension.SqlServer` (consistent across versions)
- **Version:** 8.x.x for .NET 8, 10.x.x for .NET 10
- **No suffix** in PackageId (version separation via version number only)

### Migration Path for Existing Users
Users currently using `ContextBulkExtension.SqlServer.v8` will need to:
1. Uninstall old package: `dotnet remove package ContextBulkExtension.SqlServer.v8`
2. Install new package: `dotnet add package ContextBulkExtension.SqlServer --version 8.*`

Consider adding a deprecation notice to the old package on NuGet.org.

### Directory.Build.props Logic
- Projects ending with `Net8` → Version 8.x.x, Target net8.0, Output to Nugets\net8
- All other projects → Version 10.x.x, Target net10.0, Output to Nugets\net10

---

**Last Updated:** 2026-01-12 09:07 AM
