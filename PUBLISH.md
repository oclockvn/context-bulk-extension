## Publishing New Versions

### Prerequisites

1. **Trusted Publishing Setup on nuget.org:**
   - Log into [nuget.org](https://www.nuget.org)
   - Navigate to your account settings → **Trusted Publishing**
   - Add a new trusted publishing policy:
     - **Repository Owner:** Your GitHub username/org
     - **Repository:** `ContextBulkExtension` (or your actual repo name)
     - **Workflow File:** `publish-nuget.yml`
     - **Environment:** (leave empty if not using environments)

### Publishing Process

#### Option 1: Using PowerShell Script (Recommended)

Use the automated script to publish a new version:

```powershell
# Publish version 1.0.0
.\scripts\publish-nuget.ps1 -Version "1.0.0"

# Dry run to see what would happen
.\scripts\publish-nuget.ps1 -Version "1.0.0" -DryRun

# Skip build and tests (if already verified)
.\scripts\publish-nuget.ps1 -Version "1.0.0" -SkipBuild -SkipTest
```

The script will:
1. Update `BaseVersion` in `Directory.Build.props`
2. Build the projects (unless `-SkipBuild` is specified)
3. Run tests (unless `-SkipTest` is specified)
4. Create a git tag (e.g., `v1.0.0`)
5. Push the tag to remote, which triggers the GitHub Actions workflow

#### Option 2: Manual Process

1. Update `BaseVersion` in `Directory.Build.props`:
   ```xml
   <BaseVersion>1.0</BaseVersion>  <!-- For version 8.1.0 -->
   ```

2. Create and push a git tag:
   ```bash
   git tag -a v1.0.0 -m "Release version 1.0.0"
   git push origin v1.0.0
   ```

3. The GitHub Actions workflow will automatically:
   - Build the packaging projects
   - Pack the NuGet packages
   - Authenticate using Trusted Publishing (OIDC)
   - Publish to nuget.org

#### Option 3: Manual Workflow Dispatch

1. Go to the **Actions** tab in GitHub
2. Select **Publish NuGet Package** workflow
3. Click **Run workflow**
4. Enter the version (e.g., `1.0.0`)
5. Click **Run workflow**

### Version Tag Format

Tags must follow semantic versioning: `v1.0.0`, `v1.0.1`, `v2.0.0`, etc.

The workflow extracts the version number (without the 'v' prefix) and sets it as `BaseVersion` in `Directory.Build.props`. The final package version will be `8.{BaseVersion}` for EF Core 8.x packages (e.g., `8.1.0`).
