using NUnit.Framework;
using System.Text.Json;

/// <summary>
/// Characterisation tests for what the compliance runners say about how much they examined.
///
/// Today: nothing. Both runners walk the files they were handed, drop some silently and some
/// with a [SKIP] line, and report only a pass or a fail. A run that examined every file and a
/// run that examined none are indistinguishable in the output, which means a green cannot be
/// read as evidence that anything was checked.
///
/// That matters most for the case where the selection layer and the runner's own filter
/// disagree: a file can be selected by the changed-file pathspec, counted into the decision not
/// to skip the check, handed to the runner, and then discarded by the filter. The check reports
/// success having inspected nothing, and there is no number anywhere that would show it.
///
/// These are written to PASS against today's behaviour, so the absence is recorded rather than
/// described. They are the inverse of what should be true, and the comment on each says what it
/// should become.
///
/// Process invocation via RunnerFixture, like the other tests here: the accounting lives in
/// each runner's entry point, which compiles against BHoM types and cannot be reached in-process.
/// </summary>
[TestFixture]
[Category("Integration")]
public class CoverageReportingTests
{
    // ── ComplianceRunner ──────────────────────────────────────────────────────────────

    [Test]
    [Description("A run that examines nothing reports no count of what it examined.")]
    public void ComplianceRunner_ExaminesNothing_ReportsNoDenominator()
    {
        // Three files relevant to a code check, none of them on disk. Every one is dropped.
        var (exitCode, stdout) = RunnerFixture.Run("ComplianceRunner",
            "code", "--output", "github", "a.cs", "b.cs", "c.cs");

        Assert.Multiple(() =>
        {
            Assert.That(exitCode, Is.EqualTo(0), "unchanged by this work: reporting a count decides nothing");

            // Should become a Coverage line naming files examined out of files handed in.
            Assert.That(stdout, Does.Not.Contain("Coverage:"),
                "There is no denominator. Adding one is what makes a green interpretable.");
        });
    }

    [Test]
    [Description("A file the filter discards is dropped with no output at all.")]
    public void ComplianceRunner_FileDroppedByFilter_IsSilent()
    {
        // Ends with AssemblyInfo.cs, so a '*AssemblyInfo.cs' pathspec selects it, but the
        // project filter requires the name to equal AssemblyInfo.cs exactly. This is the
        // disagreement that produces a green check having examined nothing.
        var (exitCode, stdout) = RunnerFixture.Run("ComplianceRunner",
            "project", "--output", "github", "Properties/NotAssemblyInfo.cs");

        Assert.Multiple(() =>
        {
            Assert.That(exitCode, Is.EqualTo(0));

            // Unlike the absent-file path, which prints [SKIP], this one prints nothing.
            Assert.That(stdout, Does.Not.Contain("NotAssemblyInfo"),
                "The file is discarded without appearing anywhere in the output.");

            // Should become a warning naming the counts, since this is the shape of a
            // pathspec-versus-filter disagreement rather than an empty pull request.
            Assert.That(stdout, Does.Not.Contain("::warning"),
                "Nothing signals that every file handed in was dropped.");
        });
    }

    [Test]
    [Description("The json payload carries no accounting of what was examined.")]
    public void ComplianceRunner_JsonPayload_CarriesNoCounts()
    {
        var (_, stdout) = RunnerFixture.Run("ComplianceRunner",
            "code", "--output", "json", "definitely-absent.cs");

        // The [SKIP] line precedes the payload and breaks a naive parse, which is a separate
        // known defect. Take the payload from the first brace so this test measures one thing.
        int brace = stdout.IndexOf('{');
        Assert.That(brace, Is.GreaterThanOrEqualTo(0), "no json payload found at all");
        string payload = stdout[brace..];

        using var doc = JsonDocument.Parse(payload);
        Assert.That(doc.RootElement.TryGetProperty("coverage", out _), Is.False,
            "Should become a coverage object. Machine-readable consumers get counts structurally, "
          + "not by parsing a console line.");
    }

    // ── DatasetComplianceRunner: the same loop, the same blindness ────────────────────

    [Test]
    [Description("The dataset runner has the identical four-exit loop and reports no denominator.")]
    public void DatasetComplianceRunner_ExaminesNothing_ReportsNoDenominator()
    {
        // Relevant to a dataset check by path and extension, absent from disk.
        var (exitCode, stdout) = RunnerFixture.Run("DatasetComplianceRunner",
            "--output", "github", "datasets/a.json", "datasets/b.json");

        Assert.Multiple(() =>
        {
            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(stdout, Does.Not.Contain("Coverage:"),
                "Instrumenting one of two runners with the same loop would be an arbitrary "
              + "asymmetry, and the gap would be found again later.");
        });
    }

    [Test]
    [Description("A non-dataset file is dropped by the dataset filter with no output.")]
    public void DatasetComplianceRunner_FileDroppedByFilter_IsSilent()
    {
        var (exitCode, stdout) = RunnerFixture.Run("DatasetComplianceRunner",
            "--output", "github", "src/NotADataset.json");

        Assert.Multiple(() =>
        {
            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(stdout, Does.Not.Contain("NotADataset"));
            Assert.That(stdout, Does.Not.Contain("::warning"));
        });
    }
}
