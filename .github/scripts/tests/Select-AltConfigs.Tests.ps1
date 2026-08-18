# Select-AltConfigs.Tests.ps1 — Pester tests for the altConfigs.txt selection decision.
#
# FIRST PowerShell test in this repository. Introduced deliberately rather than
# incidentally: Build-Dependencies.ps1 and Resolve-DependencyGraph.ps1 carry over 500
# lines of dependency-resolution logic that nothing tests, and several defects in this
# repo have been ones nobody could check cheaply. This file establishes the surface on a
# small, well-understood change; it is not an attempt to cover those two scripts.
#
# Deliberately narrow: it tests the selection decision only, not the build. The build
# needs a solution, an SDK and a runner; the decision is a pure function over strings and
# is where the defect was.
#
# Run locally:  pwsh -Command "Invoke-Pester .github/scripts/tests -Output Detailed"
# Run in CI:    test-tools.yml, alongside the two C# suites.

BeforeAll {
    $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
    . (Join-Path $repoRoot '.github/actions/ci-build/scripts/Select-AltConfigs.ps1')
}

Describe 'Select-AltConfigs' {

    Context 'the defect this fixed: Debug configurations were built' {
        # The shape of a real Revit toolkit's altConfigs.txt, from the production run that
        # logged all ten configurations being built. Org and repo segments are synthetic:
        # Select-AltConfigs reads only the third segment, so they carry no test weight.
        BeforeAll {
            $script:real = @(
                'Example_Org/Example_Revit_Tool/Debug2022'
                'Example_Org/Example_Revit_Tool/Debug2023'
                'Example_Org/Example_Revit_Tool/Debug2024'
                'Example_Org/Example_Revit_Tool/Debug2025'
                'Example_Org/Example_Revit_Tool/Debug2026'
                'Example_Org/Example_Revit_Tool/Release2022'
                'Example_Org/Example_Revit_Tool/Release2023'
                'Example_Org/Example_Revit_Tool/Release2024'
                'Example_Org/Example_Revit_Tool/Release2025'
                'Example_Org/Example_Revit_Tool/Release2026'
            )
        }

        It 'selects the five Release configurations and none of the Debug ones' {
            $got = Select-AltConfigs -Lines $script:real -Configuration 'Release'
            $got | Should -HaveCount 5
            $got | Should -Be @('Release2022','Release2023','Release2024','Release2025','Release2026')
        }

        It 'previously built all ten, which is what this asserts against' {
            # Characterisation of the old behaviour: every three-segment line was built.
            $unfiltered = @($script:real | ForEach-Object { $_.Split('/')[2] })
            $unfiltered | Should -HaveCount 10
            (Select-AltConfigs -Lines $script:real -Configuration 'Release').Count |
                Should -BeLessThan $unfiltered.Count
        }
    }

    Context 'segment handling' {
        It 'uses the third segment and ignores org and repo' {
            Select-AltConfigs -Lines @('AnyOrg/AnyRepo/Release2024') -Configuration 'Release' |
                Should -Be @('Release2024')
        }

        It 'skips lines with fewer than three segments' {
            Select-AltConfigs -Lines @('Org/Repo', 'Release2024', 'Org/Repo/Release2025') -Configuration 'Release' |
                Should -Be @('Release2025')
        }

        It 'skips an empty configuration name' {
            Select-AltConfigs -Lines @('Org/Repo/') -Configuration 'Release' | Should -HaveCount 0
        }

        It 'keeps configuration names containing further slashes intact only up to the third segment' {
            # Documents current behaviour rather than endorsing it: Split('/') means a
            # config name cannot contain a slash. No real altConfigs.txt has one.
            Select-AltConfigs -Lines @('Org/Repo/Release/Extra') -Configuration 'Release' |
                Should -Be @('Release')
        }
    }

    Context 'blank lines and comments' {
        It 'skips blanks, whitespace-only lines and comments' {
            $lines = @('', '   ', '# a comment', '#Org/Repo/Release2024', 'Org/Repo/Release2025')
            Select-AltConfigs -Lines $lines -Configuration 'Release' | Should -Be @('Release2025')
        }

        It 'trims surrounding whitespace before parsing' {
            Select-AltConfigs -Lines @('  Org/Repo/Release2024  ') -Configuration 'Release' |
                Should -Be @('Release2024')
        }
    }

    Context 'the prefix match' {
        It 'is case-insensitive, matching the dependency-side filter' {
            # Build-Dependencies.ps1 uses OrdinalIgnoreCase; these must agree or the two
            # sides of the same feature diverge again.
            Select-AltConfigs -Lines @('Org/Repo/release2024') -Configuration 'Release' |
                Should -Be @('release2024')
        }

        It 'is a prefix match, not equality, so Release2024 and bare Release both match' {
            Select-AltConfigs -Lines @('Org/Repo/Release', 'Org/Repo/Release2024') -Configuration 'Release' |
                Should -Be @('Release','Release2024')
        }

        It 'does not match a configuration that merely contains the prefix' {
            Select-AltConfigs -Lines @('Org/Repo/NotRelease2024') -Configuration 'Release' |
                Should -HaveCount 0
        }

        It 'honours a non-Release prefix when asked' {
            Select-AltConfigs -Lines @('Org/Repo/Debug2024','Org/Repo/Release2024') -Configuration 'Debug' |
                Should -Be @('Debug2024')
        }
    }

    Context 'ordering and duplicates' {
        It 'preserves file order' {
            Select-AltConfigs -Lines @('Org/Repo/Release2026','Org/Repo/Release2022') -Configuration 'Release' |
                Should -Be @('Release2026','Release2022')
        }

        It 'removes duplicates so a repeated entry is not built twice' {
            Select-AltConfigs -Lines @('A/B/Release2024','C/D/Release2024') -Configuration 'Release' |
                Should -Be @('Release2024')
        }
    }

    Context 'empty input' {
        It 'returns nothing for an empty file' {
            Select-AltConfigs -Lines @() -Configuration 'Release' | Should -HaveCount 0
        }

        It 'returns nothing when no line matches, so the caller can skip the build entirely' {
            Select-AltConfigs -Lines @('Org/Repo/Debug2024') -Configuration 'Release' | Should -HaveCount 0
        }
    }
}
