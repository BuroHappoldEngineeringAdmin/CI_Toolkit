# Build-AltConfigs.ps1 — builds SDK-style configurations listed in altConfigs.txt.
# Expects: cwd = caller repo root, dotnet SDK on PATH (windows-latest runner).
#
# altConfigs.txt format: org/repo/ConfigName (one entry per line).
# Only ConfigName (the third segment) is used — org/repo are legacy metadata from the
# BHoMBot dependency graph. Blank lines and # comments are skipped.
#
# Only Release* configurations are built. Rationale is in Select-AltConfigs.ps1;
# the short version is that the installer ships Release* only, and
# resolve-dependencies already filtered the dependency side the same way.
#
# Parameters:
#   -SlnPath         Path to the primary solution file.
#   -Configuration   Configuration prefix to build. Default Release, matching
#                    the primary build in ci-build/action.yml.

param(
    [Parameter(Mandatory)][string]$SlnPath,
    [string]$Configuration = 'Release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Select-AltConfigs.ps1')

if (-not (Test-Path "altConfigs.txt")) {
    Write-Host "::notice::No altConfigs.txt found. No alt configuration builds to run."
    exit 0
}

# Fail fast on non-SDK project files — scoped to solution-referenced projects only.
$slnDir = Split-Path $SlnPath
$legacyProjects = @(dotnet sln $SlnPath list 2>$null |
    Where-Object { $_ -match '\.csproj$' } |
    ForEach-Object { Join-Path $slnDir $_ } |
    Where-Object { Test-Path $_ } |
    Where-Object {
        $content = Get-Content $_ -Raw -ErrorAction SilentlyContinue
        $content -and $content -notmatch '<Project[^>]+Sdk=' -and $content -notmatch '<Sdk\s'
    })
if ($legacyProjects.Count -gt 0) {
    $names = ($legacyProjects | ForEach-Object { Split-Path $_ -Leaf }) -join ', '
    Write-Host "::error title=Build::Non-SDK project(s) detected: $names. Legacy MSBuild is not supported. Migrate to <Project Sdk=`"Microsoft.NET.Sdk`">."
    exit 1
}

$configs = Select-AltConfigs -Lines @(Get-Content altConfigs.txt) -Configuration $Configuration

if ($configs.Count -eq 0) {
    Write-Host "::notice::altConfigs.txt lists no '$Configuration*' configurations. Nothing to build."
    exit 0
}

Write-Host "::notice title=Build::Building $($configs.Count) '$Configuration*' alt configuration(s): $($configs -join ', ')"

$anyFailure = $false

foreach ($configName in $configs) {
    Write-Host "::group::Alt config: $configName"

    # Relies on the prior `dotnet restore` at the solution level covering all configurations.
    # For SDK-style projects this is always true; packages.config repos are rejected above.
    dotnet build $SlnPath -c $configName --no-restore --nologo
    if ($LASTEXITCODE -ne 0) {
        Write-Host "::error title=Build::Alt config '$configName' failed."
        $anyFailure = $true
    } else {
        # Plain log line, not an annotation. Per-item success notices consume
        # GitHub's per-level annotation cap: measured 5 alt configs plus the plan
        # notice taking 6 of 10 notice slots on a passing ci-build run. The outcome
        # is carried by the check status and the job summary.
        Write-Host "Alt config '$configName' succeeded."
    }

    Write-Host "::endgroup::"
}

if ($anyFailure) { exit 1 }
