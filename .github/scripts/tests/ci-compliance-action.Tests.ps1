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

        # Characterisation, like the runner tests that accompany it: this asserts what the
        # summary says today so the absence is recorded rather than described. It should become
        # a -Match once the summary carries the examined count.
        It 'reports only pass or fail, with no count of what was examined' {
            $summaryLines = ($lines | Select-String -Pattern 'GITHUB_STEP_SUMMARY' -Context 0, 8 |
                             ForEach-Object { $_.Context.PostContext }) -join "`n"

            $summaryLines | Should -Not -Match 'examined' -Because 'a run that examined every file and one that examined none currently produce the same summary'
        }
    }
}
