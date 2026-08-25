# Get-StagedAssemblies.Tests.ps1 — Pester tests for the staged-assembly comparison.
#
# The comparison decides which assemblies the versioning check treats as the repository's own,
# so getting it wrong reproduces the defect it replaces: a subject set that looks plausible and
# silently covers part of the repository.
#
# Get-NewlyStagedAssemblies is pure over two stamp collections, so every case below is checked
# without a build. Get-AssemblyStamp touches the filesystem and is tested against real temp
# directories, because its contract is about what it does with files that are missing, nested
# or not assemblies.
#
# Run locally:  pwsh -Command "Invoke-Pester .github/scripts/tests -Output Detailed"
# Run in CI:    lint-workflows.yml, the powershell-tests job.

BeforeAll {
    $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
    . (Join-Path $repoRoot '.github/scripts/Get-StagedAssemblies.ps1')
}

Describe 'Get-NewlyStagedAssemblies' {

    Context 'the ordinary case' {

        It 'returns assemblies that were not there before' {
            $before = @('Dep_oM.dll|100', 'Dep_Engine.dll|100')
            $after  = @('Dep_oM.dll|100', 'Dep_Engine.dll|100', 'Subject_oM.dll|200')

            Get-NewlyStagedAssemblies -Before $before -After $after | Should -Be @('Subject_oM.dll')
        }

        It 'returns nothing when the build staged nothing' {
            $stamps = @('Dep_oM.dll|100', 'Dep_Engine.dll|100')

            @(Get-NewlyStagedAssemblies -Before $stamps -After $stamps).Count | Should -Be 0
        }

        It 'returns every new assembly, not just the first' {
            $after = @('A_oM.dll|200', 'B_Engine.dll|200', 'C_Adapter.dll|200')

            Get-NewlyStagedAssemblies -Before @() -After $after |
                Should -Be @('A_oM.dll', 'B_Engine.dll', 'C_Adapter.dll')
        }
    }

    Context 'the name-collision case, which a name-only comparison gets wrong' {

        # A repository can produce an assembly whose file name already exists in the staging
        # directory from a dependency. The staging step overwrites it in place, so the name is
        # present both before and after. Comparing names alone concludes nothing changed and
        # drops the repository's own assembly from its subject set — which is exactly the
        # partial-subject failure this mechanism replaces, reintroduced one layer down.
        It 'detects an assembly overwritten in place' {
            $before = @('Shared_oM.dll|100')
            $after  = @('Shared_oM.dll|200')

            Get-NewlyStagedAssemblies -Before $before -After $after | Should -Be @('Shared_oM.dll')
        }

        It 'distinguishes an overwrite from an untouched file with the same name' {
            $before = @('Untouched_oM.dll|100', 'Overwritten_oM.dll|100')
            $after  = @('Untouched_oM.dll|100', 'Overwritten_oM.dll|555')

            Get-NewlyStagedAssemblies -Before $before -After $after | Should -Be @('Overwritten_oM.dll')
        }

        It 'reports an overwritten assembly once, not twice' {
            # It appears in both inputs; the result is a set of names, not a concatenation.
            $r = Get-NewlyStagedAssemblies -Before @('X_oM.dll|1') -After @('X_oM.dll|2')
            @($r).Count | Should -Be 1
        }
    }

    Context 'shape' {

        # The contract is a sequence of names that counts correctly when the caller wraps it
        # with @(), which is what the action does before writing one line per entry. An earlier
        # version comma-wrapped the return, which produced an array containing the array: it
        # counted as one element regardless of content, so an empty result and a three-element
        # result were indistinguishable to the caller.
        It 'counts correctly for one result' {
            @(Get-NewlyStagedAssemblies -Before @() -After @('Only_oM.dll|1')).Count | Should -Be 1
        }

        It 'counts correctly for no result' {
            @(Get-NewlyStagedAssemblies -Before @('A.dll|1') -After @('A.dll|1')).Count | Should -Be 0
        }

        It 'counts correctly for many results' {
            @(Get-NewlyStagedAssemblies -Before @() -After @('A.dll|1','B.dll|1','C.dll|1')).Count | Should -Be 3
        }

        It 'tolerates empty inputs on both sides' {
            @(Get-NewlyStagedAssemblies -Before @() -After @()).Count | Should -Be 0
        }
    }
}

Describe 'Get-AssemblyStamp' {

    BeforeAll {
        $script:dir = Join-Path ([IO.Path]::GetTempPath()) ("stamp-" + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        New-Item -ItemType Directory -Force -Path (Join-Path $dir 'nested') | Out-Null
        Set-Content -Path (Join-Path $dir 'One_oM.dll')          -Value 'x'
        Set-Content -Path (Join-Path $dir 'Two_Engine.dll')      -Value 'x'
        Set-Content -Path (Join-Path $dir 'notes.txt')           -Value 'x'
        Set-Content -Path (Join-Path $dir 'nested\Deep_oM.dll')  -Value 'x'
    }

    AfterAll { Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue }

    It 'stamps each assembly with its name and write time' {
        $s = Get-AssemblyStamp -Path $dir
        ($s | Where-Object { $_ -like 'One_oM.dll|*' }).Count | Should -Be 1
    }

    It 'ignores files that are not assemblies' {
        Get-AssemblyStamp -Path $dir | Should -Not -Contain 'notes.txt'
        (Get-AssemblyStamp -Path $dir | Where-Object { $_ -like 'notes*' }).Count | Should -Be 0
    }

    # The staging directory is flat: every project copies its own output into it. Recursing
    # would pick up anything a dependency happened to nest there and attribute it to the
    # repository under test.
    It 'does not recurse' {
        (Get-AssemblyStamp -Path $dir | Where-Object { $_ -like 'Deep_oM*' }).Count | Should -Be 0
    }

    It 'returns empty for a directory that does not exist, rather than throwing' {
        { Get-AssemblyStamp -Path (Join-Path $dir 'no-such-place') } | Should -Not -Throw
        @(Get-AssemblyStamp -Path (Join-Path $dir 'no-such-place')).Count | Should -Be 0
    }

    It 'round-trips through the comparison to name a genuinely new assembly' {
        $before = Get-AssemblyStamp -Path $dir
        Start-Sleep -Milliseconds 20
        Set-Content -Path (Join-Path $dir 'Fresh_oM.dll') -Value 'x'
        $after = Get-AssemblyStamp -Path $dir

        Get-NewlyStagedAssemblies -Before $before -After $after | Should -Be @('Fresh_oM.dll')
    }
}
