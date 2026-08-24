using NUnit.Framework;
using System.Text.Json;

/// <summary>
/// What the compliance runners report about how much they examined.
///
/// Both runners walk the same four-exit loop: a file is dropped by the filter, dropped because
/// it is not on disk, dropped because the engine returned no result, or examined. Only the last
/// contributes to the verdict. Without a count of the four, a run that examined every file and a
/// run that examined none produce the same output and the same green check.
///
/// These assert the counts reach each output surface, and — as importantly — that they reach
/// only the surfaces where they belong. The machine-readable formats put a payload on stdout and
/// nothing may precede it, so the coverage line is gated to the human formats and the counts
/// travel structurally instead.
///
/// Process invocation via RunnerFixture, like the other tests here: the accounting is fed from
/// each runner's entry point, which compiles against BHoM types and cannot be reached in-process.
/// The arithmetic and the wording are tested directly in the hermetic project; these tests are
/// about whether the runner actually wires it up.
///
/// Reporting only. None of this changes an exit code, which is asserted rather than assumed.
/// </summary>
[TestFixture]
[Category("Integration")]
public class CoverageReportingTests
{
    // ── ComplianceRunner ──────────────────────────────────────────────────────────────

    [Test]
    [Description("A run that examines nothing says so, with the breakdown.")]
    public void ComplianceRunner_ExaminesNothing_ReportsTheDenominatorAndWarns()
    {
        // Three files relevant to a code check, none of them on disk. Every one is dropped.
        var (exitCode, stdout) = RunnerFixture.Run("ComplianceRunner",
            "code", "--output", "github", "a.cs", "b.cs", "c.cs");

        Assert.Multiple(() =>
        {
            Assert.That(exitCode, Is.EqualTo(0),
                "unchanged: reporting a count decides nothing, and whether examining nothing "
              + "should fail is a separate and open question");

            Assert.That(stdout, Does.Contain("Coverage: 0 of 3 file(s) examined"));
            Assert.That(stdout, Does.Contain("3 not found on disk"));

            // The warning is what a reader sees. It carries the breakdown, because the bare
            // fact does not distinguish an empty pull request from a selection layer handing
            // over files this check will never accept.
            Assert.That(stdout, Does.Contain("::warning title=Compliance coverage::"));
            Assert.That(stdout, Does.Contain("none was examined"));
        });
    }

    [Test]
    [Description("A file the filter discards is counted, and the discard is visible.")]
    public void ComplianceRunner_FileDroppedByFilter_IsCountedAndWarned()
    {
        // Ends with AssemblyInfo.cs, so a '*AssemblyInfo.cs' pathspec selects it, but the
        // project filter requires the name to equal AssemblyInfo.cs exactly. This is the
        // disagreement that produces a green check having examined nothing, and before this
        // change the file was dropped without appearing anywhere in the output.
        var (exitCode, stdout) = RunnerFixture.Run("ComplianceRunner",
            "project", "--output", "github", "Properties/NotAssemblyInfo.cs");

        Assert.Multiple(() =>
        {
            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(stdout, Does.Contain("Coverage: 0 of 1 file(s) examined"));
            Assert.That(stdout, Does.Contain("1 not relevant to this check"));

            // The reader is told what a drop-as-not-relevant implies rather than left to infer it.
            Assert.That(stdout, Does.Contain("disagree"));
        });
    }

    [Test]
    [Description("A run that examines something reports the count and does not warn.")]
    public void ComplianceRunner_ExaminesSomething_ReportsCoverageWithoutWarning()
    {
        // RunnerFixture's own source file: a real .cs file that exists on disk, so the code
        // check examines it rather than dropping it.
        string self = typeof(RunnerFixture).Assembly.Location;
        string dir  = Path.GetDirectoryName(self)!;
        string file = Path.Combine(dir, "coverage-probe.cs");
        File.WriteAllText(file, "// nothing to find here\n");
        try
        {
            var (exitCode, stdout) = RunnerFixture.Run("ComplianceRunner",
                "code", "--output", "github", file);

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(0));
                Assert.That(stdout, Does.Contain("Coverage: 1 of 1 file(s) examined"));
                Assert.That(stdout, Does.Contain("none dropped"));
                Assert.That(stdout, Does.Not.Contain("::warning title=Compliance coverage::"),
                    "the warning is for the zero case only, or it becomes noise and stops being read");
            });
        }
        finally
        {
            File.Delete(file);
        }
    }

    // ── Machine-readable output must stay machine-readable ────────────────────────────

    [Test]
    [Description("The json payload carries the counts, and the coverage line stays off stdout.")]
    public void ComplianceRunner_JsonOutput_ParsesCleanlyAndCarriesCounts()
    {
        // Dropped by the filter, which is silent, so nothing precedes the payload. A file that
        // is relevant but absent would print [SKIP] and break the parse for an unrelated
        // reason, which is a known separate defect; this test measures one thing.
        var (_, stdout) = RunnerFixture.Run("ComplianceRunner",
            "code", "--output", "json", "not-a-code-file.txt");

        // The property being pinned: adding coverage output must not put anything on stdout
        // ahead of the payload. If the coverage line ever stops being gated by output format,
        // this is what fails.
        using var doc = JsonDocument.Parse(stdout);

        Assert.That(doc.RootElement.TryGetProperty("coverage", out var coverage), Is.True,
            "machine-readable consumers get the counts structurally, not by parsing a console line");

        Assert.Multiple(() =>
        {
            Assert.That(coverage.GetProperty("handedIn").GetInt32(),    Is.EqualTo(1));
            Assert.That(coverage.GetProperty("examined").GetInt32(),    Is.EqualTo(0));
            Assert.That(coverage.GetProperty("notRelevant").GetInt32(), Is.EqualTo(1));
        });

        Assert.That(stdout, Does.Not.Contain("Coverage: "),
            "the human-facing line belongs to the console and github formats only");
    }

    [Test]
    [Description("The sarif payload is likewise not preceded by a coverage line.")]
    public void ComplianceRunner_SarifOutput_IsNotPrecededByCoverage()
    {
        var (_, stdout) = RunnerFixture.Run("ComplianceRunner",
            "code", "--output", "sarif", "not-a-code-file.txt");

        Assert.That(stdout.TrimStart(), Does.StartWith("{"),
            "sarif goes to stdout as a payload and nothing may precede it");
        Assert.That(stdout, Does.Not.Contain("Coverage: "));
    }

    // ── DatasetComplianceRunner: the same loop, the same instrumentation ──────────────

    [Test]
    [Description("The dataset runner reports the same denominator from the same loop shape.")]
    public void DatasetComplianceRunner_ExaminesNothing_ReportsTheDenominatorAndWarns()
    {
        // Relevant to a dataset check by path and extension, absent from disk.
        var (exitCode, stdout) = RunnerFixture.Run("DatasetComplianceRunner",
            "--output", "github", "datasets/a.json", "datasets/b.json");

        Assert.Multiple(() =>
        {
            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(stdout, Does.Contain("Coverage: 0 of 2 file(s) examined"));
            Assert.That(stdout, Does.Contain("2 not found on disk"));
            Assert.That(stdout, Does.Contain("::warning title=Compliance coverage::"));
        });
    }

    [Test]
    [Description("A non-dataset file is counted as not relevant rather than dropped silently.")]
    public void DatasetComplianceRunner_FileDroppedByFilter_IsCounted()
    {
        var (exitCode, stdout) = RunnerFixture.Run("DatasetComplianceRunner",
            "--output", "github", "src/NotADataset.json");

        Assert.Multiple(() =>
        {
            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(stdout, Does.Contain("Coverage: 0 of 1 file(s) examined"));
            Assert.That(stdout, Does.Contain("1 not relevant to this check"));
        });
    }
}
