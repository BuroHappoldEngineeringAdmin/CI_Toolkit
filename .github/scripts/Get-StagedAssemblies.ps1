# Get-StagedAssemblies.ps1 — works out which assemblies a build staged, by comparing the
# assembly directory before and after it.
#
# Dot-sourced by .github/actions/ci-versioning/action.yml and by
# .github/scripts/tests/Get-StagedAssemblies.Tests.ps1. Defines functions and does nothing
# else, so dot-sourcing has no side effects.
#
# Why this exists. The versioning check attributes failures only to namespaces the repository
# under test declares, so it needs to know which assemblies are the repository's own. It used
# to read them from a `Build\` directory at the workspace root, on the assumption that every
# project wrote there. Nothing guaranteed that assumption and it was false for roughly a third
# of the fleet, differently under each build configuration, so the check either widened to the
# whole dependency closure and reported other repositories' failures, or attributed against a
# fraction of the repository with nothing to say so.
#
# Every BHoM project stages its output to the shared assembly directory through a PostBuild
# step, and that is the directory the runner reflects over. So the set staged during the
# subject build is both what the repository produced and what the runner can actually see.

function Get-AssemblyStamp {
    <#
    .SYNOPSIS
      A stable identity per assembly file: name and last-write time.

    .DESCRIPTION
      The write time is part of the identity on purpose. A repository can produce an assembly
      with the same file name as one already staged by a dependency, and the staging step
      overwrites it in place. Comparing names alone would treat that as unchanged and drop the
      repository's own assembly from its subject set, which is the failure this whole mechanism
      exists to remove — silently attributing against an incomplete set.

    .PARAMETER Path
      Assembly directory. A missing directory yields an empty stamp set rather than throwing,
      so the caller decides what an empty result means.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path $Path)) { return @() }

    return @(
        Get-ChildItem -LiteralPath $Path -Filter *.dll -File -ErrorAction SilentlyContinue |
            ForEach-Object { "$($_.Name)|$($_.LastWriteTimeUtc.Ticks)" }
    )
}

function Get-NewlyStagedAssemblies {
    <#
    .SYNOPSIS
      The assembly names present after a build that were not present, identically, before it.

    .DESCRIPTION
      Pure over its two inputs so the comparison can be tested without a build. Returns names
      rather than stamps, because the runner identifies an assembly by file name.

      An entry counts as newly staged when its name-and-time pair is absent from the before
      set. That covers both shapes: an assembly that did not exist before, and one that existed
      and was overwritten.

    .PARAMETER Before, After
      Stamp collections from Get-AssemblyStamp.
    #>
    [CmdletBinding()]
    param(
        [string[]]$Before = @(),
        [string[]]$After  = @()
    )

    $seen = [System.Collections.Generic.HashSet[string]]::new(
        [string[]]@($Before), [System.StringComparer]::OrdinalIgnoreCase)

    $names = foreach ($entry in @($After)) {
        if (-not $seen.Contains($entry)) { ($entry -split '\|', 2)[0] }
    }

    # Emitted as a sequence, not wrapped. A comma-wrap here would return an array containing
    # the array, which counts as one element and silently breaks any caller that measures it.
    # Callers that need a definite collection wrap with @(), which is the repository's idiom.
    return @($names | Sort-Object -Unique)
}
