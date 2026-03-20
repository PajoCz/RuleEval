<#
.SYNOPSIS
    Local helper script for building, testing, packing, and publishing RuleEval NuGet packages.

.DESCRIPTION
    This script is a convenience wrapper for local use only.
    The recommended and official release path is via GitHub Actions (see docs/publishing.md).

    The script requires NUGET_API_KEY to be set as an environment variable.
    It never reads or stores API keys from script arguments or source control.

.PARAMETER Version
    SemVer version to assign to the packages, e.g. 0.1.1.
    If omitted, the version from Directory.Build.props is used.

.PARAMETER SkipTests
    When specified, skips running tests before packing.

.EXAMPLE
    .\scripts\publish-nuget.ps1 -Version 0.1.1

.EXAMPLE
    .\scripts\publish-nuget.ps1 -Version 0.1.1 -SkipTests
#>

[CmdletBinding()]
param(
    [Parameter()]
    [string]$Version,

    [Parameter()]
    [switch]$SkipTests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Locate repository root
# ---------------------------------------------------------------------------
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot  = Split-Path -Parent $scriptDir

if (-not (Test-Path (Join-Path $repoRoot 'RuleEval.sln'))) {
    Write-Error "RuleEval.sln not found under '$repoRoot'. Run this script from the repository root or from the scripts/ directory."
    exit 1
}

# ---------------------------------------------------------------------------
# Validate NUGET_API_KEY
# ---------------------------------------------------------------------------
$apiKey = $env:NUGET_API_KEY
if ([string]::IsNullOrWhiteSpace($apiKey)) {
    Write-Error @"
NUGET_API_KEY environment variable is not set.

Set it for the current PowerShell session:
    `$env:NUGET_API_KEY = "<your-api-key>"

Or permanently for the current user:
    [Environment]::SetEnvironmentVariable("NUGET_API_KEY", "<your-api-key>", "User")

See docs/publishing.md for full instructions.
"@
    exit 1
}

# ---------------------------------------------------------------------------
# Validate version format (if supplied)
# ---------------------------------------------------------------------------
if ($Version -and $Version -notmatch '^\d+\.\d+\.\d+') {
    Write-Error "Version '$Version' does not match expected format X.Y.Z or X.Y.Z-suffix (e.g. 0.1.1 or 0.1.1-preview)."
    exit 1
}

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------
function Invoke-Step([string]$Label, [scriptblock]$Block) {
    Write-Host ""
    Write-Host "==> $Label" -ForegroundColor Cyan
    & $Block
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Step '$Label' failed with exit code $LASTEXITCODE."
        exit $LASTEXITCODE
    }
}

# ---------------------------------------------------------------------------
# Build properties
# ---------------------------------------------------------------------------
$versionArg = if ($Version) { "/p:Version=$Version" } else { "" }
$artifactsDir = Join-Path $repoRoot 'artifacts' 'nupkgs-local'

Write-Host ""
Write-Host "RuleEval local publish" -ForegroundColor Green
Write-Host "  Repository : $repoRoot"
Write-Host "  Version    : $(if ($Version) { $Version } else { '(from Directory.Build.props)' })"
Write-Host "  Artifacts  : $artifactsDir"
Write-Host "  Skip tests : $($SkipTests.IsPresent)"

# ---------------------------------------------------------------------------
# Steps
# ---------------------------------------------------------------------------
Push-Location $repoRoot
try {
    Invoke-Step 'Restore' {
        dotnet restore RuleEval.sln
    }

    Invoke-Step 'Build (Release)' {
        if ($versionArg) {
            dotnet build RuleEval.sln -c Release --no-restore $versionArg
        } else {
            dotnet build RuleEval.sln -c Release --no-restore
        }
    }

    if (-not $SkipTests) {
        Invoke-Step 'Test (unit)' {
            dotnet test tests/RuleEval.Tests.Unit/RuleEval.Tests.Unit.csproj -c Release --no-build --logger "console;verbosity=normal"
        }

        Invoke-Step 'Test (integration)' {
            dotnet test tests/RuleEval.Tests.Integration/RuleEval.Tests.Integration.csproj -c Release --no-build --logger "console;verbosity=normal"
        }
    } else {
        Write-Host ""
        Write-Host "==> Tests skipped (-SkipTests)" -ForegroundColor Yellow
    }

    Invoke-Step 'Pack' {
        if ($versionArg) {
            dotnet pack RuleEval.sln -c Release --no-build -o $artifactsDir $versionArg
        } else {
            dotnet pack RuleEval.sln -c Release --no-build -o $artifactsDir
        }
    }

    # List produced packages
    $packages = Get-ChildItem -Path $artifactsDir -Filter '*.nupkg'
    if ($packages.Count -eq 0) {
        Write-Error "No .nupkg files found in '$artifactsDir'. Pack step may have failed silently."
        exit 1
    }
    Write-Host ""
    Write-Host "Packages to publish:" -ForegroundColor Cyan
    $packages | ForEach-Object { Write-Host "  $($_.Name)" }

    Invoke-Step 'Push to NuGet.org' {
        dotnet nuget push "$artifactsDir/*.nupkg" `
            --api-key $apiKey `
            --source https://api.nuget.org/v3/index.json `
            --skip-duplicate
    }
} finally {
    Pop-Location
}

Write-Host ""
Write-Host "Publish complete." -ForegroundColor Green
