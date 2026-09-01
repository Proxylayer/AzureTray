#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Test script for the AzureTray solution.

.DESCRIPTION
    Three modes:
      (default)   simple  : quiet, headless Release run of the whole suite;
                            prints total / passed / failed / skipped and the
                            names of failed tests only.
      -Detailed           : full diagnostic (normal verbosity) run; use only to
                            diagnose a failure that simple mode already found.
      -Target <filter>    : run a single test / class / namespace. The value is
                            passed to `dotnet test --filter`; a bare name is
                            expanded to FullyQualifiedName~<name>. Combine with
                            -Detailed to diagnose just the failing thing.

    Always runs --configuration Release. Before testing it silently stops any
    running AzureTray.exe so file locks do not break MSBuild.

.EXAMPLE
    ./test.ps1
    ./test.ps1 -Detailed
    ./test.ps1 -Target ActiveRoleAssignmentTests
    ./test.ps1 -Target "FullyQualifiedName~FormatRemaining" -Detailed
#>
[CmdletBinding()]
param(
    [switch]$Detailed,
    [string]$Target
)

$ErrorActionPreference = 'Stop'

# A failing test run is a normal outcome here, not a script error: `dotnet
# test` exits non-zero and writes the [FAIL] lines we want to report. With
# $PSNativeCommandUseErrorActionPreference on (the default in several pwsh 7
# builds) that non-zero exit becomes a terminating error under
# ErrorActionPreference 'Stop', so the script dies at the dotnet call and
# prints neither the summary nor the failed-test list - exactly when they
# matter most. Native exit codes are inspected explicitly below ($LASTEXITCODE)
# and the script still exits non-zero; genuine PowerShell errors still throw.
$PSNativeCommandUseErrorActionPreference = $false

$root = $PSScriptRoot
$project = Join-Path $root 'tests/AzureTray.Tests/AzureTray.Tests.csproj'

# Release builds/tests lock AzureTray.exe; stop it first (ignore if not running).
try { Get-Process -Name 'AzureTray' -ErrorAction Stop | Stop-Process -Force -ErrorAction SilentlyContinue } catch {}

$dotnetArgs = @($project, '--configuration', 'Release', '--nologo')

if ($Target) {
    $filter = if ($Target -match '[=~!]') { $Target } else { "FullyQualifiedName~$Target" }
    $dotnetArgs += @('--filter', $filter)
    Write-Host "Testing filter: $filter (Release)" -ForegroundColor Cyan
}

if ($Detailed) {
    $dotnetArgs += @('-v', 'normal')
    if (-not $Target) { Write-Host "Testing solution (Release, detailed)" -ForegroundColor Cyan }
    & dotnet test @dotnetArgs
    exit $LASTEXITCODE
}

$dotnetArgs += @('-v', 'minimal')
if (-not $Target) { Write-Host "Testing solution (Release)" -ForegroundColor Cyan }

$output = & dotnet test @dotnetArgs 2>&1
$exit = $LASTEXITCODE

# Build errors get reported verbatim — nothing ran, so there is no summary.
$buildErrors = $output | Where-Object { $_ -match ': error ' }
if ($buildErrors) {
    $buildErrors | Select-Object -Unique | ForEach-Object { Write-Host $_ -ForegroundColor Red }
}

# VSTest summary line: "Passed!  - Failed: 0, Passed: 85, Skipped: 0, Total: 85, ..."
$summary = $output | Where-Object { $_ -match 'Failed:\s+\d+,\s+Passed:\s+\d+' } | Select-Object -Last 1
if ($summary -and $summary -match 'Failed:\s+(\d+),\s+Passed:\s+(\d+),\s+Skipped:\s+(\d+),\s+Total:\s+(\d+)') {
    Write-Host ("Total:   {0}" -f $Matches[4])
    Write-Host ("Passed:  {0}" -f $Matches[2]) -ForegroundColor Green
    Write-Host ("Failed:  {0}" -f $Matches[1]) -ForegroundColor ($(if ($Matches[1] -eq '0') { 'Green' } else { 'Red' }))
    Write-Host ("Skipped: {0}" -f $Matches[3])
}

if ($exit -ne 0) {
    $failed = $output |
        Where-Object { $_ -match '^\s{2}(Failed|X)\s' } |
        ForEach-Object { ($_ -replace '^\s{2}(Failed|X)\s+', '') -replace '\s+\[.*$', '' } |
        Select-Object -Unique
    if ($failed) {
        Write-Host "Failed tests:" -ForegroundColor Red
        $failed | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    }
    Write-Host "TESTS FAILED (exit $exit)" -ForegroundColor Red
    if (-not $summary -and -not $buildErrors) { $output | ForEach-Object { Write-Host $_ } }
} else {
    Write-Host "TESTS PASSED" -ForegroundColor Green
}
exit $exit
