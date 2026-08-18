using NUnit.Framework;

[TestFixture]
public class ArgParserTests
{
    [TestFixture]
    public class ParseComplianceTests
    {
        // ── Missing / insufficient args ────────────────────────────────────────

        [Test]
        public void NoArgs_ReturnsNullCheckTypeAndFiles()
        {
            var result = ArgParser.ParseCompliance([]);
            Assert.That(result.CheckType, Is.Null);
            Assert.That(result.Files,     Is.Null);
        }

        [Test]
        public void OnlyCheckType_NoFiles_ReturnsNullFiles()
        {
            var result = ArgParser.ParseCompliance(["code"]);
            Assert.That(result.CheckType, Is.Null);
            Assert.That(result.Files,     Is.Null);
        }

        [Test]
        public void InvalidCheckType_ReturnsNullCheckType()
        {
            var result = ArgParser.ParseCompliance(["invalid", "file.cs"]);
            Assert.That(result.CheckType, Is.Null);
        }

        // ── Valid check types ──────────────────────────────────────────────────

        [TestCase("code")]
        [TestCase("copyright")]
        [TestCase("documentation")]
        [TestCase("project")]
        public void ValidCheckType_PresentInResult(string checkType)
        {
            var result = ArgParser.ParseCompliance([checkType, "file.cs"]);
            Assert.That(result.CheckType, Is.EqualTo(checkType));
        }

        [Test]
        public void CheckType_IsNormalisedToLowerCase()
        {
            var result = ArgParser.ParseCompliance(["CODE", "file.cs"]);
            Assert.That(result.CheckType, Is.EqualTo("code"));
        }

        // ── Files ──────────────────────────────────────────────────────────────

        [Test]
        public void MultipleFiles_AllCaptured()
        {
            var result = ArgParser.ParseCompliance(["code", "a.cs", "b.cs", "c.cs"]);
            Assert.That(result.Files, Is.EqualTo(new[] { "a.cs", "b.cs", "c.cs" }));
        }

        // ── --output flag ──────────────────────────────────────────────────────

        [TestCase("console")]
        [TestCase("github")]
        [TestCase("json")]
        [TestCase("sarif")]
        public void ValidOutputFormat_PresentInResult(string format)
        {
            var result = ArgParser.ParseCompliance(["--output", format, "code", "file.cs"]);
            Assert.That(result.OutputFormat, Is.EqualTo(format));
        }

        [Test]
        public void InvalidOutputFormat_DefaultsToConsole()
        {
            var result = ArgParser.ParseCompliance(["--output", "xml", "code", "file.cs"]);
            Assert.That(result.OutputFormat, Is.EqualTo("console"));
        }

        [Test]
        public void MissingOutputValue_DefaultsToConsole()
        {
            // --output at end of args with no following value; treated as a positional arg
            var result = ArgParser.ParseCompliance(["--output"]);
            Assert.That(result.OutputFormat, Is.EqualTo("console"));
        }

        // ── --sarif-file flag ──────────────────────────────────────────────────

        [Test]
        public void SarifFilePath_CapturedWithSarifFileFlag()
        {
            var result = ArgParser.ParseCompliance(["--sarif-file", "out.sarif", "code", "file.cs"]);
            Assert.That(result.SarifFilePath, Is.EqualTo("out.sarif"));
        }

        [Test]
        public void SarifFilePath_CapturedWithSarifFlag()
        {
            var result = ArgParser.ParseCompliance(["--sarif", "out.sarif", "code", "file.cs"]);
            Assert.That(result.SarifFilePath, Is.EqualTo("out.sarif"));
        }

        // ── --org-url flag ─────────────────────────────────────────────────────

        [Test]
        public void OrgUrl_CapturedCorrectly()
        {
            var result = ArgParser.ParseCompliance(
                ["--org-url", "https://github.com/BHoM/BHoM_Engine", "project", "file.csproj"]);
            Assert.That(result.OrgUrl, Is.EqualTo("https://github.com/BHoM/BHoM_Engine"));
        }

        [Test]
        public void OrgUrl_DefaultsToEmptyString_WhenAbsent()
        {
            var result = ArgParser.ParseCompliance(["code", "file.cs"]);
            Assert.That(result.OrgUrl, Is.Empty);
        }
    }

    [TestFixture]
    public class ParseDatasetTests
    {
        [Test]
        public void NoArgs_ReturnsNullFiles()
        {
            var result = ArgParser.ParseDataset([]);
            Assert.That(result.Files, Is.Null);
        }

        [Test]
        public void SingleFile_CapturedInFiles()
        {
            var result = ArgParser.ParseDataset(["a/datasets/foo.json"]);
            Assert.That(result.Files, Is.EqualTo(new[] { "a/datasets/foo.json" }));
        }

        [Test]
        public void MultipleFiles_AllCaptured()
        {
            var result = ArgParser.ParseDataset(["a.json", "b.json"]);
            Assert.That(result.Files, Has.Count.EqualTo(2));
        }

        [Test]
        public void InvalidOutputFormat_DefaultsToConsole()
        {
            var result = ArgParser.ParseDataset(["--output", "xyz", "foo.json"]);
            Assert.That(result.OutputFormat, Is.EqualTo("console"));
        }

        [Test]
        public void SarifFilePath_CapturedCorrectly()
        {
            var result = ArgParser.ParseDataset(["--sarif-file", "results.sarif", "foo.json"]);
            Assert.That(result.SarifFilePath, Is.EqualTo("results.sarif"));
        }
    }
}
