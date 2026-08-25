# ci-versioning-action.Tests.ps1 — structural assertions over ci-versioning's action.yml.
#
# Same shape as the sibling files for ci-serialisation and ci-compliance: properties of the
# action as a whole that a reader cannot check from any one place in it.
#
# The property here is that the check asserts its own preconditions. The runner is handed a
# subject build directory and narrows attribution to the assemblies in it; if that directory is
# missing it silently widens to the whole dependency closure and reports other repositories'
# defects against this one. The runner warns, but on stderr, and a warning does not stop a
# verdict being produced on no basis.
#
# Run locally:  pwsh -Command "Invoke-Pester .github/scripts/tests -Output Detailed"
# Run in CI:    lint-workflows.yml, the powershell-tests job.

BeforeAll {
    $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
    $script:actionPath = Join-Path $repoRoot '.github/actions/ci-versioning/action.yml'
    $script:lines = Get-Content $actionPath
    $script:text  = $lines -join "`n"
}

Describe 'ci-versioning action.yml' {

    Context 'preconditions are asserted before the runner is invoked' {

        # The datasets guard already existed and is the pattern being followed. Asserted so
        # that if it is ever removed, the reasoning below stops resting on something absent.
        It 'still guards the datasets directory' {
            $text | Should -Match 'Datasets directory missing at'
        }

        It 'guards the subject build output the same way' {
            $text | Should -Match 'Subject build output missing at' -Because 'an absent subject directory makes the check attribute every failure across the whole closure'
        }

        It 'also rejects a subject directory that exists but is empty' {
            $text | Should -Match 'contains no assemblies' -Because 'a present but empty directory yields an empty subject set, which reports nothing and passes'
        }

        It 'fails rather than warns, because the check cannot do its job without it' {
            $guard = $text -split '- name: Validate subject build output' | Select-Object -Last 1
            $guard = ($guard -split '- name: ')[0]
            $guard | Should -Match 'exit 1'
            $guard | Should -Not -Match '::warning'
        }

        # Ordering matters and is not obvious from either step alone: the guard is worthless
        # after the thing it protects has already run.
        It 'runs before the versioning tests, not after' {
            $guardIdx  = ($lines | Select-String -Pattern '- name: Validate subject build output' | Select-Object -First 1).LineNumber
            $runnerIdx = ($lines | Select-String -Pattern '- name: Run versioning tests'          | Select-Object -First 1).LineNumber
            $guardIdx | Should -BeLessThan $runnerIdx
        }

        # Substring rather than regex: the path contains backslashes and braces, and a guard
        # that checked a different directory from the one the runner reads would pass a
        # loosely-written pattern while protecting nothing.
        It 'checks the same directory the runner is pointed at' {
            $subjectPath = '"${{ github.workspace }}\Build"'
            $text.Contains('$subjectDir = ' + $subjectPath)          | Should -BeTrue -Because 'the guard must read the path the runner is given'
            $text.Contains('--subject-assemblies ' + $subjectPath)   | Should -BeTrue -Because 'if the runner argument changes, this guard stops protecting it'
        }
    }
}
