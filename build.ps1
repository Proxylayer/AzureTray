#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Build script for the AzureTray solution.

.DESCRIPTION
    Three modes:
      (default)   simple  : quiet Release build of the whole solution; pass/fail summary + errors.
      -Detailed           : full diagnostic (normal verbosity) Release build; use only to diagnose a failure.
      -Target <name>      : build a single project and show only lines relevant to it.

    Always builds --configuration Release. Before building it silently stops any
    running AzureTray.exe so file locks do not break MSBuild.

.EXAMPLE
    ./build.ps1
    ./build.ps1 -Detailed
    ./build.ps1 -Target AzureTray.Plugin.Contracts
#>
[CmdletBinding()]
param(
    [switch]$Detailed,
    [string]$Target
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$solution = Join-Path $root 'AzureTray.sln'

# Release builds lock AzureTray.exe; stop it first (ignore if not running).
try { Get-Process -Name 'AzureTray' -ErrorAction Stop | Stop-Process -Force -ErrorAction SilentlyContinue } catch {}

function Resolve-Target {
    param([string]$Name)
    $proj = Get-ChildItem -Path (Join-Path $root 'src') -Recurse -Filter '*.csproj' |
        Where-Object { [IO.Path]::GetFileNameWithoutExtension($_.Name) -eq $Name }
    if (-not $proj) { Write-Error "No project named '$Name' under src/."; exit 2 }
    return $proj.FullName
}

if ($Target) {
    $buildPath = Resolve-Target -Name $Target
    Write-Host "Building target project: $Target (Release)" -ForegroundColor Cyan
    $verbosity = if ($Detailed) { 'normal' } else { 'minimal' }
    & dotnet build $buildPath --configuration Release --nologo -v $verbosity |
        Where-Object { $_ -match [regex]::Escape($Target) -or $_ -match 'error|warning|Build succeeded|Build FAILED' }
    exit $LASTEXITCODE
}

if ($Detailed) {
    Write-Host "Building solution (Release, detailed)" -ForegroundColor Cyan
    & dotnet build $solution --configuration Release --nologo -v normal
    exit $LASTEXITCODE
}

Write-Host "Building solution (Release)" -ForegroundColor Cyan
$output = & dotnet build $solution --configuration Release --nologo -v minimal 2>&1
$exit = $LASTEXITCODE
$issues = $output | Where-Object { $_ -match 'error|warning|Build succeeded|Build FAILED' }
if ($issues) { $issues | ForEach-Object { Write-Host $_ } }
if ($exit -eq 0) {
    Write-Host "BUILD PASSED" -ForegroundColor Green
} else {
    Write-Host "BUILD FAILED (exit $exit)" -ForegroundColor Red
    if (-not $issues) { $output | ForEach-Object { Write-Host $_ } }
}
exit $exit
