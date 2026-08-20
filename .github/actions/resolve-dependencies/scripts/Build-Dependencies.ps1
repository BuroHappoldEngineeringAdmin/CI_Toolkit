param(
    [string]$Configuration = "Release",
    [string]$CloneRoot     = "C:\bhom-deps"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# .github/actions/resolve-dependencies/scripts -> ../../../scripts is .github/scripts.
. (Join-Path $PSScriptRoot '../../../scripts/Select-AltConfigs.ps1')

# Uses dotnet build for SDK-style repos. Hard-fails on non-SDK (legacy MSBuild) repos.
function Invoke-BHoMBuild {
    param(
        [Parameter(Mandatory)][string]$Target,
        [Parameter(Mandatory)][string]$Config
    )

    $targetDir = if (Test-Path $Target -PathType Container) { $Target } else { Split-Path $Target }

    # Fail fast on non-SDK project files — scoped to solution-referenced projects only to
    # avoid false positives on out-of-solution files (e.g. .ci/ test helpers).
    if ($Target -match '\.sln$') {
        $projectsToCheck = @(dotnet sln $Target list 2>$null |
            Where-Object { $_ -match '\.csproj$' } |
            ForEach-Object { Join-Path $targetDir $_ })
    } else {
        $projectsToCheck = @($Target)
    }
    $legacyProjects = @($projectsToCheck | Where-Object { Test-Path $_ } | Where-Object {
        $content = Get-Content $_ -Raw -ErrorAction SilentlyContinue
        $content -and $content -notmatch '<Project[^>]+Sdk=' -and $content -notmatch '<Sdk\s'
    })
    if ($legacyProjects.Count -gt 0) {
        $names = ($legacyProjects | ForEach-Object { Split-Path $_ -Leaf }) -join ', '
        throw "Non-SDK project(s) detected in ${targetDir}: $names — migrate to <Project Sdk=`"Microsoft.NET.Sdk`">. Legacy MSBuild format is not supported."
    }

    # Push into the repo directory for consistent relative path resolution.
    Push-Location $targetDir
    try {
        dotnet restore $Target
        if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed for $Target" }

        # -clp:ErrorsOnly suppresses WARNING output from the console logger for
        # dependency builds only. Not a cosmetic change: actions/setup-dotnet
        # registers the 'csc' problem matcher (its .github/csc.json), which turns every
        # MSBuild diagnostic line into a check-run annotation. Measured on production
        # run 31499584823, ci-build emitted 1,124 such annotations, 1,104 of them from
        # C:hom-deps paths. GitHub cannot resolve an absolute path outside the
        # workspace, so it stored them against path ".github" with the source file's
        # line number, and its per-step cap discarded all but 20. The caller's own
        # diagnostics resolve correctly and must keep doing so, which is why this is
        # scoped to the dependency build and not applied globally or via
        # ::remove-matcher, which is job-wide and cannot be undone without the
        # matcher file's path.
        #
        # Errors still print and still annotate. A dependency build failure should be
        # loud, and the throw below names the target.
        dotnet build $Target -c $Config --no-restore --nologo -clp:ErrorsOnly
        if ($LASTEXITCODE -ne 0) { throw "dotnet build failed for $Target" }
    }
    finally {
        Pop-Location
    }
}

$cloneRoot       = $CloneRoot
$depsDir         = "deps"
$orderOut        = Join-Path $depsDir "_order.txt"
$overallFailures = @()
$buildResults    = [System.Collections.Generic.List[hashtable]]::new()


if (-not (Test-Path $orderOut)) {
    Write-Warning "No build order file found at $orderOut."
    $order = @()
}
else {
    # Filter blank lines: _order.txt may be written empty when there are no dependencies.
    $order = @(Get-Content $orderOut | Where-Object { $_ -match '\S' })
}

if ($order.Count -eq 0) {
    Write-Host "::notice::No dependencies to build. Skipping dependency build step."
    exit 0
}

foreach ($ownerRepo in $order) {

    $repoName = $ownerRepo.Split("/")[1]
    $repoPath = Join-Path $cloneRoot $repoName
    if (-not (Test-Path $repoPath)) {
        # Hard-fail on missing clone: resolution recorded this repo but the clone failed
        # (auth/network error). Continuing would yield a confusing "assembly not found" later.
        Write-Host "::error title=Build::Clone not found at $repoPath. $ownerRepo was in the build order but was never cloned. Check for earlier auth or network errors in the dependency resolution step."
        $overallFailures += "$repoName"
        Write-Host "::endgroup::"
        continue
    }

    Write-Host "::group::Building $repoName"

    $solution  = Get-ChildItem $repoPath -Recurse -Filter *.sln -ErrorAction SilentlyContinue | Select-Object -First 1
    $buildType = "dotnet build (SDK)"
    $buildOk   = $true

    try {

        if ($null -ne $solution) {
            Invoke-BHoMBuild -Target $solution.FullName -Config $Configuration

            # Build alt configurations for this dependency (Release* only, matching CI config).
            # Required so that version-specific assemblies (e.g. Revit_UI_oM_2023.dll) exist
            # before the caller repo builds its own alt configs.
            $depAltConfigFile = Join-Path $repoPath "altConfigs.txt"
            if (Test-Path $depAltConfigFile) {
                # Selection is shared with ci-build and ci-versioning rather than repeated
                # here. This block used to carry its own copy of the parse, which drifted:
                # a third copy written for ci-versioning added an org/repo filter that the
                # other two do not have, and it selected nothing on forks and on the one
                # repository whose file names a different repo entirely.
                $altConfigs = @(Select-AltConfigs -Lines @(Get-Content $depAltConfigFile) -Configuration $Configuration)

                Push-Location (Split-Path $solution.FullName)
                try {
                    foreach ($altConfig in $altConfigs) {
                        Write-Host "  Alt config: $altConfig"
                        # -clp:ErrorsOnly for the same reason as the primary dependency
                        # build above: warnings from a dependency's alt config annotate
                        # against ".github" and crowd out the caller's own diagnostics.
                        dotnet build $solution.FullName -c $altConfig --no-restore --nologo -clp:ErrorsOnly
                        if ($LASTEXITCODE -ne 0) { throw "dotnet build ($altConfig) failed for $repoName" }
                    }
                }
                finally {
                    Pop-Location
                }
            }
        }
        else {
            $projects = Get-ChildItem $repoPath -Recurse -Filter *.csproj -ErrorAction SilentlyContinue

            if ($projects.Count -eq 0) {
                Write-Host "No .sln or .csproj in $repoName — skipping."
                $buildType = "skipped"
            }
            else {
                foreach ($p in $projects) {
                    Invoke-BHoMBuild -Target $p.FullName -Config $Configuration
                }
            }
        }

        # Plain log line, not an annotation. One notice per dependency consumed
        # GitHub's per-level annotation cap: measured 10 of 23 surfacing, each
        # anchored to .github, crowding out real notices. The same information is
        # in the dependency table written to the job summary.
        Write-Host "$repoName built successfully ($buildType)"
    }
    catch {
        $buildOk = $false
        Write-Warning "Build FAILED for '$repoName': $($_.Exception.Message)"
        Write-Host "::error title=Build FAILED::${repoName}: $($_.Exception.Message)"
        $overallFailures += "$repoName"
    }

    $buildResults.Add(@{ Repo=$repoName; Type=$buildType; Ok=$buildOk })

    Write-Host "::endgroup::"
}

# Assemblies are staged to ProgramData\BHoM\Assemblies by each repo's PostBuildEvent.
# That directory is the canonical output cached by the calling action.
$bhomAssemblies = Join-Path $env:ProgramData "BHoM\Assemblies"
$totalAssemblies = @(Get-ChildItem $bhomAssemblies -Filter *.dll -ErrorAction SilentlyContinue).Count
Write-Host "Total assemblies in ${bhomAssemblies}: $totalAssemblies"

if ($env:GITHUB_STEP_SUMMARY) {
    $mdLines = @("### Dependency build results", "",
                 "| Repository | Build tool | Result |",
                 "|---|---|---|")

    foreach ($r in $buildResults) {
        $status = if ($r.Ok) { "ok" } else { "**failed**" }
        $mdLines += "| ``$($r.Repo)`` | $($r.Type) | $status |"
    }

    $mdLines += ""
    $mdLines += "Total assemblies in ProgramData\BHoM\Assemblies: $totalAssemblies"

    $mdLines | Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Encoding utf8 -Append
}

if ($overallFailures.Count -gt 0) {
    Write-Error ("One or more dependency builds failed:`n - " + ($overallFailures -join "`n - "))
}
