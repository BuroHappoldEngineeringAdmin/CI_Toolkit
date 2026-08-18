using NUnit.Framework;
using System.Text.RegularExpressions;
using BH.oM.Test;
using BH.oM.Test.CodeCompliance;

/// <summary>
/// Unit tests for the preformatted (grouped) branch of AnnotationConvert.ToAnnotationEquivalent.
/// These use an Error with an empty DocumentationLink and no Location so no BHoM engine call is
/// made (DocumentationUrl short-circuits to "", IFullMessage is not invoked). The IFullMessage
/// branch used by project/dataset checks needs the engine and is exercised by the RequiresBHoM
/// integration tests.
/// </summary>
[TestFixture]
public class AnnotationConvertTests
{
    // Grouped code/copyright/documentation findings arrive with Error.Message already set to the
    // concatenated FullMessage(). The preformatted path must take it verbatim and must NOT
    // re-append the documentation suffix — this guards the double-suffix regression that the
    // GroupErrors work fixed.
    [Test]
    public void Preformatted_TakesMessageVerbatim_NoDoubleSuffix()
    {
        var err = new Error
        {
            Status            = TestStatus.Error,
            Message           = "Copyright message is invalid - For more information see https://x/HasValidCopyright",
            DocumentationLink = "",   // empty => DocumentationUrl short-circuits, no engine call
        };

        var ann = err.ToAnnotationEquivalent(messageIsPreformatted: true);

        Assert.Multiple(() =>
        {
            Assert.That(ann.Level, Is.EqualTo("failure"));
            Assert.That(ann.Message, Is.EqualTo("Copyright message is invalid - For more information see https://x/HasValidCopyright"));
            Assert.That(Regex.Matches(ann.Message, "For more information see").Count, Is.EqualTo(1),
                "preformatted message must not have the documentation suffix appended a second time");
        });
    }

    [Test]
    public void Preformatted_TrimsTrailingWhitespace()
    {
        // GroupErrors ends each concatenated message with two newlines; the annotation trims them.
        var err = new Error { Status = TestStatus.Warning, Message = "msg\n\n", DocumentationLink = "" };

        var ann = err.ToAnnotationEquivalent(messageIsPreformatted: true);

        Assert.Multiple(() =>
        {
            Assert.That(ann.Message, Is.EqualTo("msg"));
            Assert.That(ann.Level, Is.EqualTo("warning"));
        });
    }
}
