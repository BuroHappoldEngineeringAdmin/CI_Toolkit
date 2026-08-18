using NUnit.Framework;
using BH.oM.Test;

[TestFixture]
public class CheckMetadataTests
{
    // ── Title mapping ──────────────────────────────────────────────────────────

    [TestCase("code",          "Check Code Compliance")]
    [TestCase("copyright",     "Check Copyright Compliance")]
    [TestCase("documentation", "Check Documentation Compliance")]
    [TestCase("project",       "Check Project Compliance")]
    [TestCase("dataset",       "Check Dataset Compliance")]
    [TestCase("unknown",       "Check Compliance")]
    [TestCase(null,            "Check Compliance")]
    public void GetOutput_ReturnsCorrectTitle(string? checkType, string expectedTitle)
    {
        CheckMetadata.GetOutput(checkType, TestStatus.Pass, out var title, out _, out _);
        Assert.That(title, Is.EqualTo(expectedTitle));
    }

    // ── Pass: summary and text are empty ──────────────────────────────────────

    [TestCase("code")]
    [TestCase("copyright")]
    [TestCase("documentation")]
    [TestCase("project")]
    [TestCase("dataset")]
    public void GetOutput_Pass_EmptySummaryAndText(string checkType)
    {
        CheckMetadata.GetOutput(checkType, TestStatus.Pass, out _, out var summary, out var text);
        Assert.That(summary, Is.Empty);
        Assert.That(text,    Is.Empty);
    }

    // ── Error: summary and text are non-empty ─────────────────────────────────

    [TestCase("code")]
    [TestCase("copyright")]
    [TestCase("documentation")]
    [TestCase("project")]
    [TestCase("dataset")]
    public void GetOutput_Error_SummaryAndTextAreNonEmpty(string checkType)
    {
        CheckMetadata.GetOutput(checkType, TestStatus.Error, out _, out var summary, out var text);
        Assert.That(summary, Is.Not.Empty);
        Assert.That(text,    Is.Not.Empty);
    }

    // ── Error: check-type-specific text content ───────────────────────────────

    [Test]
    public void GetOutput_Error_ProjectHasCsprojOrAssemblyInfoInText()
    {
        CheckMetadata.GetOutput("project", TestStatus.Error, out _, out _, out var text);
        Assert.That(text, Does.Contain(".csproj").Or.Contain("AssemblyInfo"));
    }

    [Test]
    public void GetOutput_Error_DatasetHasDatasetInText()
    {
        CheckMetadata.GetOutput("dataset", TestStatus.Error, out _, out _, out var text);
        Assert.That(text, Does.Contain("dataset"));
    }

    // ── Warning: summary and text are non-empty ───────────────────────────────

    [Test]
    public void GetOutput_Warning_NonEmptySummaryAndText()
    {
        CheckMetadata.GetOutput("code", TestStatus.Warning, out _, out var summary, out var text);
        Assert.That(summary, Is.Not.Empty);
        Assert.That(text,    Is.Not.Empty);
    }
}
