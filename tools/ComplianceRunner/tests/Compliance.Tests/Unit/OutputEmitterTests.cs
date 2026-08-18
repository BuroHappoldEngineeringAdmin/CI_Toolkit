using NUnit.Framework;
using BH.oM.Test;

/// <summary>
/// Unit tests for the "github" branch of OutputEmitter.Write, which emits workflow-command
/// annotations. These need no BHoM engine: CheckMetadata only maps a TestStatus to strings and
/// the annotations are constructed directly.
///
/// GitHub consumes file/line/col as annotation metadata and strips them from the rendered log
/// line, so the offending file has to be present in the title property and in the message text
/// for a failure to be actionable from the job log. See CI_Toolkit_Proxy issue #22.
/// </summary>
[TestFixture]
public class OutputEmitterTests
{
    private static string EmitGitHub(List<Annotation> annotations, TestStatus status = TestStatus.Error)
    {
        var original = Console.Out;
        var buffer   = new StringWriter();
        try
        {
            Console.SetOut(buffer);
            OutputEmitter.Write("github", "documentation", status, annotations, null, false, "");
        }
        finally
        {
            Console.SetOut(original);
        }
        return buffer.ToString();
    }

    private static Annotation Failure(string path, int line, int col, string message) => new Annotation
    {
        FilePath    = path,
        LineStart   = line,
        LineEnd     = line,
        ColumnStart = col,
        ColumnEnd   = col,
        Level       = "failure",
        Message     = message,
    };

    // ── The reported defect: the file path must survive into the log-visible message ──────────

    // GitHub does not print file=/line= in the rendered ##[error] log line, so without the path
    // in the message body the log shows several identical-looking failures and the author cannot
    // tell which file to fix.
    [Test]
    public void GitHubOutput_Failure_MessageBeginsWithFileAndLine()
    {
        var output = EmitGitHub(new List<Annotation>
        {
            Failure("Some_Engine/Query/Thing.cs", 14, 90, "Engine Method must contain a Description attribute"),
        });

        var messageBody = output.Split("::", 3)[2].TrimEnd();

        Assert.That(messageBody,
            Is.EqualTo("Some_Engine/Query/Thing.cs#L14: Engine Method must contain a Description attribute"));
    }

    // BHoMBot posted annotations through the Checks API, where GitHub auto-titles each one
    // "<path>#L<line>". Workflow-command annotations get no such default, so set it explicitly to
    // restore the per-file heading the old check displayed.
    [Test]
    public void GitHubOutput_Failure_SetsTitleToFileAndLine()
    {
        var output = EmitGitHub(new List<Annotation>
        {
            Failure("Some_Engine/Query/Thing.cs", 14, 90, "message"),
        });

        Assert.That(output, Does.Contain("title=Some_Engine/Query/Thing.cs#L14"));
    }

    // GroupErrors concatenates each finding for a file into one message separated by blank lines.
    // Collapsing those newlines to spaces runs the findings together; %0A is the workflow-command
    // newline escape and restores the separation.
    [Test]
    public void GitHubOutput_MultiLineMessage_EncodesNewlinesAsPercent0A()
    {
        var output = EmitGitHub(new List<Annotation>
        {
            Failure("A.cs", 1, 0, "first finding\r\n\r\nsecond finding"),
        });

        Assert.Multiple(() =>
        {
            Assert.That(output, Does.Contain("first finding%0A%0Asecond finding"));
            Assert.That(output.TrimEnd(), Does.Not.Contain("\n"),
                "a multi-line message must be emitted as a single workflow-command line");
        });
    }

    // Annotations only attach to a diff line when the path is workspace-relative; the title must
    // be built from the same normalised path, not the raw absolute one.
    [Test]
    public void GitHubOutput_StripsWorkspacePrefixFromBothFileAndTitle()
    {
        var previous = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
        try
        {
            Environment.SetEnvironmentVariable("GITHUB_WORKSPACE", @"D:\a\Repo\Repo");

            var output = EmitGitHub(new List<Annotation>
            {
                Failure(@"D:\a\Repo\Repo\Some_Engine\Query\Thing.cs", 7, 0, "message"),
            });

            Assert.Multiple(() =>
            {
                Assert.That(output, Does.Contain("file=Some_Engine/Query/Thing.cs,"));
                Assert.That(output, Does.Contain("title=Some_Engine/Query/Thing.cs#L7"));
                Assert.That(output, Does.Not.Contain("D:/a/Repo/Repo"));
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITHUB_WORKSPACE", previous);
        }
    }

    // A finding whose engine Location carries no line number arrives with LineStart 0 (see the
    // "?? 0" fallbacks in AnnotationConvert). A "#L0" suffix would point at nothing, so the
    // location degrades to the bare path. DatasetComplianceRunner clamps to 1 and is unaffected.
    [Test]
    public void GitHubOutput_WithoutLineNumber_OmitsLineSuffixFromLocation()
    {
        var output = EmitGitHub(new List<Annotation>
        {
            new Annotation { FilePath = "A.cs", LineStart = 0, Level = "failure", Message = "message" },
        });

        Assert.Multiple(() =>
        {
            Assert.That(output, Does.Contain("title=A.cs::A.cs: message"));
            Assert.That(output, Does.Not.Contain("#L0"));
        });
    }

    // ── Guards on behaviour the fix must not disturb ──────────────────────────────────────────

    // Documentation URLs are percent-encoded ("Code%20Compliance%20and%20CI"). The runner's
    // unescape pass only reverses %25, %0D and %0A, so a literal %20 survives intact. Escaping
    // '%' here would turn it into %2520 and break every documentation link.
    [Test]
    public void GitHubOutput_PreservesPercentEncodedDocumentationUrls()
    {
        var output = EmitGitHub(new List<Annotation>
        {
            Failure("A.cs", 1, 0,
                "msg - For more information see https://bhom.xyz/documentation/DevOps/Code%20Compliance%20and%20CI/x"),
        });

        Assert.That(output, Does.Contain("Code%20Compliance%20and%20CI"));
    }

    [Test]
    public void GitHubOutput_MapsAnnotationLevelsToWorkflowCommands()
    {
        var output = EmitGitHub(new List<Annotation>
        {
            new Annotation { FilePath = "A.cs", LineStart = 1, Level = "failure", Message = "a" },
            new Annotation { FilePath = "B.cs", LineStart = 2, Level = "warning", Message = "b" },
            new Annotation { FilePath = "C.cs", LineStart = 3, Level = "notice",  Message = "c" },
        }, TestStatus.Warning);

        Assert.Multiple(() =>
        {
            Assert.That(output, Does.Contain("::error file=A.cs,line=1"));
            Assert.That(output, Does.Contain("::warning file=B.cs,line=2"));
            Assert.That(output, Does.Contain("::notice file=C.cs,line=3"));
        });
    }

    [Test]
    public void GitHubOutput_OmitsColumnWhenNotReported()
    {
        var output = EmitGitHub(new List<Annotation>
        {
            Failure("A.cs", 5, 0, "message"),
        });

        Assert.Multiple(() =>
        {
            Assert.That(output, Does.Not.Contain("col="));
            Assert.That(output, Does.Contain("line=5,title=A.cs#L5"));
        });
    }

    [Test]
    public void GitHubOutput_PassingRun_EmitsNothing()
    {
        var output = EmitGitHub(new List<Annotation>(), TestStatus.Pass);

        Assert.That(output, Is.Empty);
    }
}
