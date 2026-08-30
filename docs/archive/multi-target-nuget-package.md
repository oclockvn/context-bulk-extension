# Multi-Targeting NuGet Packages - Complete Guide

This document explains how the **EFCore.BulkExtensions** solution implements multi-target NuGet packaging to build separate NuGet packages for different .NET versions (e.g., .NET 8 and .NET 10) from the same codebase.

## Table of Contents
- [Overview](#overview)
- [Architecture](#architecture)
- [Key Components](#key-components)
- [How It Works](#how-it-works)
- [Step-by-Step Implementation Guide](#step-by-step-implementation-guide)
- [Building and Publishing](#building-and-publishing)
- [Best Practices](#best-practices)

---

## Overview

The EFCore.BulkExtensions solution uses a **dual-project** strategy where:
- Each project has **two .csproj files**: one for .NET 10 (latest) and one for .NET 8
- Each solution has **two .sln files**: one referencing .NET 10 projects, one referencing .NET 8 projects
- A central **Directory.Build.props** file manages version numbers and package output paths based on project naming conventions
- Each version produces **separate NuGet packages** with different version numbers

**Example:**
- Building `EFCore.BulkExtensions.sln` produces NuGet v10.0.0 targeting .NET 10
- Building `EFCore.BulkExtensions.Net8.sln` produces NuGet v8.1.3 targeting .NET 8

---

## Architecture

### Project Structure
```
EFCore.BulkExtensions/
│
├── Directory.Build.props                          # Central build configuration
│
├── EFCore.BulkExtensions.sln                      # Solution for .NET 10
├── EFCore.BulkExtensions.Net8.sln                 # Solution for .NET 8
│
├── EFCore.BulkExtensions/
│   ├── EFCore.BulkExtensions.csproj               # .NET 10 (net9.0) - meta package
│   └── EFCore.BulkExtensions.Net8.csproj          # .NET 8 (net8.0) - meta package
│
├── EFCore.BulkExtensions.Core/
│   ├── EFCore.BulkExtensions.Core.csproj          # .NET 10 (net10.0)
│   └── EFCore.BulkExtensions.Core.Net8.csproj     # .NET 8 (net8.0)
│
├── EFCore.BulkExtensions.SqlServer/
│   ├── EFCore.BulkExtensions.SqlServer.csproj     # .NET 10
│   └── EFCore.BulkExtensions.SqlServer.Net8.csproj # .NET 8
│
├── EFCore.BulkExtensions.PostgreSql/
│   ├── EFCore.BulkExtensions.PostgreSql.csproj    # .NET 10 (net9.0)
│   └── EFCore.BulkExtensions.PostgreSql.Net8.csproj # .NET 8
│
└── ... (similar pattern for MySql, Oracle, Sqlite)
```

### Package Output Structure
```
Nugets/
├── net8/
│   ├── EFCore.BulkExtensions.8.1.3.nupkg
│   ├── EFCore.BulkExtensions.Core.8.1.3.nupkg
│   └── ...
│
└── net10/
    ├── EFCore.BulkExtensions.10.0.0.nupkg
    ├── EFCore.BulkExtensions.Core.10.0.0.nupkg
    └── ...
```

---

## Key Components

### 1. Directory.Build.props

This is the **central configuration file** that controls versioning and packaging behavior for all projects.

**Location:** `E:\projects\EfCore.BulkExtensions\Directory.Build.props`

```xml
<Project>
  <PropertyGroup>
    <!-- Common properties for all projects -->
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <Nullable>enable</Nullable>
    <Authors>borisdj</Authors>
    <PackageProjectUrl>https://github.com/borisdj/EFCore.BulkExtensions</PackageProjectUrl>
    <Company>CODIS LLC</Company>
    <PackageIcon>EFCoreBulk.png</PackageIcon>
    <PackageLicenseFile>LICENSE.txt</PackageLicenseFile>
    <PackageReleaseNotes>net 10 rc</PackageReleaseNotes>
    <PackageTags>EntityFrameworkCore Entity Framework Core .Net EFCore EF Core Bulk Batch...</PackageTags>
    <PackageRequireLicenseAcceptance>True</PackageRequireLicenseAcceptance>
    <RepositoryUrl>https://github.com/borisdj/EFCore.BulkExtensions</RepositoryUrl>
    <RepositoryType>Git</RepositoryType>
    <AllowedOutputExtensionsInPackageBuildOutputFolder>$(AllowedOutputExtensionsInPackageBuildOutputFolder);.pdb</AllowedOutputExtensionsInPackageBuildOutputFolder>
    <SignAssembly>true</SignAssembly>
    <AssemblyOriginatorKeyFile>$(MSBuildThisFileDirectory)Keys\EFCore.BulkExtensions.snk</AssemblyOriginatorKeyFile>
    <DelaySign>false</DelaySign>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <LangVersion>latest</LangVersion>
    <GeneratePackageOnBuild>True</GeneratePackageOnBuild>
    <PackageReadmeFile>README.md</PackageReadmeFile>
  </PropertyGroup>

  <!-- .NET 8 specific configuration -->
  <PropertyGroup Condition="$(MSBuildProjectName.EndsWith('Net8'))">
    <Version>8.1.3</Version>
    <PackageOutputPath>$(SolutionDir)Nugets\net8</PackageOutputPath>
    <AssemblyVersion>$(Version).0</AssemblyVersion>
  </PropertyGroup>

  <!-- .NET 10 specific configuration (default) -->
  <PropertyGroup Condition="!$(MSBuildProjectName.EndsWith('Net8'))">
    <Version>10.0.0</Version>
    <PackageOutputPath>$(SolutionDir)Nugets\net10</PackageOutputPath>
    <AssemblyVersion>$(Version).0</AssemblyVersion>
  </PropertyGroup>

  <!-- Common package references and files -->
  <ItemGroup>
    <PackageReference Include="StrongNamer" Version="0.2.5" PrivateAssets="All" />
  </ItemGroup>
  <ItemGroup>
    <None Include="$(MSBuildThisFileDirectory)README.md" Pack="true" PackagePath="" />
    <None Include="$(MSBuildThisFileDirectory)LICENSE.txt" Pack="true" PackagePath="" />
    <None Include="EFCoreBulk.png" Pack="true" PackagePath="" />
  </ItemGroup>
</Project>
```

**Key Features:**
- **Conditional logic based on project name:** If project name ends with `Net8`, uses version 8.1.3 and outputs to `Nugets\net8`
- **Default behavior:** Projects without `Net8` suffix use version 10.0.0 and output to `Nugets\net10`
- **Automatic package generation:** `GeneratePackageOnBuild` set to `True`
- **Centralized metadata:** All package metadata is defined once

### 2. Project Files (.csproj)

Each project has two versions: a default one for the latest .NET and a `.Net8` variant.

#### Example: Core Library

**EFCore.BulkExtensions.Core.csproj (.NET 10):**
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Title>EFCore.BulkExtensions.Core</Title>
    <RootNamespace>EFCore.BulkExtensions</RootNamespace>
    <Description>EntityFramework .Net EFCore EF Core Bulk Batch Extensions for Insert Update Delete Read (CRUD) operations</Description>
    <NuGetAudit>false</NuGetAudit>
    <Version>10.0.0-rc.1</Version>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.EntityFrameworkCore.Relational" Version="10.0.0" />
    <PackageReference Include="MedallionTopologicalSort" Version="1.0.0" />
    <PackageReference Include="NetTopologySuite" Version="2.6.0" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="EFCore.BulkExtensions.SqlServer,PublicKey='...'" />
    <InternalsVisibleTo Include="EFCore.BulkExtensions.Sqlite,PublicKey='...'" />
    <!-- ... other InternalsVisibleTo entries ... -->
  </ItemGroup>
</Project>
```

**EFCore.BulkExtensions.Core.Net8.csproj (.NET 8):**
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Title>EFCore.BulkExtensions.Core</Title>
    <RootNamespace>EFCore.BulkExtensions</RootNamespace>
    <Description>EntityFramework .Net EFCore EF Core Bulk Batch Extensions for Insert Update Delete Read (CRUD) operations</Description>
    <PackageId>EFCore.BulkExtensions.Core</PackageId>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.EntityFrameworkCore.Relational" Version="8.0.15" />
    <PackageReference Include="MedallionTopologicalSort" Version="1.0.0" />
    <PackageReference Include="NetTopologySuite" Version="2.6.0" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="EFCore.BulkExtensions.SqlServer.Net8,PublicKey='...'" />
    <InternalsVisibleTo Include="EFCore.BulkExtensions.Sqlite.Net8,PublicKey='...'" />
    <!-- ... other InternalsVisibleTo entries pointing to .Net8 projects ... -->
  </ItemGroup>
</Project>
```

**Key Differences:**
- **TargetFramework:** `net10.0` vs `net8.0`
- **PackageReference versions:** Different EF Core versions (10.0.0 vs 8.0.15)
- **InternalsVisibleTo:** References point to matching version projects (with or without `.Net8` suffix)
- **PackageId:** Explicitly set in .Net8 variant

#### Example: Database Provider (SqlServer)

**EFCore.BulkExtensions.SqlServer.csproj (.NET 10):**
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Title>EFCore.BulkExtensions.SqlServer</Title>
    <Description>EntityFramework .Net EFCore EF Core Bulk Batch Extensions for Insert Update Delete Read (CRUD) operations on SQL Server</Description>
    <PackageTags>$(PackageTags) SQLServer</PackageTags>
    <Version>10.0.0-rc.1</Version>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Data.SqlClient" Version="6.1.3" />
    <PackageReference Include="NetTopologySuite.IO.SqlServerBytes" Version="2.1.0" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.SqlServer.HierarchyId" Version="10.0.0" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\EFCore.BulkExtensions.Core\EFCore.BulkExtensions.Core.csproj" />
  </ItemGroup>
</Project>
```

**EFCore.BulkExtensions.SqlServer.Net8.csproj (.NET 8):**
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Title>EFCore.BulkExtensions.SqlServer</Title>
    <Description>EntityFramework .Net EFCore EF Core Bulk Batch Extensions for Insert Update Delete Read (CRUD) operations on SQL Server</Description>
    <PackageTags>$(PackageTags) SQLServer</PackageTags>
    <PackageId>EFCore.BulkExtensions.SqlServer</PackageId>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Data.SqlClient" Version="6.0.2" />
    <PackageReference Include="NetTopologySuite.IO.SqlServerBytes" Version="2.1.0" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.SqlServer.HierarchyId" Version="8.0.15" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\EFCore.BulkExtensions.Core\EFCore.BulkExtensions.Core.Net8.csproj" />
  </ItemGroup>
</Project>
```

**Key Differences:**
- **ProjectReference:** Points to matching version (`Core.csproj` vs `Core.Net8.csproj`)
- **PackageReference versions:** Different dependency versions

#### Example: Meta Package (Main Package)

**EFCore.BulkExtensions.csproj (.NET 10/9):**
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <Title>EFCore.BulkExtensions</Title>
    <Description>EntityFramework .Net EFCore EF Core Bulk Batch Extensions for Insert Update Delete Read (CRUD) operations on SQL Server, PostgreSQL, MySQL, SQLite</Description>
    <PackageTags>$(PackageTags) SQLServer PostgreSQL MySQL SQLite</PackageTags>
    <IncludeBuildOutput>false</IncludeBuildOutput>
    <NoWarn>$(NoWarn);NU5128</NoWarn>
    <NuGetAudit>false</NuGetAudit>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\EFCore.BulkExtensions.SqlServer\EFCore.BulkExtensions.SqlServer.csproj" />
    <ProjectReference Include="..\EFCore.BulkExtensions.PostgreSql\EFCore.BulkExtensions.PostgreSql.csproj" />
    <ProjectReference Include="..\EFCore.BulkExtensions.MySql\EFCore.BulkExtensions.MySql.csproj" />
    <ProjectReference Include="..\EFCore.BulkExtensions.Oracle\EFCore.BulkExtensions.Oracle.csproj" />
    <ProjectReference Include="..\EFCore.BulkExtensions.Sqlite\EFCore.BulkExtensions.Sqlite.csproj" />
  </ItemGroup>
</Project>
```

**EFCore.BulkExtensions.Net8.csproj (.NET 8):**
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Title>EFCore.BulkExtensions</Title>
    <Description>EntityFramework .Net EFCore EF Core Bulk Batch Extensions for Insert Update Delete Read (CRUD) operations on SQL Server, PostgreSQL, MySQL, SQLite</Description>
    <PackageTags>$(PackageTags) SQLServer PostgreSQL MySQL SQLite</PackageTags>
    <IncludeBuildOutput>false</IncludeBuildOutput>
    <NoWarn>$(NoWarn);NU5128</NoWarn>
    <NuGetAudit>false</NuGetAudit>
    <PackageId>EFCore.BulkExtensions</PackageId>
    <IsPackable>true</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\EFCore.BulkExtensions.SqlServer\EFCore.BulkExtensions.SqlServer.Net8.csproj" />
    <ProjectReference Include="..\EFCore.BulkExtensions.PostgreSql\EFCore.BulkExtensions.PostgreSql.Net8.csproj" />
    <ProjectReference Include="..\EFCore.BulkExtensions.MySql\EFCore.BulkExtensions.MySql.Net8.csproj" />
    <ProjectReference Include="..\EFCore.BulkExtensions.Oracle\EFCore.BulkExtensions.Oracle.Net8.csproj" />
    <ProjectReference Include="..\EFCore.BulkExtensions.Sqlite\EFCore.BulkExtensions.Sqlite.Net8.csproj" />
  </ItemGroup>
</Project>
```

**Special Properties:**
- `IncludeBuildOutput` set to `false` - this is a meta-package that only references other packages
- All ProjectReferences point to matching version projects

### 3. Solution Files (.sln)

**EFCore.BulkExtensions.sln** - References all .NET 10 projects:
- `EFCore.BulkExtensions.Tests\EFCore.BulkExtensions.Tests.csproj`
- `EFCore.BulkExtensions\EFCore.BulkExtensions.csproj`
- `EFCore.BulkExtensions.Core\EFCore.BulkExtensions.Core.csproj`
- `EFCore.BulkExtensions.SqlServer\EFCore.BulkExtensions.SqlServer.csproj`
- etc.

**EFCore.BulkExtensions.Net8.sln** - References all .NET 8 projects:
- `EFCore.BulkExtensions.Tests\EFCore.BulkExtensions.Tests.Net8.csproj`
- `EFCore.BulkExtensions\EFCore.BulkExtensions.Net8.csproj`
- `EFCore.BulkExtensions.Core\EFCore.BulkExtensions.Core.Net8.csproj`
- `EFCore.BulkExtensions.SqlServer\EFCore.BulkExtensions.SqlServer.Net8.csproj`
- etc.

---

## How It Works

### Build Flow

1. **Open Solution:** Developer or CI/CD opens either `.sln` or `.Net8.sln`

2. **MSBuild Loads Projects:** MSBuild loads all `.csproj` files referenced in the solution

3. **Directory.Build.props Applied:** MSBuild automatically imports `Directory.Build.props` for each project
   - Checks project name: Does it end with `Net8`?
   - **If YES:** Sets `Version=8.1.3` and `PackageOutputPath=Nugets\net8`
   - **If NO:** Sets `Version=10.0.0` and `PackageOutputPath=Nugets\net10`

4. **Build Compilation:** Each project compiles with its specific:
   - TargetFramework (.NET 8 or .NET 10)
   - Package dependencies (EF Core 8.x or 10.x)

5. **Package Generation:** With `GeneratePackageOnBuild=True`:
   - Each project creates a `.nupkg` file
   - Package uses version from Directory.Build.props
   - Package saved to designated output path

### Naming Convention Magic

The key to this approach is the **naming convention** in `Directory.Build.props`:

```xml
<PropertyGroup Condition="$(MSBuildProjectName.EndsWith('Net8'))">
  <Version>8.1.3</Version>
  <PackageOutputPath>$(SolutionDir)Nugets\net8</PackageOutputPath>
  <AssemblyVersion>$(Version).0</AssemblyVersion>
</PropertyGroup>
```

- **MSBuildProjectName:** The project file name without extension
  - For `EFCore.BulkExtensions.Core.Net8.csproj` → `EFCore.BulkExtensions.Core.Net8`
  - For `EFCore.BulkExtensions.Core.csproj` → `EFCore.BulkExtensions.Core`

- **Condition Check:** If name ends with `Net8`, special properties apply

- **Version Separation:** Different version numbers ensure NuGet treats them as separate releases

---

## Step-by-Step Implementation Guide

### Prerequisites
- .NET SDK with multiple runtime versions installed (e.g., .NET 8 and .NET 10)
- Understanding of NuGet package versioning
- A working .NET project to convert

### Step 1: Create Directory.Build.props

Create a `Directory.Build.props` file in your solution root:

```xml
<Project>
  <PropertyGroup>
    <!-- Common build properties -->
    <Authors>YourName</Authors>
    <Company>YourCompany</Company>
    <PackageProjectUrl>https://github.com/yourname/yourproject</PackageProjectUrl>
    <RepositoryUrl>https://github.com/yourname/yourproject</RepositoryUrl>
    <RepositoryType>Git</RepositoryType>
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <LangVersion>latest</LangVersion>
    <GeneratePackageOnBuild>True</GeneratePackageOnBuild>
  </PropertyGroup>

  <!-- .NET 8 specific settings -->
  <PropertyGroup Condition="$(MSBuildProjectName.EndsWith('Net8'))">
    <Version>8.1.0</Version>
    <PackageOutputPath>$(SolutionDir)Nugets\net8</PackageOutputPath>
    <AssemblyVersion>$(Version).0</AssemblyVersion>
  </PropertyGroup>

  <!-- Latest .NET specific settings -->
  <PropertyGroup Condition="!$(MSBuildProjectName.EndsWith('Net8'))">
    <Version>10.0.0</Version>
    <PackageOutputPath>$(SolutionDir)Nugets\net10</PackageOutputPath>
    <AssemblyVersion>$(Version).0</AssemblyVersion>
  </PropertyGroup>
</Project>
```

### Step 2: Duplicate Project Files

For each `.csproj` in your solution:

1. **Copy the file:**
   ```bash
   cp MyProject.csproj MyProject.Net8.csproj
   ```

2. **Update the .NET 8 version:**
   - Change `<TargetFramework>` to `net8.0`
   - Add `<PackageId>MyProject</PackageId>` (ensure same ID as main package)
   - Update package references to .NET 8 compatible versions
   - Update any `<ProjectReference>` to point to `.Net8.csproj` files

3. **Update the latest version:**
   - Ensure `<TargetFramework>` is set to latest (e.g., `net10.0`)
   - Ensure `<ProjectReference>` points to non-`.Net8.csproj` files

### Step 3: Handle Project Dependencies

**Critical:** Project references must match the version being built.

**Example: MyApp.csproj**
```xml
<ItemGroup>
  <ProjectReference Include="..\MyLibrary\MyLibrary.csproj" />
</ItemGroup>
```

**Example: MyApp.Net8.csproj**
```xml
<ItemGroup>
  <ProjectReference Include="..\MyLibrary\MyLibrary.Net8.csproj" />
</ItemGroup>
```

### Step 4: Handle InternalsVisibleTo (if applicable)

If using `InternalsVisibleTo` for signed assemblies:

**MyLibrary.csproj:**
```xml
<ItemGroup>
  <InternalsVisibleTo Include="MyApp,PublicKey='...'" />
</ItemGroup>
```

**MyLibrary.Net8.csproj:**
```xml
<ItemGroup>
  <InternalsVisibleTo Include="MyApp.Net8,PublicKey='...'" />
</ItemGroup>
```

### Step 5: Create Solution Files

Create separate solutions:

**Option A: Using dotnet CLI**
```bash
# Create .NET 8 solution
dotnet new sln -n MySolution.Net8
dotnet sln MySolution.Net8.sln add MyProject/MyProject.Net8.csproj
dotnet sln MySolution.Net8.sln add MyLibrary/MyLibrary.Net8.csproj

# Create latest .NET solution
dotnet new sln -n MySolution
dotnet sln MySolution.sln add MyProject/MyProject.csproj
dotnet sln MySolution.sln add MyLibrary/MyLibrary.csproj
```

**Option B: Duplicate and edit .sln file manually**
1. Copy existing `.sln` to `.Net8.sln`
2. Open in text editor
3. Replace all `.csproj` references with `.Net8.csproj` equivalents

### Step 6: Create Output Directories (Optional)

```bash
mkdir -p Nugets/net8
mkdir -p Nugets/net10
```

*Note: These will be created automatically during build, but creating them beforehand helps with .gitignore.*

### Step 7: Update .gitignore

Add to your `.gitignore`:
```
# NuGet outputs
Nugets/
*.nupkg
*.snupkg
```

---

## Building and Publishing

### Building Locally

**Build .NET 8 packages:**
```bash
dotnet build -c Release MySolution.Net8.sln
```
Output: `Nugets\net8\*.nupkg`

**Build .NET 10 packages:**
```bash
dotnet build -c Release MySolution.sln
```
Output: `Nugets\net10\*.nupkg`

### Publishing to NuGet.org

**Option 1: Manual Upload**
1. Build both versions
2. Go to https://www.nuget.org/packages/manage/upload
3. Upload `Nugets\net8\YourPackage.8.1.0.nupkg`
4. Upload `Nugets\net10\YourPackage.10.0.0.nupkg`

**Option 2: Using dotnet CLI**
```bash
dotnet nuget push Nugets/net8/*.nupkg -k YOUR_API_KEY -s https://api.nuget.org/v3/index.json
dotnet nuget push Nugets/net10/*.nupkg -k YOUR_API_KEY -s https://api.nuget.org/v3/index.json
```

### CI/CD Pipeline Example (GitHub Actions)

```yaml
name: Build and Publish NuGet

on:
  push:
    tags:
      - 'v*'

jobs:
  build-net8:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v3
      - name: Setup .NET 8
        uses: actions/setup-dotnet@v3
        with:
          dotnet-version: 8.0.x
      - name: Build .NET 8
        run: dotnet build -c Release MySolution.Net8.sln
      - name: Publish .NET 8 NuGet
        run: dotnet nuget push Nugets/net8/*.nupkg -k ${{ secrets.NUGET_API_KEY }} -s https://api.nuget.org/v3/index.json

  build-net10:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v3
      - name: Setup .NET 10
        uses: actions/setup-dotnet@v3
        with:
          dotnet-version: 10.0.x
      - name: Build .NET 10
        run: dotnet build -c Release MySolution.sln
      - name: Publish .NET 10 NuGet
        run: dotnet nuget push Nugets/net10/*.nupkg -k ${{ secrets.NUGET_API_KEY }} -s https://api.nuget.org/v3/index.json
```

---

## Best Practices

### 1. **Maintain Source Code in One Location**
- Keep only ONE copy of source code (`.cs` files)
- DO NOT duplicate source files between projects
- Both `.csproj` files should reference the SAME source files
- Use `<Compile Include="..\SharedSource\**\*.cs" />` if needed

### 2. **Consistent Naming Convention**
- Use a clear, consistent suffix (e.g., `.Net8`)
- Apply the suffix to ALL projects in the dependency chain
- Example chain: `App.Net8.csproj` → `Lib.Net8.csproj` → `Core.Net8.csproj`

### 3. **Version Number Strategy**
- **Major version** should match the .NET version (8.x.x for .NET 8, 10.x.x for .NET 10)
- **Minor/Patch** versions can be synchronized or independent
- Document your versioning strategy in README

Example:
- .NET 8: `8.1.3` (EF Core 8.x compatible)
- .NET 10: `10.0.0` (EF Core 10.x compatible)

### 4. **Dependency Version Management**
- Keep dependency versions aligned with target framework
- Use version-specific packages (e.g., EntityFrameworkCore 8.x vs 10.x)
- Test each package version independently

### 5. **Testing**
- Create separate test projects for each version:
  - `MyProject.Tests.csproj` (for .NET 10)
  - `MyProject.Tests.Net8.csproj` (for .NET 8)
- Run tests for both versions before publishing

### 6. **Package Metadata**
- Use `Directory.Build.props` for shared metadata
- Keep `PackageId` consistent across versions
- Use clear `PackageReleaseNotes` indicating .NET version

### 7. **Breaking Changes**
- When .NET 10 adds breaking changes, isolate them in the main `.csproj`
- Keep .NET 8 version stable until users migrate
- Document migration paths

### 8. **Documentation**
- Clearly document which NuGet version supports which .NET version
- Include framework requirements in README
- Provide migration guides between major versions

### 9. **Git Workflow**
- Add all `.csproj` files to source control
- Add `.sln` files to source control
- Ignore `Nugets/` output directory
- Use git tags for releases: `v8.1.3-net8`, `v10.0.0-net10`

### 10. **Backward Compatibility**
- Maintain .NET 8 packages as long as there's demand
- Consider LTS (Long Term Support) versions
- Plan deprecation timeline and communicate clearly

---

## Common Pitfalls and Solutions

### ❌ Problem: Projects reference the wrong version
**Symptom:** Build errors like "cannot find assembly"

**Solution:** Ensure all `<ProjectReference>` paths match:
- In `.Net8.csproj` → reference `.Net8.csproj` dependencies
- In `.csproj` → reference `.csproj` dependencies

---

### ❌ Problem: Version numbers don't apply correctly
**Symptom:** All packages have the same version number

**Solution:** Verify `Directory.Build.props` conditional logic:
```xml
<PropertyGroup Condition="$(MSBuildProjectName.EndsWith('Net8'))">
```
Ensure project file names actually end with `Net8`.

---

### ❌ Problem: Packages output to wrong directory
**Symptom:** All `.nupkg` files in same folder

**Solution:** Check `PackageOutputPath` in `Directory.Build.props` and ensure directories exist.

---

### ❌ Problem: InternalsVisibleTo doesn't work
**Symptom:** Internal classes not visible to other assemblies

**Solution:**
- Verify `InternalsVisibleTo` points to correct assembly name (with or without `.Net8`)
- If using strong naming, verify `PublicKey` is correct
- Check that both assemblies are signed consistently

---

### ❌ Problem: NuGet restore fails
**Symptom:** "Unable to find package" errors

**Solution:**
- Ensure package dependency versions match the framework
- Check that EF Core/ASP.NET versions are compatible with target framework
- Verify NuGet sources are configured correctly

---

## Advanced Topics

### Using Shared Project References

If you have shared code that doesn't need different versions:

```xml
<!-- SharedUtilities.csproj - single version, multi-target -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
  </PropertyGroup>
</Project>
```

Then reference from both versions:
```xml
<!-- MyApp.csproj and MyApp.Net8.csproj -->
<ItemGroup>
  <ProjectReference Include="..\SharedUtilities\SharedUtilities.csproj" />
</ItemGroup>
```

### Conditional Compilation

Use preprocessor directives for framework-specific code:

```csharp
#if NET8_0
    // .NET 8 specific code
#elif NET10_0
    // .NET 10 specific code
#else
    // Fallback code
#endif
```

### Package Consolidation

Create a meta-package that references all sub-packages (like `EFCore.BulkExtensions` does):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <IncludeBuildOutput>false</IncludeBuildOutput>
    <PackageId>MyMetaPackage</PackageId>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\SubPackage1\SubPackage1.Net8.csproj" />
    <ProjectReference Include="..\SubPackage2\SubPackage2.Net8.csproj" />
  </ItemGroup>
</Project>
```

---

## Summary

The multi-targeting strategy employed by EFCore.BulkExtensions is:

✅ **Duplicate project files** (not source code) with version suffix
✅ **Separate solution files** for each .NET version
✅ **Central configuration** via `Directory.Build.props` using naming conventions
✅ **Automatic versioning** and package output routing
✅ **Consistent cross-references** between matching versions

This approach provides:
- Clean separation of concerns
- Easy maintenance (single source code)
- Flexible versioning per framework
- Simple CI/CD integration
- Clear package organization

By following this guide, you can implement the same strategy in your projects to support multiple .NET versions with separate NuGet packages.
