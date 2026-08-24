# ci-serialisation-action.Tests.ps1 — structural assertions over ci-serialisation's action.yml.
#
# These test the shape of the workflow file rather than any script it calls. That is unusual
# here, and deliberate: all three properties below are invariants a reader cannot check by
# looking at one place in the file, and each of them was broken in a way that produced no
# failure anywhere. A composite action with a dozen steps has emergent properties, and nothing
# else in this repository asserts one.
#
# Text assertions rather than a YAML parse. The powershell-tests job installs Pester and
# nothing else, and adding a YAML module to reach three line-shaped facts would cost more than
# it returns. Each assertion below is written so that a restructure fails it loudly rather than
# passing vacuously.
#
# Run locally:  pwsh -Command "Invoke-Pester .github/scripts/tests -Output Detailed"
# Run in CI:    lint-workflows.yml, the powershell-tests job.

BeforeAll {
    $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
    $script:actionPath = Join-Path $repoRoot '.github/actions/ci-serialisation/action.yml'
    $script:lines = Get-Content $actionPath
    $script:text  = $lines -join "`n"
}

Describe 'ci-serialisation action.yml' {

    Context 'every outcome reaches the job summary' {

        # The failure this guards against is silent and reads as a tooling fault rather than a
        # check finding: the job goes red and the summary is empty, so the author is told
        # nothing at all about why.
        #
        # The action used to carry five summary steps, each gated on a different combination of
        # step conclusions and outputs. Between them they named five of the eighteen functional
        # steps, so a failure in any of the other thirteen matched no condition and wrote
        # nothing. Seven of those thirteen run before serialisation is even attempted, which is
        # the case a reader is least able to diagnose unaided.
        #
        # One always-gated step cannot have that hole. Enumerating conditions in YAML can,
        # every time a step is added, which is why this asserts the shape rather than the list.
        It 'writes the summary from a single step that always runs' {
            $summarySteps = $lines | Where-Object { $_ -match '^\s*- name:\s*Write to Job Summary' }
            @($summarySteps).Count | Should -Be 1 -Because 'one always-gated step cannot leave an outcome unreported; several mutually-exclusive ones can, and did'

            # The `if:` belonging to that step is the next one in the file.
            $summaryIndex = ($lines | Select-String -Pattern '^\s*- name:\s*Write to Job Summary' | Select-Object -First 1).LineNumber - 1
            $condition = $lines[$summaryIndex..($summaryIndex + 4)] |
                         Where-Object { $_ -match '^\s*if:' } |
                         Select-Object -First 1
            $condition | Should -Match 'always\(\)' -Because 'a summary gated on success cannot describe a failure'
        }
    }

    Context 'build output does not drown the check it belongs to' {

        # setup-dotnet registers the csc problem matcher, which turns every MSBuild diagnostic
        # line into a check-run annotation. Dependency and baseline builds compile code the
        # author did not write, from paths outside the workspace that GitHub cannot resolve, so
        # their warnings land against the wrong file with the wrong line number and consume the
        # per-step annotation cap that the caller's own diagnostics need.
        #
        # Build-Dependencies.ps1 already applies this to dependency builds and records the
        # measurement behind it. The four builds in this action are the same kind of work: a
        # means to an end, where build warnings belong to ci-build instead.
        It 'passes -clp:ErrorsOnly on every build it runs' {
            $builds = $lines | Where-Object { $_ -match 'dotnet build' }
            @($builds).Count | Should -BeGreaterThan 0 -Because 'if this finds nothing the pattern has drifted and the test is vacuous'

            $missing = @($builds | Where-Object { $_ -notmatch '-clp:ErrorsOnly' })
            $missing | Should -BeNullOrEmpty -Because "these builds annotate the check with warnings from code the author did not write:`n$($missing -join "`n")"
        }
    }

    Context 'staging is reset in one place' {

        # The assemblies directory is reset by resolve-dependencies, in its own Prepare folders
        # step, which removes the directory and recreates it and does the same for Upgrades.
        # A second reset in this action removes the contents of one of those two directories
        # and runs immediately before the step that does the job properly, so it is both
        # weaker and redundant.
        #
        # It is worth a test rather than a comment because a reader looking for where the two
        # legs are separated finds this line first and reasonably concludes it is the mechanism.
        It 'leaves the assemblies reset to resolve-dependencies' {
            $resets = @($lines | Where-Object { $_ -match 'Remove-Item.*ProgramData\\BHoM\\Assemblies' })
            $resets | Should -BeNullOrEmpty -Because "resolve-dependencies resets this directory in its own Prepare folders step, so a reset here is redundant and reads as the between-legs separation when it is not:`n$($resets -join "`n")"
        }
    }
}
