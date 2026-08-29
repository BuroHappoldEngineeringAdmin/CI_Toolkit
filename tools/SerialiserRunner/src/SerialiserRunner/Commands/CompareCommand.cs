using SerialiserRunner.Models;

namespace SerialiserRunner.Commands;

public record CompareResult(bool IsRegression, string Summary, bool IsBaselineUnusable = false);

public static class CompareCommand
{
    // POLICY VALUE, NOT A MEASUREMENT. The share of the tested population that must fail on
    // the base branch before we refuse to diff against it. Set by CI ownership, not derived
    // from anything. Overridable per-repo via the ci-serialisation action's
    // implausible_baseline_ratio input; change it there, not here.
    //
    // HISTORICAL: the 99.3% failure rate (5981 of 6024) this value was originally chosen
    // against was the pre-fix serialiser, and the defect behind it was fixed on 2026-08-03 by
    // the System.Drawing.Common reference in SerialiserRunner.csproj. Healthy baselines since
    // then report at or near zero failures over a population of roughly six thousand.
    //
    // Any value from roughly 0.25 to 0.90 therefore behaves identically on everything observed
    // so far, but the reason is not that intermediate baselines are impossible. It is that the
    // only two states seen to date sit at opposite ends of that band: near 0% with a healthy
    // serialiser, near 100% with a globally broken one. Intermediate states are reachable:
    // failures and population are summed across three independent converter legs, so a defect
    // confined to one of them lands in the low tens of percent. Choosing inside the band is a
    // choice about which partial states to refuse, not a free one.
    public const double DefaultImplausibleBaselineRatio = 0.5;

    public static CompareResult Compare(SerialiserResult baseline, SerialiserResult branch,
                                        double implausibleBaselineRatio = DefaultImplausibleBaselineRatio)
    {
        // BHoMBot RunCheck(Serialisation): a branch result of Pass or Warning is
        // always a success and is never compared against the baseline.
        if (branch.Status is TestStatus.Pass or TestStatus.Warning)
            return new CompareResult(false, $"Branch status: {branch.Status} — no regression check needed.");

        // Population is 0 when it could not be read off the Verify summaries, in which case
        // the guard is skipped rather than run against a fabricated denominator.
        if (baseline.Population > 0 && baseline.Failures.Count >= baseline.Population * implausibleBaselineRatio)
            return new CompareResult(false,
                $"Baseline unusable, cannot diff: {baseline.Failures.Count} of {baseline.Population} items "
              + $"({baseline.Failures.Count * 100.0 / baseline.Population:F1}%) fail serialisation on the base "
              + "branch. A baseline this broken cannot establish what the branch changed. This is a runner "
              + "environment failure, not a defect in this pull request.",
                IsBaselineUnusable: true);

        // Legacy comparison is COUNT-aware (List semantics), keyed on each failing
        // entry's Description. Reproduced verbatim from BHoMBot's
        // InformationIsEqual / InformationIsLessAndBetter (see the two helpers).
        List<FailureInfo> main = baseline.Failures;
        List<FailureInfo> branchInfo = branch.Failures;

        if (InformationIsEqual(main, branchInfo))
            return new CompareResult(false,
                "No serialisation regression: same failure count as baseline and every failure present on the baseline.");

        if (InformationIsLessAndBetter(main, branchInfo))
            return new CompareResult(false,
                $"Improvement: branch has {branchInfo.Count} failure(s) vs baseline {main.Count}, all present on the baseline.");

        // Legacy else-branch: regression. Preserve the two legacy summary shapes and
        // the cascade annotation (which does not affect the verdict).
        var baselineDescs = main.Select(f => f.Description).ToHashSet(StringComparer.Ordinal);
        var newFailures = branchInfo
            .Where(f => !baselineDescs.Contains(f.Description))
            .OrderBy(f => f.Description, StringComparer.Ordinal)
            .ToList();

        string summary;
        if (newFailures.Count > 0)
            summary = $"Regression: {newFailures.Count} new failure(s) introduced.\n"
                    + string.Join("\n", newFailures.Select(f =>
                    {
                        string line = $"  - {f.Description}";
                        if (f.SuspectedRootCauses.Count > 0)
                            line += $" (possible cascade from: {string.Join(", ", f.SuspectedRootCauses)})";
                        return line;
                    }));
        else
            // No new failing type, but more failures than the baseline (e.g. an
            // already-failing type now fails an additional verification dimension).
            summary = $"Regression: serialisation failure count increased "
                    + $"({branchInfo.Count} vs baseline {main.Count}) with no net improvement.";

        return new CompareResult(true, summary);
    }

    // BHoMBot Serialisation.InformationIsEqual: equal entry count AND every branch
    // failure's Description exists on the baseline (existence, not multiplicity).
    private static bool InformationIsEqual(List<FailureInfo> main, List<FailureInfo> branch)
    {
        if (main.Count != branch.Count)
            return false;
        return branch.All(b => main.Any(m => m.Description == b.Description));
    }

    // BHoMBot Serialisation.InformationIsLessAndBetter: strictly fewer entries than
    // the baseline AND every branch failure's Description exists on the baseline.
    private static bool InformationIsLessAndBetter(List<FailureInfo> main, List<FailureInfo> branch)
    {
        if (main.Count <= branch.Count)
            return false;
        return branch.All(b => main.Any(m => m.Description == b.Description));
    }
}
