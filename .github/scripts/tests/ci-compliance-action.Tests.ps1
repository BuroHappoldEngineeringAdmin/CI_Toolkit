# ci-compliance-action.Tests.ps1 — structural assertions over ci-compliance's action.yml.
#
# The same shape as the sibling file for ci-serialisation: properties of the action as a whole
# that a reader cannot check from any one place in it.
#
# The property here is where the runner's output ends up. The invocation is wrapped in a
# ::group::, which the GitHub UI collapses by default, so anything the runner prints is hidden
# until someone expands it. The job summary is the surface a reader actually sees, and today it
# carries a single line saying only whether the check passed. A run that examined every file and
# a run that examined none produce the same summary.
#
# Run locally:  pwsh -Command "Invoke-Pester .github/scripts/tests -Output Detailed"
# Run in CI:    lint-workflows.yml, the powershell-tests job.

BeforeAll {
    $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
    $script:actionPath = Join-Path $repoRoot '.github/actions/ci-compliance/action.yml'
    $script:lines = Get-Content $actionPath
    $script:text  = $lines -join "`n"
}

Describe 'ci-compliance action.yml' {

    Context 'the job summary says how much was examined' {

        # Stdout is not the surface. The runner's output is inside a collapsed group, so a
        # count printed there is invisible to a reader who does not already suspect something.
        It 'wraps the runner invocation in a collapsed group' {
            $text | Should -Match '::group::' -Because 'if this stops being true the reasoning below needs revisiting'
        }

        It 'carries the examined count into the summary, not only pass or fail' {
            $text | Should -Match 'Files examined' -Because 'a run that examined every file and one that examined none must not produce the same summary'
        }

        # The count is the runner's, read back off one line it controls. If that line is ever
        # renamed on one side only, the summary silently loses the count rather than breaking,
        # so both ends of the contract are asserted here.
        It 'reads the count off the line the runner emits' {
            $text | Should -Match "match '\^Coverage: '"
        }
    }
}
