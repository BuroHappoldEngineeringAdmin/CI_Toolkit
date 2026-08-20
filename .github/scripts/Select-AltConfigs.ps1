# Select-AltConfigs.ps1 — the altConfigs.txt selection decision, on its own so it
# can be tested without invoking a build.
#
# Dot-sourced by Build-AltConfigs.ps1 and by
# .github/scripts/tests/Select-AltConfigs.Tests.ps1. Defines a function and does
# nothing else, so dot-sourcing has no side effects.

function Select-AltConfigs {
    <#
    .SYNOPSIS
      Turns altConfigs.txt lines into the list of build configurations CI should build.

    .DESCRIPTION
      altConfigs.txt format is 'org/repo/ConfigName', one per line. Only the third
      segment is used; org and repo are legacy metadata from the BHoMBot dependency
      graph and are ignored, which is what BHoMBot itself did.

      Selection filters to configurations whose name starts with $Configuration.
      In practice that means Release* only, because:

        - Real altConfigs.txt files carry both. Every Revit-family repo sampled lists
          five Debug20NN and five Release20NN entries.
        - The installer ships Release* only. BuroHappold_Installer's own
          IncludedRepos/altConfigs.txt contains Release entries exclusively, no Debug.
          So building Debug20NN in CI verifies configurations that are never shipped.
        - resolve-dependencies already filters the same way for dependency repos
          (Build-Dependencies.ps1), and caller Debug builds succeeded in production
          against a Release-only dependency closure, so the two sides were not
          consistent and the stricter one was the one doing less pointless work.

      Malformed lines are reported and skipped rather than failing the run, matching
      the previous behaviour.

    .PARAMETER Lines
      Raw lines from altConfigs.txt.

    .PARAMETER Configuration
      Configuration prefix to keep. Case-insensitive.

    .OUTPUTS
      Configuration names to build, in file order, duplicates removed.
    #>
    [CmdletBinding()]
    param(
        # AllowEmptyString is required, not defensive: real altConfigs.txt files contain
        # blank lines, and a Mandatory [string[]] rejects an empty-string element without
        # it. Caught by Select-AltConfigs.Tests.ps1 on its first run.
        [Parameter(Mandatory)][AllowEmptyCollection()][AllowEmptyString()][string[]]$Lines,
        [Parameter(Mandatory)][string]$Configuration
    )

    $selected = [System.Collections.Generic.List[string]]::new()

    foreach ($raw in $Lines) {
        if ($null -eq $raw) { continue }
        $line = $raw.Trim()
        if ($line -eq "" -or $line.StartsWith("#")) { continue }

        $parts = $line.Split('/')
        if ($parts.Length -lt 3) {
            Write-Host "::warning::Skipping malformed altConfigs entry: '$line' (expected org/repo/ConfigName)"
            continue
        }

        $configName = $parts[2].Trim()
        if ($configName -eq "") {
            Write-Host "::warning::Skipping altConfigs entry with an empty configuration name: '$line'"
            continue
        }

        if (-not $configName.StartsWith($Configuration, [System.StringComparison]::OrdinalIgnoreCase)) {
            Write-Host "::debug::Skipping '$configName': not a '$Configuration*' configuration."
            continue
        }

        if (-not $selected.Contains($configName)) { $selected.Add($configName) }
    }

    return $selected.ToArray()
}

function Get-AltConfigSelectionError {
    <#
    .SYNOPSIS
      Returns an error message when a non-empty altConfigs.txt selected nothing, or $null.

    .DESCRIPTION
      Separated from Select-AltConfigs for the same reason Select-AltConfigs is separated
      from Build-AltConfigs: the decision is testable without invoking a build.

      A file with content that selects nothing is an error, not a quiet no-op. An earlier
      ci-versioning draft filtered entries on their org/repo segments against
      github.repository. On a fork those never match, so it selected nothing, built
      nothing, printed nothing and reported success. The absence of a signal was the
      damage; the filter was only how the silence arose.

      Failing is safe because the alternative shape does not exist. Measured across the
      fleet: 14 repositories carry altConfigs.txt, each exactly 10 lines, 5 Release and 5
      Debug. None is Debug-only, so "non-empty but nothing selected for Release" has no
      legitimate instance today. If one appears, this fails loudly and someone decides.

    .PARAMETER Lines
      Raw lines from altConfigs.txt.

    .PARAMETER Selected
      What Select-AltConfigs returned for those lines.

    .PARAMETER Configuration
      Configuration prefix that was requested, used in the message.
    #>
    [CmdletBinding()]
    param(
        # AllowNull on both is not padding. Select-AltConfigs returns ToArray(), and
        # PowerShell unrolls an empty array to $null on assignment, so the empty case
        # arrives here as $null from any ordinary caller. Rejecting it replaces this
        # function's message with a parameter-binding error, which is the silent-to-
        # useless failure it exists to prevent. Observed on a real runner.
        [Parameter(Mandatory)][AllowNull()][AllowEmptyCollection()][AllowEmptyString()][string[]]$Lines,
        [Parameter(Mandatory)][AllowNull()][AllowEmptyCollection()][string[]]$Selected,
        [Parameter(Mandatory)][string]$Configuration
    )

    if ($Selected.Count -gt 0) { return $null }

    $meaningful = @($Lines | Where-Object { $null -ne $_ -and $_.Trim() -and -not $_.Trim().StartsWith('#') })
    if ($meaningful.Count -eq 0) { return $null }

    $seen = ($meaningful | ForEach-Object { $_.Trim() }) -join '; '
    return "altConfigs.txt has $($meaningful.Count) entr(ies) but none selected for '$Configuration*'. Expected at least one. Entries seen: $seen"
}
