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
