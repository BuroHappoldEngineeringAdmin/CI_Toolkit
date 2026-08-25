using NUnit.Framework;
using System.Text.Json;

/// <summary>
/// End-to-end tests for ComplianceRunner (code / copyright / documentation / project checks).
///
/// Tests marked [Category("RequiresBHoM")] invoke the BHoM engine and need BHoM installed.
/// Tests without that category exercise filtering/argument paths that return before any BHoM
/// call is made, so they work on any machine with the solution already built.
/// </summary>
[TestFixture]
[Category("Integration")]
public class ComplianceRunnerE2ETests
{
    // ── Usage / bad args ───────────────────────────────────────────────────────

    [Test]
    public void NoArgs_ExitsWithCode1()
    {
        var (exitCode, _) = RunnerFixture.Run("ComplianceRunner");
        Assert.That(exitCode, Is.EqualTo(1));
    }

    [Test]
    public void MissingFiles_ExitsWithCode1()
    {
        // Only a check type provided, no files — runner should print usage and exit 1.
        var (exitCode, _) = RunnerFixture.Run("ComplianceRunner", "code");
        Assert.That(exitCode, Is.EqualTo(1));
    }

    [Test]
    public void UnknownCheckType_ExitsWithCode1()
    {
        var (exitCode, _) = RunnerFixture.Run("ComplianceRunner", "unknown", "file.cs");
        Assert.That(exitCode, Is.EqualTo(1));
    }

    // ── File filtering — no BHoM call made ────────────────────────────────────

    [Test]
    [Description("Non-.cs files given to a code check are filtered out before the BHoM engine is called.")]
    public void NonRelevantFiles_JsonOutput_ExitsWithCode0AndPassStatus()
    {
        // Pass a file whose extension is not relevant for "code" compliance.
        // The runner filters by extension first, so File.Exists is never checked
        // and BHoM is never invoked.
        var (exitCode, stdout) = RunnerFixture.Run("ComplianceRunner",
            "code", "--output", "json", "nonexistent.md");

        Assert.That(exitCode, Is.EqualTo(0));
        var json = JsonDocument.Parse(stdout);
        Assert.Multiple(() =>
        {
            Assert.That(json.RootElement.GetProperty("status").GetString(),          Is.EqualTo("Pass"));
            Assert.That(json.RootElement.GetProperty("annotationCount").GetInt32(),  Is.EqualTo(0));
        });
    }

    [Test]
    [Description("Non-.csproj / non-AssemblyInfo files for project check are filtered before BHoM is called.")]
    public void NonRelevantFiles_ProjectCheck_ExitsWithCode0()
    {
        var (exitCode, _) = RunnerFixture.Run("ComplianceRunner",
            "project", "--output", "json", "irrelevant.cs");
        Assert.That(exitCode, Is.EqualTo(0));
    }

    // ── JSON output structure ─────────────────────────────────────────────────

    [Test]
    public void JsonOutput_ContainsAllExpectedTopLevelKeys()
    {
        var (_, stdout) = RunnerFixture.Run("ComplianceRunner",
            "code", "--output", "json", "nonexistent.md");
        var root = JsonDocument.Parse(stdout).RootElement;
        Assert.Multiple(() =>
        {
            Assert.That(root.TryGetProperty("status",          out _), Is.True, "status");
            Assert.That(root.TryGetProperty("checkType",       out _), Is.True, "checkType");
            Assert.That(root.TryGetProperty("title",           out _), Is.True, "title");
            Assert.That(root.TryGetProperty("summary",         out _), Is.True, "summary");
            Assert.That(root.TryGetProperty("annotationCount", out _), Is.True, "annotationCount");
            Assert.That(root.TryGetProperty("annotations",     out _), Is.True, "annotations");
        });
    }

    [Test]
    public void JsonOutput_CheckType_MatchesInput()
    {
        var (_, stdout) = RunnerFixture.Run("ComplianceRunner",
            "copyright", "--output", "json", "nonexistent.md");
        var checkType = JsonDocument.Parse(stdout).RootElement.GetProperty("checkType").GetString();
        Assert.That(checkType, Is.EqualTo("copyright"));
    }

    // ── GitHub Actions output format ──────────────────────────────────────────

    [Test]
    public void GitHubOutput_ForPassingRun_ProducesNoFindingAnnotations()
    {
        // This previously asserted stdout was entirely empty. That silence was the defect: a run
        // that examined nothing looked exactly like a run that examined everything. What a
        // passing run must not produce is a *finding* annotation, which is what the name means
        // and what is asserted now. The coverage output is not a finding and changes no verdict.
        var (exitCode, stdout) = RunnerFixture.Run("ComplianceRunner",
            "code", "--output", "github", "nonexistent.md");

        Assert.Multiple(() =>
        {
            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(stdout, Does.Not.Contain("::error"), "no findings, so no error annotations");
            Assert.That(stdout, Does.Contain("Coverage: 0 of 1 file(s) examined"));
            Assert.That(stdout, Does.Contain("::warning title=Compliance coverage::"),
                "the one annotation a passing run may produce is the report that it examined nothing");
        });
    }
}
