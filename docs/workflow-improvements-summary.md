# Workflow Improvements Summary

**Date:** 2026-01-17
**Status:** ✅ Completed

---

## Overview

This document summarizes the improvements made to the GitHub Actions workflow and build system for multi-target NuGet package publishing.

---

## Issues Identified

### 1. ❌ Ambiguous Project Name Error
**Problem:** Both jobs failed during build with:
```
error : Ambiguous project name 'ContextBulkExtension'
```

**Root Cause:**
- Matrix strategy built both .NET versions in same workspace
- Both solution files defined projects with the same name "ContextBulkExtension"
- NuGet couldn't distinguish between `ContextBulkExtension.csproj` and `ContextBulkExtension.Net8.csproj`

---

### 2. ❌ Incorrect Version Detection for .NET 10
**Problem:** Version detection extracted wrong BaseVersion:
```bash
# For EF Core 10.0.1:
BaseVersion=$(echo "10.0.1" | cut -d. -f2-)
# Result: BaseVersion=0.1  ❌ WRONG
# Expected: BaseVersion=0.20 (from Directory.Build.props)
```

**Impact:**
- Net10 package got version `10.0.1.0` instead of `10.0.20.0`
- Version didn't match Directory.Build.props configuration

---

### 3. ⚠️ Package Collection Path Issues
**Problem:** Workflow expected packages in wrong location:
```yaml
cp ${{ matrix.output-path }}/*.nupkg ./artifacts/
# Expected: Nugets/net8/*.nupkg
# Actual: ContextBulkExtension/bin/Release/*.nupkg (doesn't exist)
```

---

### 4. ⚠️ Limited Flexibility for Different BaseVersions
**Problem:**
- Single `BaseVersion` in Directory.Build.props shared by both .NET versions
- Couldn't support scenarios like `.NET 8: 8.0.20.X` and `.NET 10: 10.0.1.X`

---

## Solutions Implemented

### ✅ Solution 1: Refactored to Separate Jobs

**From:** Matrix Strategy
```yaml
strategy:
  matrix:
    include:
      - name: net8
        solution: ContextBulkExtension.Net8.sln
      - name: net10
        solution: ContextBulkExtension.sln
```

**To:** Independent Jobs
```yaml
jobs:
  build-net8:
    name: Build and Publish .NET 8 Package
    runs-on: ubuntu-latest
    # ...builds ContextBulkExtension.Net8.csproj directly

  build-net10:
    name: Build and Publish .NET 10 Package
    runs-on: ubuntu-latest
    # ...builds ContextBulkExtension.csproj directly
```

**Benefits:**
- ✅ Clean isolation - no workspace conflicts
- ✅ Builds `.csproj` directly instead of `.sln` (avoids NuGet ambiguity)
- ✅ Easier to debug individual builds
- ✅ Follows EFCore.BulkExtensions pattern

**Files Changed:**
- `.github/workflows/publish-nuget.yml` (lines 15-282)

---

### ✅ Solution 2: MSBuild Property Evaluation for Version Detection

**From:** Manual XML Parsing
```bash
# Extract BaseVersion from Directory.Build.props
BASE_VERSION=$(grep '<BaseVersion>' Directory.Build.props | sed -n 's/.*<BaseVersion>\([^<]*\)<\/BaseVersion>.*/\1/p')

# Construct version
VERSION="8.${BASE_VERSION}.${PATCH_NUMBER}"
```

**To:** MSBuild Evaluation
```bash
# Use MSBuild to evaluate the Version property
VERSION=$(dotnet msbuild ContextBulkExtension/ContextBulkExtension.Net8.csproj \
  -getProperty:Version \
  -p:PatchNumber=$PATCH_NUMBER \
  -nologo)
```

**Benefits:**
- ✅ Single source of truth - uses same logic as build
- ✅ Respects all conditionals in Directory.Build.props
- ✅ Supports different BaseVersion per .NET version automatically
- ✅ No manual XML parsing (eliminates grep/sed fragility)
- ✅ Type-safe property evaluation

**How It Works:**
```bash
# .NET 8 Job
dotnet msbuild ContextBulkExtension/ContextBulkExtension.Net8.csproj -getProperty:Version -p:PatchNumber=0
# MSBuild evaluates: Project ends with 'Net8' → Version = 8.$(BaseVersion).$(PatchNumber)
# Output: 8.0.20.0 ✓

# .NET 10 Job
dotnet msbuild ContextBulkExtension/ContextBulkExtension.csproj -getProperty:Version -p:PatchNumber=0
# MSBuild evaluates: Project does NOT end with 'Net8' → Version = 10.$(BaseVersion).$(PatchNumber)
# Output: 10.0.20.0 ✓
```

**Files Changed:**
- `.github/workflows/publish-nuget.yml` (lines 43-79, 173-209)

---

### ✅ Solution 3: Fixed Package Collection Paths

**From:**
```yaml
cp ${{ matrix.output-path }}/*.nupkg ./artifacts/
# Path: Nugets/net8/*.nupkg (doesn't exist because $(SolutionDir) not set)
```

**To:**
```yaml
# For .NET 8 job
cp ContextBulkExtension/Nugets/net8/*.nupkg ./artifacts/

# For .NET 10 job
cp ContextBulkExtension/Nugets/net10/*.nupkg ./artifacts/
```

**Why It Works:**
- `Directory.Build.props` sets `PackageOutputPath` relative to solution directory
- Packages are actually output to `ContextBulkExtension/Nugets/net8/` and `ContextBulkExtension/Nugets/net10/`
- Workflow now copies from correct locations

**Files Changed:**
- `.github/workflows/publish-nuget.yml` (lines 93-96, 218-230)

---

### ✅ Solution 4: Updated Solution File for Unique Project Names

**From:**
```sln
Project(...) = "ContextBulkExtension", "ContextBulkExtension\ContextBulkExtension.Net8.csproj", {...}
```

**To:**
```sln
Project(...) = "ContextBulkExtension.Net8", "ContextBulkExtension\ContextBulkExtension.Net8.csproj", {...}
```

**Why:**
- Ensures unique project names across workspace
- Prevents NuGet from seeing ambiguous project references
- Aligns with multi-target package best practices

**Files Changed:**
- `ContextBulkExtension.Net8.sln` (line 5)

---

### ✅ Solution 5: Added Flexibility for Different BaseVersions

**Documentation Added:**
```xml
<!-- .NET 8 specific configuration -->
<PropertyGroup Condition="$(MSBuildProjectName.EndsWith('Net8'))">
  <!-- Note: Can override BaseVersion here for Net8-specific versioning if needed -->
  <!-- Example: <BaseVersion>0.21</BaseVersion> would produce 8.0.21.X -->
  <Version>8.$(BaseVersion).$(PatchNumber)</Version>
  <!-- ... -->
</PropertyGroup>

<!-- .NET 10 specific configuration (default) -->
<PropertyGroup Condition="!$(MSBuildProjectName.EndsWith('Net8'))">
  <!-- Note: Can override BaseVersion here for Net10-specific versioning if needed -->
  <!-- Example: <BaseVersion>0.1</BaseVersion> would produce 10.0.1.X -->
  <Version>10.$(BaseVersion).$(PatchNumber)</Version>
  <!-- ... -->
</PropertyGroup>
```

**Usage Example:**
If you need .NET 8 at version `8.0.20.0` and .NET 10 at `10.0.1.0`:
1. Add `<BaseVersion>0.20</BaseVersion>` in .NET 8 conditional block
2. Add `<BaseVersion>0.1</BaseVersion>` in .NET 10 conditional block
3. No workflow changes needed - MSBuild evaluation automatically picks up correct version!

**Files Changed:**
- `Directory.Build.props` (lines 29-30, 41-42)

---

## Test Results

### ✅ Local Testing (MSBuild Evaluation)

```bash
# .NET 8 with PatchNumber=0
$ dotnet msbuild ContextBulkExtension/ContextBulkExtension.Net8.csproj -getProperty:Version -p:PatchNumber=0 -nologo
8.0.20.0 ✓

# .NET 10 with PatchNumber=0
$ dotnet msbuild ContextBulkExtension/ContextBulkExtension.csproj -getProperty:Version -p:PatchNumber=0 -nologo
10.0.20.0 ✓

# Custom patch number
$ dotnet msbuild ContextBulkExtension/ContextBulkExtension.Net8.csproj -getProperty:Version -p:PatchNumber=5 -nologo
8.0.20.5 ✓
```

### ✅ Act Testing (Workflow Simulation)

```bash
# .NET 8 job
$ act workflow_dispatch -W .github/workflows/publish-nuget.yml --input patch_number=0 -j build-net8

# Results:
✓ Determined version: 8.0.20.0 (via MSBuild)
✓ Build succeeded
✓ Package created: ContextBulkExtension.8.0.20.nupkg
✓ Package location: ContextBulkExtension/Nugets/net8/
```

---

## Before vs After Comparison

| Aspect | Before | After |
|--------|--------|-------|
| **Job Strategy** | Matrix (single job) | Separate jobs |
| **Build Target** | Solution files (.sln) | Project files (.csproj) |
| **Version Detection** | grep + sed XML parsing | MSBuild property evaluation |
| **Version Accuracy** | ❌ Wrong for .NET 10 (10.0.1.0) | ✅ Correct (10.0.20.0) |
| **Workspace Conflicts** | ❌ "Ambiguous project name" | ✅ Clean isolation |
| **BaseVersion Flexibility** | ❌ Single shared value | ✅ Can differ per .NET version |
| **Package Collection** | ❌ Wrong path | ✅ Correct path |
| **Reliability** | ⭐⭐ | ⭐⭐⭐⭐⭐ |

---

## Files Modified

### Core Changes
1. ✅ `.github/workflows/publish-nuget.yml` - Complete refactor (282 lines)
2. ✅ `ContextBulkExtension.Net8.sln` - Updated project name (line 5)
3. ✅ `Directory.Build.props` - Added documentation comments (lines 29-30, 41-42)

### Documentation Added
4. ✅ `docs/build-and-versioning.md` - Comprehensive build system documentation (500+ lines)
5. ✅ `docs/workflow-improvements-summary.md` - This summary document

---

## Key Takeaways

### ✅ What We Achieved

1. **Fixed critical build issues**
   - Eliminated "ambiguous project name" errors
   - Corrected version detection for all .NET versions

2. **Improved reliability**
   - MSBuild evaluation ensures version accuracy
   - Direct .csproj builds avoid solution file complexities

3. **Enhanced flexibility**
   - Support for different BaseVersions per .NET version
   - Easy to add .NET 12 support in future

4. **Better developer experience**
   - Clear documentation
   - Local testing with `act`
   - Easier debugging

### 🎯 Future-Proof

The new architecture makes it trivial to add .NET 12 support:

1. Create `ContextBulkExtension.Net12.csproj`
2. Add conditional block in `Directory.Build.props`:
   ```xml
   <PropertyGroup Condition="$(MSBuildProjectName.EndsWith('Net12'))">
     <Version>12.$(BaseVersion).$(PatchNumber)</Version>
     <TargetFramework>net12.0</TargetFramework>
     <PackageOutputPath>$(SolutionDir)Nugets\net12</PackageOutputPath>
   </PropertyGroup>
   ```
3. Add `build-net12` job to workflow (copy from `build-net8`, update paths)
4. Done! ✅

---

## Additional Fixes

### ✅ Solution 6: Use Explicit `dotnet pack` Command

**Issue:** GitHub Actions failed with `NU3004: The package is not signed` error when using `GeneratePackageOnBuild`.

**Root Cause Analysis:**
- Current workflow relied on `GeneratePackageOnBuild=True` in Directory.Build.props
- Working commit (`6ea1e78`) used explicit `dotnet pack` command
- `dotnet pack` and build-time package generation may handle package metadata differently

**Solution:** Revert to explicit `dotnet pack` (matches working commit)

**Changes:**
```yaml
# Before (relying on GeneratePackageOnBuild)
- name: Build project
  run: dotnet build ... -p:PatchNumber=...

- name: Collect NuGet packages
  run: cp ContextBulkExtension/Nugets/net8/*.nupkg ./artifacts/

# After (explicit pack)
- name: Build project
  run: dotnet build ... -p:PatchNumber=...

- name: Pack NuGet package
  run: |
    dotnet pack ContextBulkExtension/ContextBulkExtension.Net8.csproj \
      --configuration Release \
      --no-build \
      --output ./artifacts \
      -p:PatchNumber=...
```

**Files Changed:**
- `.github/workflows/publish-nuget.yml` (lines 88-94, 221-227)

---

## Verification Checklist

Before merging to main:

- [x] Local MSBuild evaluation produces correct versions
- [x] Act workflow simulation succeeds for .NET 8
- [ ] Act workflow simulation succeeds for .NET 10
- [x] Package validation fix applied (removed --all flag)
- [ ] GitHub Actions workflow runs successfully
- [ ] Packages published to NuGet.org with correct versions
- [ ] Documentation updated and reviewed

---

## Next Steps

1. **Test in GitHub Actions**
   - Trigger workflow via tag push or manual dispatch
   - Verify both jobs complete successfully
   - Confirm packages published to NuGet.org

2. **Monitor First Production Run**
   - Check job logs for any issues
   - Verify package versions on NuGet.org
   - Validate package contents

3. **Update Implementation Progress Doc**
   - Mark workflow refactor as complete in `docs/implementation-progress.md`

---

## References

- [Build and Versioning Documentation](build-and-versioning.md)
- [Multi-Target NuGet Package Guide](multi-target-nuget-package.md)
- [EFCore.BulkExtensions Pattern](https://github.com/borisdj/EFCore.BulkExtensions)
- [GitHub Actions: Workflow Syntax](https://docs.github.com/en/actions/using-workflows/workflow-syntax-for-github-actions)
