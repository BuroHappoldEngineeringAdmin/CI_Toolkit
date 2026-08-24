using NUnit.Framework;
using System.Text.Json;

/// <summary>
/// Unit tests for SarifBuilder.
/// SarifBuilder.Build accepts an optional toolUri parameter — tests omit it (defaults to ""),
/// so these tests run without a BHoM runtime dependency.
/// </summary>
[TestFixture]
public class SarifBuilderTests
{
    // ── Schema / structural shape ──────────────────────────────────────────────

    [Test]
    public void Build_ProducesValidJson()
    {
        Assert.DoesNotThrow(() => JsonDocument.Parse(SarifBuilder.Build("code", "Check Code Compliance", [])));
    }

    [Test]
    public void Build_SarifVersion_Is_2_1_0()
    {
        var doc = JsonDocument.Parse(SarifBuilder.Build("code", "Check Code Compliance", []));
        Assert.That(doc.RootElement.GetProperty("version").GetString(), Is.EqualTo("2.1.0"));
    }

    [Test]
    public void Build_HasSingleRun()
    {
        var doc = JsonDocument.Parse(SarifBuilder.Build("code", "Check Code Compliance", []));
        Assert.That(doc.RootElement.GetProperty("runs").GetArrayLength(), Is.EqualTo(1));
    }

    // ── Default rule (empty annotations) ─────────────────────────────────────

    [Test]
    public void Build_EmptyAnnotations_DefaultRuleIdIncludesCheckType()
    {
        var doc = JsonDocument.Parse(SarifBuilder.Build("code", "Check Code Compliance", []));
        var ruleId = doc.RootElement
            .GetProperty("runs")[0]
            .GetProperty("tool").GetProperty("driver").GetProperty("rules")[0]
            .GetProperty("id").GetString();
        Assert.That(ruleId, Is.EqualTo("BHoM.code"));
    }

    [Test]
    public void Build_EmptyAnnotations_NoResults()
    {
        var doc = JsonDocument.Parse(SarifBuilder.Build("code", "Check Code Compliance", []));
        var resultCount = doc.RootElement.GetProperty("runs")[0].GetProperty("results").GetArrayLength();
        Assert.That(resultCount, Is.EqualTo(0));
    }

    // ── Result level mapping ───────────────────────────────────────────────────

    [Test]
    public void Build_FailureAnnotation_LevelMapsToError()
    {
        var annotations = new List<Annotation>
        {
            new() { Level = "failure", RuleName = "HasValidCopyright", FilePath = "test.cs", LineStart = 1, Message = "Missing copyright" }
        };
        var doc = JsonDocument.Parse(SarifBuilder.Build("copyright", "Check Copyright Compliance", annotations));
        var level = doc.RootElement.GetProperty("runs")[0].GetProperty("results")[0]
            .GetProperty("level").GetString();
        Assert.That(level, Is.EqualTo("error"));
    }

    [Test]
    public void Build_WarningAnnotation_LevelMapsToWarning()
    {
        var annotations = new List<Annotation>
        {
            new() { Level = "warning", RuleName = "SomeRule", FilePath = "test.cs", LineStart = 5, Message = "A warning" }
        };
        var doc = JsonDocument.Parse(SarifBuilder.Build("code", "Check Code Compliance", annotations));
        var level = doc.RootElement.GetProperty("runs")[0].GetProperty("results")[0]
            .GetProperty("level").GetString();
        Assert.That(level, Is.EqualTo("warning"));
    }

    // ── Multiple annotations ───────────────────────────────────────────────────

    [Test]
    public void Build_MultipleAnnotations_AllResultsPresent()
    {
        var annotations = new List<Annotation>
        {
            new() { Level = "failure", RuleName = "Rule1", FilePath = "a.cs", LineStart = 1, Message = "E1" },
            new() { Level = "warning", RuleName = "Rule2", FilePath = "b.cs", LineStart = 2, Message = "W1" },
            new() { Level = "failure", RuleName = "Rule1", FilePath = "c.cs", LineStart = 3, Message = "E2" },
        };
        var doc = JsonDocument.Parse(SarifBuilder.Build("code", "Check Code Compliance", annotations));
        var count = doc.RootElement.GetProperty("runs")[0].GetProperty("results").GetArrayLength();
        Assert.That(count, Is.EqualTo(3));
    }

    [Test]
    public void Build_DistinctRuleNames_EachGetsOwnRule()
    {
        var annotations = new List<Annotation>
        {
            new() { Level = "failure", RuleName = "RuleA", FilePath = "a.cs", LineStart = 1, Message = "E1" },
            new() { Level = "failure", RuleName = "RuleB", FilePath = "b.cs", LineStart = 2, Message = "E2" },
            new() { Level = "failure", RuleName = "RuleA", FilePath = "c.cs", LineStart = 3, Message = "E3" }, // duplicate of RuleA
        };
        var doc = JsonDocument.Parse(SarifBuilder.Build("code", "Check Code Compliance", annotations));
        var ruleCount = doc.RootElement.GetProperty("runs")[0]
            .GetProperty("tool").GetProperty("driver").GetProperty("rules").GetArrayLength();
        Assert.That(ruleCount, Is.EqualTo(2)); // RuleA and RuleB — no duplicates
    }

    // ── File path handling ────────────────────────────────────────────────────

    [Test]
    public void Build_BackslashFilePath_IsNormalisedToForwardSlashes()
    {
        var annotations = new List<Annotation>
        {
            new() { Level = "failure", RuleName = "Rule1", FilePath = @"src\foo\bar.cs", LineStart = 1, Message = "E" }
        };
        var doc = JsonDocument.Parse(SarifBuilder.Build("code", "Check Code Compliance", annotations));
        var uri = doc.RootElement.GetProperty("runs")[0].GetProperty("results")[0]
            .GetProperty("locations")[0]
            .GetProperty("physicalLocation").GetProperty("artifactLocation").GetProperty("uri")
            .GetString();
        Assert.That(uri, Is.EqualTo("src/foo/bar.cs"));
    }

    // ── Region ────────────────────────────────────────────────────────────────

    [Test]
    public void Build_ZeroLineStart_DefaultsRegionToLine1()
    {
        var annotations = new List<Annotation>
        {
            new() { Level = "failure", RuleName = "Rule1", FilePath = "a.cs", LineStart = 0, Message = "E" }
        };
        var doc = JsonDocument.Parse(SarifBuilder.Build("code", "Check Code Compliance", annotations));
        var startLine = doc.RootElement.GetProperty("runs")[0].GetProperty("results")[0]
            .GetProperty("locations")[0]
            .GetProperty("physicalLocation").GetProperty("region")
            .GetProperty("startLine").GetInt32();
        Assert.That(startLine, Is.EqualTo(1));
    }
}
