# PowerShell script to automate NuGet package publishing
# Updates BaseVersion in Directory.Build.props and creates a git tag

param(
    [Parameter(Mandatory=$true)]
    [string]$Version,
    
    [Parameter(Mandatory=$false)]
    [switch]$SkipBuild,
    
    [Parameter(Mandatory=$false)]
    [switch]$SkipTest,
    
    [Parameter(Mandatory=$false)]
    [switch]$DryRun
)

# Validate version format (semantic versioning: X.Y.Z)
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    Write-Error "Invalid version format. Expected format: X.Y.Z (e.g., 1.0.0)"
    exit 1
}

$BaseVersion = $Version -replace '^\d+\.', ''
$DirectoryBuildPropsPath = "NugetPackages\Directory.Build.props"

Write-Host "Publishing NuGet package version: $Version" -ForegroundColor Cyan
Write-Host "BaseVersion will be set to: $BaseVersion" -ForegroundColor Cyan

if ($DryRun) {
    Write-Host "`n[DRY RUN] Would perform the following actions:" -ForegroundColor Yellow
    Write-Host "  1. Update BaseVersion in $DirectoryBuildPropsPath to $BaseVersion"
    Write-Host "  2. Build projects (unless -SkipBuild is specified)"
    Write-Host "  3. Run tests (unless -SkipTest is specified)"
    Write-Host "  4. Create git tag: v$Version"
    Write-Host "  5. Push tag to remote"
    exit 0
}

# Check if Directory.Build.props exists
if (-not (Test-Path $DirectoryBuildPropsPath)) {
    Write-Error "Directory.Build.props not found at $DirectoryBuildPropsPath"
    exit 1
}

# Read and update Directory.Build.props
Write-Host "`nUpdating BaseVersion in Directory.Build.props..." -ForegroundColor Green
$content = Get-Content $DirectoryBuildPropsPath -Raw

# Update BaseVersion property
$content = $content -replace '(<BaseVersion>)(.*?)(</BaseVersion>)', "`$1$BaseVersion`$3"

# Write back to file
Set-Content -Path $DirectoryBuildPropsPath -Value $content -NoNewline
Write-Host "✓ Updated BaseVersion to $BaseVersion" -ForegroundColor Green

# Build projects
if (-not $SkipBuild) {
    Write-Host "`nBuilding projects..." -ForegroundColor Green
    dotnet build --configuration Release
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Build failed"
        exit 1
    }
    Write-Host "✓ Build successful" -ForegroundColor Green
}

# Run tests
if (-not $SkipTest) {
    Write-Host "`nRunning tests..." -ForegroundColor Green
    dotnet test --configuration Release --no-build
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Tests failed"
        exit 1
    }
    Write-Host "✓ Tests passed" -ForegroundColor Green
}

# Check git status
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

# Create git tag
$tagName = "v$Version"
Write-Host "`nCreating git tag: $tagName" -ForegroundColor Green

# Check if tag already exists
$existingTag = git tag -l $tagName
if ($existingTag) {
    Write-Warning "Tag $tagName already exists"
    $response = Read-Host "Delete and recreate? (y/N)"
    if ($response -eq 'y' -or $response -eq 'Y') {
        git tag -d $tagName
        if ($LASTEXITCODE -ne 0) {
            Write-Error "Failed to delete local tag"
            exit 1
        }
    } else {
        Write-Host "Aborted" -ForegroundColor Yellow
        exit 0
    }
}

git tag -a $tagName -m "Release version $Version"
if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to create tag"
    exit 1
}
Write-Host "✓ Tag created: $tagName" -ForegroundColor Green

# Push tag to remote
Write-Host "`nPushing tag to remote..." -ForegroundColor Green
$response = Read-Host "Push tag to remote? (Y/n)"
if ($response -ne 'n' -and $response -ne 'N') {
    git push origin $tagName
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to push tag"
        exit 1
    }
    Write-Host "✓ Tag pushed to remote" -ForegroundColor Green
    Write-Host "`nGitHub Actions workflow will automatically publish the package." -ForegroundColor Cyan
} else {
    Write-Host "Tag created locally. Push manually with: git push origin $tagName" -ForegroundColor Yellow
}

Write-Host "`n✓ Done!" -ForegroundColor Green

