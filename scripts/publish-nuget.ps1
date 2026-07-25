# PowerShell script to pack and publish SqlServer + Postgres NuGet packages
# Version = {8|10}.{BaseVersion}.{PatchNumber} from Directory.Build.props

param(
    [Parameter(Mandatory = $false)]
    [string]$BaseVersion,

    [Parameter(Mandatory = $false)]
    [int]$PatchNumber = 0,

    [Parameter(Mandatory = $false)]
    [switch]$SkipBuild,

    [Parameter(Mandatory = $false)]
    [switch]$SkipTest,

    [Parameter(Mandatory = $false)]
    [switch]$SkipTag,

    [Parameter(Mandatory = $false)]
    [switch]$SkipPush,

    [Parameter(Mandatory = $false)]
    [switch]$LocalPush,

    [Parameter(Mandatory = $false)]
    [string]$ApiKey,

    [Parameter(Mandatory = $false)]
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepoRoot

$DirectoryBuildPropsPath = "Directory.Build.props"

if ($BaseVersion -and $BaseVersion -notmatch '^\d+\.\d+$') {
    Write-Error "Invalid BaseVersion. Expected format: X.Y (e.g., 0.20)"
    exit 1
}

if ($PatchNumber -lt 0) {
    Write-Error "PatchNumber must be >= 0"
    exit 1
}

$Projects = @(
    @{ Path = "ContextBulkExtension.SqlServer/ContextBulkExtension.SqlServer.Net8.csproj"; Label = "net8 SqlServer" },
    @{ Path = "ContextBulkExtension.Postgres/ContextBulkExtension.Postgres.Net8.csproj"; Label = "net8 Postgres" },
    @{ Path = "ContextBulkExtension.SqlServer/ContextBulkExtension.SqlServer.csproj"; Label = "net10 SqlServer" },
    @{ Path = "ContextBulkExtension.Postgres/ContextBulkExtension.Postgres.csproj"; Label = "net10 Postgres" }
)

Write-Host "Publish SqlServer + Postgres (Core embedded, not packed)" -ForegroundColor Cyan
if ($BaseVersion) { Write-Host "BaseVersion -> $BaseVersion" -ForegroundColor Cyan }
Write-Host "PatchNumber = $PatchNumber" -ForegroundColor Cyan

if ($DryRun) {
    Write-Host "`n[DRY RUN] Would:" -ForegroundColor Yellow
    if ($BaseVersion) { Write-Host "  1. Set BaseVersion in $DirectoryBuildPropsPath to $BaseVersion" }
    else { Write-Host "  1. Keep existing BaseVersion" }
    Write-Host "  2. Build/pack SqlServer + Postgres (net8 + net10) unless -SkipBuild"
    Write-Host "  3. Run tests unless -SkipTest"
    if ($LocalPush) { Write-Host "  4. Local nuget push Nugets/net8 + Nugets/net10 (-ApiKey or NUGET_API_KEY)" }
    elseif (-not $SkipTag) { Write-Host "  4. Create/push git tag (triggers CI) unless -SkipTag/-SkipPush" }
    else { Write-Host "  4. Skip tag/push" }
    exit 0
}

if (-not (Test-Path $DirectoryBuildPropsPath)) {
    Write-Error "Directory.Build.props not found"
    exit 1
}

if ($BaseVersion) {
    Write-Host "`nUpdating BaseVersion..." -ForegroundColor Green
    $content = Get-Content $DirectoryBuildPropsPath -Raw
    $content = $content -replace '(<BaseVersion>)(.*?)(</BaseVersion>)', ('<BaseVersion>' + $BaseVersion + '</BaseVersion>')
    Set-Content -Path $DirectoryBuildPropsPath -Value $content -NoNewline
    Write-Host "Updated BaseVersion to $BaseVersion" -ForegroundColor Green
}

$resolvedBase = ([xml](Get-Content $DirectoryBuildPropsPath)).Project.PropertyGroup.BaseVersion |
    Where-Object { $_ } |
    Select-Object -First 1
$tagName = "v8.$resolvedBase.$PatchNumber"

if (-not $SkipBuild) {
    Write-Host "`nBuilding and packing providers..." -ForegroundColor Green
    foreach ($p in $Projects) {
        Write-Host "  $($p.Label): $($p.Path)" -ForegroundColor DarkGray
        dotnet build $p.Path --configuration Release --no-incremental -p:PatchNumber=$PatchNumber
        if ($LASTEXITCODE -ne 0) { Write-Error "Build failed: $($p.Path)"; exit 1 }
        # GeneratePackageOnBuild writes to Nugets/; explicit pack keeps output consistent
        dotnet pack $p.Path --configuration Release --no-build -p:PatchNumber=$PatchNumber
        if ($LASTEXITCODE -ne 0) { Write-Error "Pack failed: $($p.Path)"; exit 1 }
    }
    Write-Host "Build/pack OK" -ForegroundColor Green
}

if (-not $SkipTest) {
    Write-Host "`nRunning tests..." -ForegroundColor Green
    dotnet test ContextBulkExtension.Tests/ContextBulkExtension.Tests.csproj --configuration Release -v q
    if ($LASTEXITCODE -ne 0) { Write-Error "net10 tests failed"; exit 1 }
    dotnet test ContextBulkExtension.Tests/ContextBulkExtension.Tests.Net8.csproj --configuration Release -v q
    if ($LASTEXITCODE -ne 0) { Write-Error "net8 tests failed"; exit 1 }
    Write-Host "Tests OK" -ForegroundColor Green
}

if ($LocalPush) {
    if (-not $ApiKey) { $ApiKey = $env:NUGET_API_KEY }
    if (-not $ApiKey) {
        Write-Error "-ApiKey required for -LocalPush (or set NUGET_API_KEY)"
        exit 1
    }
    Write-Host "`nPushing to nuget.org..." -ForegroundColor Green
    foreach ($dir in @("Nugets/net8", "Nugets/net10")) {
        if (-not (Test-Path $dir)) { Write-Error "Missing $dir"; exit 1 }
        Get-ChildItem "$dir/*.nupkg" | ForEach-Object {
            # Skip Core if any leftover nupkg exists
            if ($_.Name -like "ContextBulkExtension.Core.*") {
                Write-Host "  skip $($_.Name)" -ForegroundColor DarkYellow
                return
            }
            Write-Host "  push $($_.Name)" -ForegroundColor DarkGray
            dotnet nuget push $_.FullName `
                --api-key $ApiKey `
                --source https://api.nuget.org/v3/index.json `
                --skip-duplicate
            if ($LASTEXITCODE -ne 0) { Write-Error "Push failed: $($_.Name)"; exit 1 }
        }
    }
    Write-Host "Local push done" -ForegroundColor Green
    exit 0
}

if ($SkipTag) {
    Write-Host "`nSkipTag set; packages in Nugets/. Done." -ForegroundColor Yellow
    exit 0
}

Write-Host "`nChecking git status..." -ForegroundColor Green
$gitStatus = git status --porcelain
if ($gitStatus) {
    Write-Warning "Working directory has uncommitted changes:"
    Write-Host $gitStatus
    $response = Read-Host "Continue anyway? (y/N)"
    if ($response -ne 'y' -and $response -ne 'Y') {
        Write-Host "Aborted" -ForegroundColor Yellow
        exit 0
    }
}

Write-Host "`nCreating git tag: $tagName" -ForegroundColor Green
$existingTag = git tag -l $tagName
if ($existingTag) {
    Write-Warning "Tag $tagName already exists"
    $response = Read-Host "Delete and recreate? (y/N)"
    if ($response -eq 'y' -or $response -eq 'Y') {
        git tag -d $tagName
        if ($LASTEXITCODE -ne 0) { Write-Error "Failed to delete local tag"; exit 1 }
    }
    else {
        Write-Host "Aborted" -ForegroundColor Yellow
        exit 0
    }
}

git tag -a $tagName -m "Release $tagName (SqlServer + Postgres)"
if ($LASTEXITCODE -ne 0) { Write-Error "Failed to create tag"; exit 1 }
Write-Host "Tag created: $tagName" -ForegroundColor Green

if ($SkipPush) {
    Write-Host "SkipPush set. Push manually: git push origin $tagName" -ForegroundColor Yellow
    exit 0
}

$response = Read-Host "Push tag to remote? (Y/n)"
if ($response -ne 'n' -and $response -ne 'N') {
    git push origin $tagName
    if ($LASTEXITCODE -ne 0) { Write-Error "Failed to push tag"; exit 1 }
    Write-Host "Tag pushed. GitHub Actions will publish packages." -ForegroundColor Cyan
}
else {
    Write-Host "Tag local only. Push: git push origin $tagName" -ForegroundColor Yellow
}

Write-Host "`nDone." -ForegroundColor Green
