<#
.SYNOPSIS
  Pack + push the three plugin-side NuGet packages as a prerelease,
  from a developer machine. Used for end-to-end testing of the in-app
  plugin browser against nuget.org without waiting for a tag-driven
  CI run.

.DESCRIPTION
  Bumps the base version from Directory.Build.props by appending
  '-preview.YYYYMMDDHHMM' (UTC) and runs `dotnet pack` against the
  three packable projects:

    AzureTray.Plugin.Contracts
    AzureTray.Plugin.PIM
    AzureTray.Plugin.LAPS

  Outputs to .\NuGet\ then (unless -DryRun) pushes to nuget.org via the
  NUGET_API_KEY environment variable.

  The host EXE project is deliberately excluded -- it ships through
  Velopack, not NuGet.

.PARAMETER ApiKey
  Override the NUGET_API_KEY env var. Use only when scripting; for
  interactive use, set the env var instead.

.PARAMETER VersionSuffix
  Override the auto-generated '-preview.YYYYMMDDHHMM' suffix. Use to
  publish a deterministic prerelease (e.g. '-rc.1', '-beta'). The
  suffix is appended to the version in Directory.Build.props.

.PARAMETER DryRun
  Pack only, skip the push. Useful for verifying the .nupkg shape
  before committing to a publish.

.EXAMPLE
  # Quick test-publish using the env-var key:
  $env:NUGET_API_KEY = '<your-key>'
  ./scripts/publish-plugins-prerelease.ps1

.EXAMPLE
  # Pack-only, no push:
  ./scripts/publish-plugins-prerelease.ps1 -DryRun

.EXAMPLE
  # Named prerelease tag:
  ./scripts/publish-plugins-prerelease.ps1 -VersionSuffix '-rc.1'
#>
[CmdletBinding()]
param(
    [string]$ApiKey = $env:NUGET_API_KEY,
    [string]$VersionSuffix,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

# ---- Resolve the base version from Directory.Build.props ----------
[xml]$buildProps = Get-Content (Join-Path $repoRoot 'Directory.Build.props')
$baseVersion = $buildProps.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($baseVersion)) {
    throw 'Could not read <Version> from Directory.Build.props.'
}

if ([string]::IsNullOrWhiteSpace($VersionSuffix)) {
    $stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddHHmm')
    $VersionSuffix = "-preview.$stamp"
}

$packageVersion = "$baseVersion$VersionSuffix"
Write-Host "Packaging plugin packages at version $packageVersion" -ForegroundColor Cyan

# ---- Kill any running AzureTray.exe before we pack ----------------
# Matches the user's standing build preference: file locks from a live
# instance break MSBuild output. Silent on no-instance.
Get-Process -Name AzureTray -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue

# ---- Pack -----------------------------------------------------------
$nugetDir = Join-Path $repoRoot 'NuGet'
if (Test-Path $nugetDir) {
    Remove-Item -Recurse -Force $nugetDir
}
New-Item -ItemType Directory -Path $nugetDir | Out-Null

$packableProjects = @(
    'src/AzureTray.Plugin.Contracts/AzureTray.Plugin.Contracts.csproj',
    'src/AzureTray.Plugin.PIM/AzureTray.Plugin.PIM.csproj',
    'src/AzureTray.Plugin.LAPS/AzureTray.Plugin.LAPS.csproj'
)

foreach ($csproj in $packableProjects) {
    Write-Host ""
    Write-Host "Packing $csproj ..." -ForegroundColor DarkCyan
    dotnet pack $csproj `
        --configuration Release `
        --output $nugetDir `
        /p:Version=$packageVersion
    if ($LASTEXITCODE -ne 0) { throw "dotnet pack failed for $csproj" }
}

$produced = Get-ChildItem $nugetDir -Filter '*.nupkg' | Where-Object { $_.Name -notlike '*.symbols.nupkg' }
Write-Host ""
Write-Host "Produced packages:" -ForegroundColor Green
$produced | ForEach-Object { Write-Host "  $($_.Name)" }

# ---- Push (unless -DryRun) -----------------------------------------
if ($DryRun) {
    Write-Host ""
    Write-Host "-DryRun specified -- skipping push to nuget.org." -ForegroundColor Yellow
    return
}

if ([string]::IsNullOrWhiteSpace($ApiKey)) {
    throw 'NUGET_API_KEY env var (or -ApiKey) is required to push. Use -DryRun to pack only.'
}

Write-Host ""
Write-Host "Pushing $($produced.Count) package(s) to nuget.org ..." -ForegroundColor Cyan
foreach ($pkg in $produced) {
    Write-Host "  push $($pkg.Name)"
    # --skip-duplicate keeps this re-runnable: re-running the same
    # version (intentionally or not) is a noop instead of a fail.
    dotnet nuget push $pkg.FullName `
        --api-key $ApiKey `
        --source https://api.nuget.org/v3/index.json `
        --skip-duplicate
    if ($LASTEXITCODE -ne 0) { throw "nuget push failed for $($pkg.Name)" }
}

Write-Host ""
Write-Host "Done. Plugins will appear on nuget.org within ~30 seconds (indexing delay)." -ForegroundColor Green
Write-Host "Test discovery: open Settings -> Browse online plugins -> Browse (with 'Include prerelease' checked)." -ForegroundColor Gray
