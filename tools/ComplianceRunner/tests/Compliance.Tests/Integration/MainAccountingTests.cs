using NUnit.Framework;
using System.Text.Json;

/// <summary>
/// Characterisation tests for ComplianceRunner.Main's file accounting and exit code.
///
/// These pin down what the entry point currently does when it examines nothing: the runner
/// reports success having inspected no file at all. They are written to PASS against today's
/// behaviour on purpose, so that a change to it shows up as a failing test rather than as a
/// silent change in what a compliance check means.
///
/// Whether examining nothing should fail, warn, or stay as it is has not been decided. Where
/// an assertion would change under a different answer, the comment says what it should become
/// and under which answer, so nothing here has to be reverse-engineered later.
///
/// Main cannot be unit-tested in-process: every branch of it compiles against BHoM types
/// (TestResult, TestStatus, ITestInformation, BH.Engine.Test.CodeCompliance.Compute,
/// BH.Engine.Base.Query), so even the usage path at :18-31, which touches no BHoM type at
/// runtime, cannot be reached without them. Process invocation via RunnerFixture is therefore
/// the only way to observe it without restructuring the runner, and it is the convention the
/// existing E2E tests already use.
/// </summary>
[TestFixture]
[Category("Integration")]
public class MainAccountingTests
{
    // ── The [SKIP] path: relevant extension, file absent (ComplianceRunner.cs:49-53) ──
    //
    // Distinct from the filter path already covered by ComplianceRunnerE2ETests. There, a
    // file is rejected by FileFilter and `continue`d silently. Here the file IS relevant, so
    // it passes the filter, and is then dropped because it is not on disk. That second drop
    // prints a line but changes nothing else: no annotation, no status change, no exit code.

    [Test]
    [Description("A relevant file that is absent from disk is announced as [SKIP] on stdout.")]
    public void MissingRelevantFile_AnnouncesSkipOnStdout()
    {
        var (_, stdout) = RunnerFixture.Run("ComplianceRunner", "code", "definitely-absent.cs");

        Assert.That(stdout, Does.Contain("[SKIP]"),
            "ComplianceRunner.cs:51 prints '  [SKIP] File not found: <file>' for a relevant "
          + "file that is not on disk. If this assertion fails the diagnostic has been removed "
          + "or reworded, and with it the only signal that a file went unexamined.");
    }

    [Test]
    [Description("Every relevant file being absent still exits 0 with status Pass.")]
    public void AllRelevantFilesMissing_ExitsZeroHavingExaminedNothing()
    {
        // Three files, all relevant to a code check, none on disk. Nothing is examined.
        var (exitCode, stdout) = RunnerFixture.Run("ComplianceRunner",
            "code", "--output", "github", "a.cs", "b.cs", "c.cs");

        Assert.Multiple(() =>
        {
            // Should become Is.EqualTo(1) if examining nothing is later treated as a failure,
            // or stay 0 with an added ::warning if it is treated as a warning instead.
            Assert.That(exitCode, Is.EqualTo(0),
                "mergedResult.Status is initialised to Pass at ComplianceRunner.cs:39 and only "
              + "ever changes via Merge inside the per-file loop. Every file skipping means the "
              + "loop body never runs, so :162 returns 0. A compliance check therefore reports "
              + "success having inspected nothing.");

            // No annotation is emitted either, so nothing in the GitHub log distinguishes
            // this from a genuine clean pass except the [SKIP] lines.
            Assert.That(stdout, Does.Not.Contain("::error"),
                "No annotation is produced when nothing was examined.");
        });
    }

    [Test]
    [Description("The count of files actually examined is reported.")]
    public void ExaminedCount_IsReported()
    {
        // This was the inverse assertion: it recorded that no count existed, and said adding one
        // was the cheapest partial mitigation and did not depend on the pass-versus-fail question
        // being settled. The count now exists, so the assertion is inverted. The pass-versus-fail
        // question is still open and this still does not touch it.
        //
        // Matches VersioningRunner, which prints a Coverage line precisely so that a pass over
        // zero and a pass over thousands are distinguishable.
        var (_, stdout) = RunnerFixture.Run("ComplianceRunner",
            "code", "--output", "github", "a.cs", "b.cs", "c.cs");

        Assert.That(stdout, Does.Contain("0 of 3 file(s) examined"),
            "A pass that examined nothing must be distinguishable from one that examined "
          + "everything, which is what the denominator is for.");
    }

    // ── Machine-readable output and the [SKIP] diagnostic ─────────────────────────────

    [Test]
    [Description("The [SKIP] diagnostic is written to stdout even when the output format is json.")]
    public void MissingRelevantFile_JsonOutput_SkipLinePrecedesTheJson()
    {
        var (exitCode, stdout) = RunnerFixture.Run("ComplianceRunner",
            "code", "--output", "json", "definitely-absent.cs");

        Assert.That(exitCode, Is.EqualTo(0));

        // ComplianceRunner.cs:51 is an unconditional Console.WriteLine, not gated on the
        // `verbose` flag that the console format sets. So in json and sarif modes the
        // diagnostic lands on the same stream as the payload, ahead of it.
        Assert.That(stdout.TrimStart(), Does.StartWith("[SKIP]").Or.StartWith("  [SKIP]"),
            "If this fails, the skip diagnostic has been moved off stdout or behind the "
          + "verbose flag, which would resolve the stream-mixing issue.");

        // The consequence, asserted rather than described: the raw stdout is not valid JSON.
        // Assert.Catch rather than Assert.Throws because the concrete type is
        // JsonReaderException, a subclass, and Assert.Throws matches the exact type only.
        Assert.Catch<JsonException>(() => JsonDocument.Parse(stdout),
            "Raw stdout does not parse as JSON once a skip line is present. Callers using "
          + "--output json must strip leading diagnostics. The existing E2E tests parse "
          + "stdout directly and only pass because the filter path prints nothing.");
    }

    // ── Exit code mapping at :162 ─────────────────────────────────────────────────────

    [Test]
    [Description("A run with no findings maps to exit 0 (the Pass and Warning half of :162).")]
    public void NoFindings_MapsToExitZero()
    {
        var (exitCode, _) = RunnerFixture.Run("ComplianceRunner",
            "code", "--output", "json", "definitely-absent.cs");

        // :162 is `return mergedResult.Status == TestStatus.Error ? 1 : 0`, so Pass and
        // Warning both map to 0. That mirrors BHoMBot deliberately (ComplianceRunner.cs:161).
        // The Error half needs a real finding from the BHoM engine and so belongs with the
        // RequiresBHoM tests, not here.
        Assert.That(exitCode, Is.EqualTo(0));
    }
}
