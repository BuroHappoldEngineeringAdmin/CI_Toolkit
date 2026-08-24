# Write-SerialisationSummary.ps1 — the serialisation check's job-summary decision, on its
# own so it can be tested without running a serialisation check.
#
# Dot-sourced by .github/actions/ci-serialisation/action.yml and by
# .github/scripts/tests/Write-SerialisationSummary.Tests.ps1. Defines a function and does
# nothing else, so dot-sourcing has no side effects.

function Get-SerialisationSummary {
    <#
    .SYNOPSIS
      Turns the serialisation check's step outcomes into the lines of its job summary.

    .DESCRIPTION
      This replaced five separate summary steps, each gated on its own combination of step
      conclusions and outputs. The problem with that shape was not any one condition: it was
      that the conditions had to enumerate the states worth reporting, and a state nobody
      enumerated produced no summary at all. Between them they named five of the action's
      eighteen functional steps, so a failure anywhere else left the check red and the summary
      empty, with nothing to tell the reader whether serialisation had even been attempted.

      A single function reached on every path cannot have that hole. The last branch is a
      catch-all, so an outcome nobody anticipated still produces a readable summary naming the
      phase it stopped in, rather than silence.

      Every parameter is a string because that is what a workflow expression yields. An absent
      value arrives as the empty string, and the empty string is meaningful here: it is how a
      step that never ran is distinguished from one that ran and failed.

    .PARAMETER DepsBranchConclusion
      Conclusion of the branch-leg dependency resolution.

    .PARAMETER BuildBranchConclusion
      Conclusion of the branch-leg build of the calling repository.

    .PARAMETER BranchRunConclusion
      Conclusion of the branch-leg serialisation run. Empty when it never ran.

    .PARAMETER BranchStatus
      Status the branch-leg runner reported: Pass, Warning or Error. Empty when it never ran.

    .PARAMETER CompareVerdict
      Verdict of the comparison step: ok, regression, baseline-unusable. Empty when the
      comparison never ran, which is every path that stopped before it.

    .PARAMETER Population, Assemblies, Legs, Failures
      Coverage figures the branch-leg runner reported. Emitted only when the branch leg
      produced them, so a summary never claims coverage the run did not measure.

    .OUTPUTS
      [string[]] markdown lines, ready to append to the step summary.
    #>
    [CmdletBinding()]
    param(
        [string]$DepsBranchConclusion  = '',
        [string]$BuildBranchConclusion = '',
        [string]$BranchRunConclusion   = '',
        [string]$BranchStatus          = '',
        [string]$CompareVerdict        = '',
        [string]$Population            = '',
        [string]$Assemblies            = '',
        [string]$Legs                  = '',
        [string]$Failures              = ''
    )

    # The dependency closure or the caller's own build failed, so serialisation never started.
    # Reported separately from every other failure because it is not a serialisation finding
    # at all and the same failure is already on the build check, where it belongs.
    if ($DepsBranchConclusion -eq 'failure' -or $BuildBranchConclusion -eq 'failure') {
        $step = if ($DepsBranchConclusion -eq 'failure') { 'Resolve dependencies (branch)' }
                else                                     { 'Build primary repo (branch)' }
        return @(
            "### Serialisation did not run"
            ""
            "Failed in: $step"
            ""
            "The dependency closure did not build, so no serialisation comparison was made."
            "This is not a serialisation finding. See the ci-build check on this pull request"
            "for the same failure."
        )
    }

    $status =
        if ($BranchRunConclusion -eq 'success' -and $BranchStatus -ne 'Error') {
            "No serialisation failures ($BranchStatus)"
        }
        elseif ($CompareVerdict -eq 'ok') {
            "Serialisation failures detected. All are pre-existing on the base branch, so this job passes."
        }
        elseif ($CompareVerdict -eq 'regression') {
            "Serialisation regression detected. Affected types are in the step log."
        }
        elseif ($BranchRunConclusion -eq 'failure' -or $CompareVerdict -eq 'baseline-unusable') {
            "Serialisation could not be verified. This is a CI runner failure, not a defect in this pull request."
        }
        else {
            # The catch-all, and the reason this function exists. Everything above describes a
            # state someone thought of; this describes the rest. It names the phase rather than
            # the step, because deriving the phase needs only the signals already passed in,
            # whereas naming the step would mean giving every step an id and threading each
            # conclusion through — which is the enumeration that failed in the first place.
            $phase =
                if ($BranchRunConclusion -eq '' -or $BranchRunConclusion -eq 'skipped') {
                    "while preparing the branch leg, before serialisation ran"
                }
                elseif ($BranchStatus -eq 'Error') {
                    "while preparing the baseline leg, after the branch leg reported failures"
                }
                else {
                    "at a point this summary cannot identify"
                }
            "Serialisation did not reach a verdict. The check stopped $phase. Look at the failed step in the log; this is a CI failure, not a finding about this pull request."
        }

    $lines = @(
        "### Serialisation"
        ""
        "| Status |"
        "|---|"
        "| $status |"
        ""
    )

    # Coverage is evidence that the run examined something, so a green summary can be told
    # apart from a vacuous one. Only emitted when the branch leg actually reported figures:
    # printing "Objects exercised: across loaded assemblies" for a run that never happened
    # would be worse than printing nothing.
    if ($Population -ne '') {
        $lines += "Objects exercised: $Population across $Assemblies loaded assemblies, $Legs legs. Failures reported by the runner: $Failures."
        $lines += ""
    }

    return $lines
}
