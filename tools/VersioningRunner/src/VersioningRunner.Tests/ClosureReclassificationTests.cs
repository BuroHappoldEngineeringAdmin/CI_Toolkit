using VersioningRunner.Commands;
using VersioningRunner.Models;
using VersioningRunner.Tests.Fixtures;
using Xunit;

namespace VersioningRunner.Tests
{
    // The reclassification rule is easy to get subtly wrong and had no coverage at first:
    // no test calls RunCommand.Execute, so the static state v1 relied on was never populated
    // and the rule could not fire in any test. The three tests v1 broke therefore passed
    // again under its gate incidentally, not because the gate was verified. These arrange
    // the closure explicitly so the rule is actually exercised.
    public class ClosureReclassificationTests
    {
        private const string ModelQaEvent =
            "Method TryGetValueFromSource from { \"_t\" : \"System.Type\", \"Name\" : \"BH.Revit.Engine.Core.Compute, Revit_ModelQA_Engine_2022, Version=9.0.0.0, Culture=neutral, PublicKeyToken=null\", \"_bhomVersion\" : \"9.2\" } failed to deserialise.";

        private const string Config2024Event =
            "Method ProjectParameter from { \"_t\" : \"System.Type\", \"Name\" : \"BH.Revit.Engine.Core.Create, Revit_Core_Engine_2024, Version=9.0.0.0, Culture=neutral, PublicKeyToken=null\", \"_bhomVersion\" : \"9.2\" } failed to deserialise.";

        private const string SubjectAsmEvent =
            "Method Gone from { \"_t\" : \"System.Type\", \"Name\" : \"BH.Revit.Engine.Core.Compute, Revit_Core_Engine_2022, Version=9.0.0.0, Culture=neutral, PublicKeyToken=null\", \"_bhomVersion\" : \"9.2\" } failed to deserialise.";

        private static FakeTestResult Tree(string description, params string[] events)
        {
            var leaf = new FakeTestInfo
            {
                Status = "Error",
                Description = description,
                Message = "Error: Returned null from json.",
                Information = events.Select(m => (object)new FakeEventMessage { Message = m }).ToList()
            };
            return new FakeTestResult
            {
                Status = "Error",
                Information = [new FakeTestResult { Status = "Error", Information = [leaf] }]
            };
        }

        private static ClosureContext Closure(string[] loaded, string[] subject)
        {
            var l = new HashSet<string>(loaded, StringComparer.Ordinal);
            return new ClosureContext(
                l,
                new HashSet<string>(l.Select(RunCommand.StripConfigSuffix), StringComparer.Ordinal),
                new HashSet<string>(subject.Select(RunCommand.StripConfigSuffix), StringComparer.Ordinal));
        }

        // A non-empty candidate list is the runner's record that some OTHER loaded assembly
        // answered for the type.
        private static Func<string, string, string?, (string?, ClassificationPath, IReadOnlyList<string>)> Answered(
            params string[] answering)
            => (_, _, _) => (null, ClassificationPath.SignatureResolved, answering);

        private static readonly Func<string, string, string?, (string?, ClassificationPath, IReadOnlyList<string>)> NothingAnswered =
            (_, _, _) => (null, ClassificationPath.DeclaringTypeNotLoaded, Array.Empty<string>());

        private static (VersioningResult Result, FailureDiagnostic Diag) Run(
            FakeTestResult tree,
            Func<string, string, string?, (string?, ClassificationPath, IReadOnlyList<string>)> probe,
            ClosureContext? closure)
        {
            var diagnostics = new List<FailureDiagnostic>();
            var result = RunCommand.ExtractFilteredResult(
                tree, (_, _) => (true, AttributionBasis.NotRecorded), new List<RunCommand.UnverifiedFailure>(),
                (t, m, a) => probe(t, m, a), diagnostics, closure);
            return (result, Assert.Single(diagnostics));
        }

        [Fact]
        public void ForeignAssemblyAnsweredByAnother_IsUnverified()
        {
            var (result, d) = Run(
                Tree("BH.Revit.Engine.Core.Compute.TryGetValueFromSource", ModelQaEvent),
                Answered("Revit_Core_Engine_2022"),
                Closure(loaded: ["Revit_Core_Engine_2022"], subject: ["Revit_Core_Engine_2022", "Revit_oM"]));

            Assert.Equal(0, result.FailureCount);
            Assert.False(d.CountedAsReal);
            Assert.Equal(ClassificationPath.ForeignDeclaringAssembly, d.Path);
        }

        [Fact]
        public void ConfigurationVariantNotBuilt_IsUnverified()
        {
            var (result, d) = Run(
                Tree("BH.Revit.Engine.Core.Create.ProjectParameter", Config2024Event),
                Answered("Revit_Core_Engine_2022"),
                Closure(loaded: ["Revit_Core_Engine_2022"], subject: ["Revit_Core_Engine_2022"]));

            Assert.Equal(0, result.FailureCount);
            Assert.False(d.CountedAsReal);
            Assert.Equal(ClassificationPath.ConfigurationNotBuilt, d.Path);
        }

        // The v1 defect, guarded. With no answering assembly the path is
        // DeclaringTypeNotLoaded, which is how a genuinely removed type presents. v1 had no
        // candidates precondition and relabelled this as foreign, turning a real removal
        // into a silent pass.
        [Fact]
        public void AbsentAssemblyAndNothingAnswered_StaysReal()
        {
            var (result, d) = Run(
                Tree("BH.Revit.Engine.Core.Compute.Gone", SubjectAsmEvent),
                NothingAnswered,
                Closure(loaded: ["Revit_oM"], subject: ["Revit_oM"]));

            Assert.Equal(1, result.FailureCount);
            Assert.True(d.CountedAsReal);
            Assert.Equal(ClassificationPath.DeclaringTypeNotLoaded, d.Path);
        }

        [Fact]
        public void DeclaringAssemblyPresent_IsUntouched()
        {
            var (result, d) = Run(
                Tree("BH.Revit.Engine.Core.Create.ProjectParameter", Config2024Event),
                Answered("Revit_Core_Engine_2024"),
                Closure(loaded: ["Revit_Core_Engine_2024", "Revit_Core_Engine_2022"], subject: ["Revit_Core_Engine_2022"]));

            Assert.Equal(1, result.FailureCount);
            Assert.True(d.CountedAsReal);
            Assert.Equal(ClassificationPath.SignatureResolved, d.Path);
        }

        [Fact]
        public void NoClosureSupplied_IsUntouched()
        {
            var (result, d) = Run(
                Tree("BH.Revit.Engine.Core.Compute.TryGetValueFromSource", ModelQaEvent),
                Answered("Revit_Core_Engine_2022"),
                closure: null);

            Assert.Equal(1, result.FailureCount);
            Assert.True(d.CountedAsReal);
        }

        // The whole family is gone, not just one configuration of it. Nothing distinguishes
        // that from a deliberate removal, so it must not be excused.
        [Fact]
        public void SubjectFamilyWithNoLoadedVariant_StaysReal()
        {
            var (result, d) = Run(
                Tree("BH.Revit.Engine.Core.Create.ProjectParameter", Config2024Event),
                Answered("Revit_oM"),
                Closure(loaded: ["Revit_oM"], subject: ["Revit_Core_Engine_2022", "Revit_oM"]));

            Assert.Equal(1, result.FailureCount);
            Assert.True(d.CountedAsReal);
            Assert.Equal(ClassificationPath.SignatureResolved, d.Path);
        }

        [Fact]
        public void AlreadyUnverified_IsNotRelabelled()
        {
            var (result, d) = Run(
                Tree("BH.Revit.Engine.Core.Compute.TryGetValueFromSource", ModelQaEvent),
                (_, _, _) => ("RevitAPI", ClassificationPath.SignatureBlockerOutsideBHoM, new[] { "Revit_Core_Engine_2022" }),
                Closure(loaded: ["Revit_Core_Engine_2022"], subject: ["Revit_Core_Engine_2022"]));

            Assert.Equal(0, result.FailureCount);
            Assert.False(d.CountedAsReal);
            Assert.Equal(ClassificationPath.SignatureBlockerOutsideBHoM, d.Path);
            Assert.Equal("RevitAPI", d.Cause);
        }

        [Theory]
        [InlineData("Revit_Core_Engine_2024", "Revit_Core_Engine")]
        [InlineData("Revit_Core_Engine", "Revit_Core_Engine")]
        [InlineData("Structure_oM", "Structure_oM")]
        // Documents a known limitation: the heuristic cannot tell a Revit release year from
        // any other four-digit 20xx suffix. No such assembly exists in the fleet today
        // (measured: 295 of 640 match, all prefixed Revit), but nothing enforces that.
        [InlineData("Eurocode_2004", "Eurocode")]
        [InlineData("Foo_1999", "Foo_1999")]
        public void StripConfigSuffix_CollapsesOnlyA20xxTail(string input, string expected)
            => Assert.Equal(expected, RunCommand.StripConfigSuffix(input));

        // Documents current behaviour and a residual risk: the declaring assembly is taken
        // from the FIRST parsable Method event, so a finding carrying events for several
        // assemblies is decided by the first. A present assembly later in the list does not
        // stop the finding being excused.
        [Fact]
        public void MultipleMethodEvents_TheFirstNamedAssemblyDecides()
        {
            var (result, d) = Run(
                Tree("BH.Revit.Engine.Core.Compute.TryGetValueFromSource", ModelQaEvent, SubjectAsmEvent),
                Answered("Revit_Core_Engine_2022"),
                Closure(loaded: ["Revit_Core_Engine_2022"], subject: ["Revit_Core_Engine_2022"]));

            Assert.Equal("Revit_ModelQA_Engine_2022", d.DeclaringAssembly);
            Assert.Equal(0, result.FailureCount);
            Assert.Equal(ClassificationPath.ForeignDeclaringAssembly, d.Path);
        }
    }
}
