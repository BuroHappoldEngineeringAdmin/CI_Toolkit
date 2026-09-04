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
            $text | Should -Match 'Subject assembly list missing' -Because 'without a subject set the check attributes every failure across the whole closure'
        }

        It 'also rejects a subject set that was collected but is empty' {
            $text | Should -Match 'staged no assemblies' -Because 'an empty subject set attributes nothing and passes green having measured nothing'
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

        # The guard and the runner must read the same artefact. If they diverge the guard
        # passes on one thing while the runner attributes against another, which is the
        # failure it exists to prevent, one level removed.
        It 'checks the same artefact the runner is given' {
            $artefact = 'subject-assemblies.txt'
            $text.Contains("Set-Content -Path '$artefact'")      | Should -BeTrue -Because 'the collection step writes it'
            $text.Contains("`$listPath = '$artefact'")           | Should -BeTrue -Because 'the guard reads it'
            $text.Contains("--subject-assembly-list '$artefact'") | Should -BeTrue -Because 'the runner is given it'
        }

        # The two guard branches above only mean different things if the file is always
        # written. Piping an empty array to Set-Content never invokes its process block and
        # leaves no file (measured), so an empty subject set arrives at the reader as an
        # absent one and the "did not run" branch fires for a step that ran. Measured on
        # BHoM/Versioning_Toolkit PR #348, run 33619426914.
        #
        # Asserted on the -Value form rather than on behaviour because this file is text
        # under test, not an executed script. Reverting to a pipe is the specific regression
        # this catches, and it looks harmless.
        It 'writes the subject list unconditionally, so absent and empty stay distinguishable' {
            $collect = ($text -split '- name: Collect subject assemblies' | Select-Object -Last 1)
            $collect = ($collect -split '- name: ')[0]
            $collect | Should -Match "Set-Content -Path 'subject-assemblies\.txt' -Value \`$subject" `
                -Because 'an empty pipeline into Set-Content writes no file, which makes the guard misreport'
            $collect | Should -Not -Match "\`$subject \| Set-Content" `
                -Because 'the pipe form is the regression: it leaves no file when the build stages nothing'
        }
    }

    Context 'the unverified breakdown is reported unconditionally' {

        # A silent zero cannot be told from a number nobody measured. Same trap as the
        # attribution-basis print that used to be gated on being non-zero, and the reason
        # run 33849699768's 114/31 split was read as 0/145.
        It 'prints both breakdown rows without gating them on a non-zero total' {
            $summary = ($text -split '- name: Write to Job Summary' | Select-Object -Last 1)
            $summary | Should -Match 'could not be resolved \(closure gap\)'
            $summary | Should -Match 'could not be attributed \(inferred ownership\)'
            # Tested by contiguity rather than by absence of any 'if': #17 legitimately uses
            # elseif ($unverified -gt 0) further down for the findings-table prose. What must
            # hold is that nothing branches between the total and its two components.
            $between = [regex]::Match($text,
                '(?s)Reported unverified.*?could not be attributed \(inferred ownership\)').Value
            $between | Should -Not -Match 'if \(' `
                -Because 'a branch between the total and its breakdown reintroduces the silent zero'
        }

        It 'surfaces both axes, not just attribution' {
            $text | Should -Match 'Unverified basis'   -Because 'the classification axis'
            $text | Should -Match 'Attribution basis'  -Because 'the ownership axis'
        }
    }

    Context 'the subject-assembly bracket' {

        # The subject set is the difference between two snapshots of the shared assembly
        # directory, so it is exactly whatever was staged between them. That makes the bracket
        # an ordering assumption in a file where steps get inserted, and an inserted step that
        # builds or copies would be silently attributed to the repository under test.
        #
        # This asserts the shape rather than today's list: it fails when anything is added
        # inside the bracket, which is the point. If a step genuinely belongs there, this test
        # is the place to say so deliberately.
        It 'contains exactly the two steps that build this repository' {
            $open  = ($lines | Select-String -Pattern '- name: Snapshot staged assemblies' | Select-Object -First 1).LineNumber
            $close = ($lines | Select-String -Pattern '- name: Collect subject assemblies'  | Select-Object -First 1).LineNumber

            $open  | Should -Not -BeNullOrEmpty
            $close | Should -BeGreaterThan $open

            $inner = $lines[$open..($close - 2)] | Where-Object { $_ -match '^\s+- name: ' }
            @($inner).Count | Should -Be 2 -Because "only the primary and alt-config builds may sit inside the bracket; found:`n$($inner -join "`n")"
            ($inner -join ' ') | Should -Match 'Build primary repo'
            ($inner -join ' ') | Should -Match 'Build alt configurations'
        }

        # A nested action inside the bracket could wipe or repopulate the assembly directory,
        # which would corrupt the difference without adding a step name anyone would question.
        It 'contains no nested action call' {
            $open  = ($lines | Select-String -Pattern '- name: Snapshot staged assemblies' | Select-Object -First 1).LineNumber
            $close = ($lines | Select-String -Pattern '- name: Collect subject assemblies'  | Select-Object -First 1).LineNumber

            $uses = $lines[$open..($close - 2)] | Where-Object { $_ -match '^\s+uses:' }
            @($uses).Count | Should -Be 0 -Because "a nested action inside the bracket can change the assembly directory: $($uses -join '; ')"
        }

        It 'closes after the alt-config build, so alt configurations are included' {
            $alt   = ($lines | Select-String -Pattern '- name: Build alt configurations' | Select-Object -First 1).LineNumber
            $close = ($lines | Select-String -Pattern '- name: Collect subject assemblies' | Select-Object -First 1).LineNumber
            $close | Should -BeGreaterThan $alt -Because 'a Revit repository year-suffixed assemblies are its own code and belong in the subject set'
        }
    }
}
