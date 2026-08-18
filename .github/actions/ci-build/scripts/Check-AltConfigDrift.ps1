# Check-AltConfigDrift.ps1 - warns when solution configurations are not covered by CI.
#
# ci-build compiles Release plus the configurations listed in altConfigs.txt.
# Any other configuration declared in the solution is never built by CI, and
# Release* entries missing from altConfigs.txt are also invisible to the
# installer manifest. This check surfaces that drift. Debug is exempt: it is
# the local development configuration, not part of the ship path.
#
# Warning-only for now. Once the fleet is clean, Release* drift is intended to
# become blocking via -FailOnReleaseDrift.
#
# Parameters:
#   -SlnPath             Path to the primary solution file.
#   -FailOnReleaseDrift  Exit 1 when uncovered Release* configurations exist.

param(
    [Parameter(Mandatory)][string]$SlnPath,
    [switch]$FailOnReleaseDrift
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Distinct configuration names from the solution's SolutionConfigurationPlatforms
# section, |Platform suffix stripped.
$inSection  = $false
$slnConfigs = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($line in Get-Content $SlnPath) {
    if ($line -match 'GlobalSection\(SolutionConfigurationPlatforms\)') { $inSection = $true; continue }
    if ($inSection -and $line -match 'EndGlobalSection') { break }
    if ($inSection -and $line -match '^\s*([^=|]+?)\s*\|') {
        [void]$slnConfigs.Add($Matches[1].Trim())
    }
}

if ($slnConfigs.Count -eq 0) {
    Write-Host "::warning title=Build::No configurations parsed from $SlnPath - altConfigs drift check skipped."
    exit 0
}

$covered = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
[void]$covered.Add('Debug')
[void]$covered.Add('Release')

# altConfigs.txt format: org/repo/ConfigName, blank lines and # comments skipped.
if (Test-Path 'altConfigs.txt') {
    Get-Content 'altConfigs.txt' |
        ForEach-Object { $_.Trim() } |
        Where-Object  { $_ -ne '' -and -not $_.StartsWith('#') } |
        ForEach-Object {
            $parts = $_.Split('/')
            if ($parts.Length -ge 3) { [void]$covered.Add($parts[2]) }
        }
}

$uncovered = @($slnConfigs | Where-Object { -not $covered.Contains($_) } | Sort-Object)
if ($uncovered.Count -eq 0) {
    Write-Host "::notice title=Build::All solution configurations are covered by CI."
    exit 0
}

$releaseDrift = @($uncovered | Where-Object { $_ -like 'Release*' })
$otherDrift   = @($uncovered | Where-Object { $_ -notlike 'Release*' })

foreach ($config in $releaseDrift) {
    Write-Host "::warning title=Build::Configuration '$config' is declared in the solution but never built by CI and not visible to the installer. Add it to altConfigs.txt or remove it from the solution."
}
foreach ($config in $otherDrift) {
    Write-Host "::warning title=Build::Configuration '$config' is declared in the solution but never built by CI. Add it to altConfigs.txt if it should be built, or remove it from the solution."
}

if ($FailOnReleaseDrift -and $releaseDrift.Count -gt 0) {
    Write-Host "::error title=Build::Uncovered Release* configuration(s): $($releaseDrift -join ', ')."
    exit 1
}
