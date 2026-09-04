using VersioningRunner.Commands;
using VersioningRunner.Models;
using VersioningRunner.Tests.Fixtures;
using Xunit;

namespace VersioningRunner.Tests
{
    // Attribution decides whether a failure is the subject repository's at all. It used to be
    // decided on the description string, which cannot answer the question: a repository that
    // declares a namespace other repositories extend was attributed all of their failures.
    // Measured on a real run of a repository declaring BH.Adapter: 47 findings reported, spread
    // across 28 other repositories' adapter namespaces, none of them its own.
    //
    // The cases below fix the shape of the fix, not just its outcome. In particular they pin
    // that a declaring assembly which says "not yours" is honoured rather than falling through
    // to the namespace, because falling through is what would quietly reinstate the defect.
    public class SubjectAttributionDecisionTests
    {
        // The event shape the dataset actually produces. The "Name" field is assembly-qualified
        // and is the only place the declaring assembly appears.
        private static string MethodEvent(string type, string assembly) =>
            "Method Push from { \"_t\" : \"System.Type\", \"Name\" : \"" + type + ", " + assembly
            + ", Version=9.0.0.0, Culture=neutral, PublicKeyToken=null\", \"_bhomVersion\" : \"9.2\" } failed to deserialise.";

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

        private static ClosureContext Closure(params string[] subject) =>
            new(new HashSet<string>(StringComparer.Ordinal),
                new HashSet<string>(StringComparer.Ordinal),
                new HashSet<string>(subject.Select(RunCommand.StripConfigSuffix), StringComparer.Ordinal));

        // BHoM_Adapter's real subject set and namespaces, from validation run 32837506868.
        private static readonly HashSet<string> AdapterNamespaces =
            new(["BH.Adapter", "BH.Engine.Adapter", "BH.oM.Adapter"], StringComparer.Ordinal);

        private static ClosureContext AdapterClosure() =>
            Closure("Adapter_Engine", "Adapter_oM", "BHoM_Adapter", "Structure_AdapterModules");

        public class TheDecision
        {
            // The defect, stated as a test. Namespace matching accepts this because the subject
            // declares BH.Adapter and the type sits below it; the declaring assembly says it
            // belongs to another repository, and that is the answer that must win.
            [Fact]
            public void ForeignAssemblyUnderTheSubjectsOwnNamespaceRoot_IsNotAttributed()
            {
                var (attributable, basis) = RunCommand.AttributeToSubject(
                    "BH.Adapter.ETABS.ETABSAdapter..ctor", "ETABS_Adapter",
                    AdapterNamespaces, AdapterClosure());

                Assert.False(attributable);
                Assert.Equal(AttributionBasis.DeclaringAssembly, basis);
            }

            // The same string, with no assembly recorded, still matches by namespace. This is
            // the residue the runner counts and warns about: it is wrong, and it is retained
            // because dropping it would lose genuine regressions that carry no Method event.
            [Fact]
            public void TheSameStringWithNoAssembly_FallsBackToNamespaceAndIsStillWrong()
            {
                var (attributable, basis) = RunCommand.AttributeToSubject(
                    "BH.Adapter.ETABS.ETABSAdapter..ctor", null,
                    AdapterNamespaces, AdapterClosure());

                Assert.True(attributable);
                Assert.Equal(AttributionBasis.NamespaceFallback, basis);
            }

            [Fact]
            public void SubjectsOwnAssembly_IsAttributed()
            {
                var (attributable, basis) = RunCommand.AttributeToSubject(
                    "BH.Adapter.BHoMAdapter.Push", "BHoM_Adapter",
                    AdapterNamespaces, AdapterClosure());

                Assert.True(attributable);
                Assert.Equal(AttributionBasis.DeclaringAssembly, basis);
            }

            // Attribution must not depend on the namespace agreeing. A subject assembly may
            // declare types anywhere, and the assembly is the authority.
            [Fact]
            public void SubjectsOwnAssemblyWithAnUnrelatedNamespace_IsStillAttributed()
            {
                var (attributable, _) = RunCommand.AttributeToSubject(
                    "BH.Something.Entirely.Else.Thing", "Structure_AdapterModules",
                    AdapterNamespaces, AdapterClosure());

                Assert.True(attributable);
            }

            // Revit repositories build one assembly per year. The subject staged _2022; a
            // record naming _2024 is the same assembly to this repository, which is why the
            // comparison strips the configuration suffix.
            [Fact]
            public void ConfigurationSuffixedVariantOfASubjectAssembly_IsAttributed()
            {
                var (attributable, _) = RunCommand.AttributeToSubject(
                    "BH.Revit.Engine.Core.Create.ProjectParameter", "Revit_Core_Engine_2024",
                    new HashSet<string>(["BH.Revit.Engine.Core"], StringComparer.Ordinal),
                    Closure("Revit_Core_Engine_2022"));

                Assert.True(attributable);
            }

            // No subject set was established, so nothing can be called this repository's.
            // Returning true here would attribute the whole closure on no basis.
            [Fact]
            public void NoClosure_AttributesNothingByAssembly()
            {
                var (attributable, basis) = RunCommand.AttributeToSubject(
                    "BH.Adapter.BHoMAdapter.Push", "BHoM_Adapter", AdapterNamespaces, closure: null);

                Assert.False(attributable);
                Assert.Equal(AttributionBasis.DeclaringAssembly, basis);
            }

            [Theory]
            [InlineData("")]
            [InlineData("   ")]
            public void BlankAssemblyName_IsNotTreatedAsASubjectMatch(string assembly)
                => Assert.False(RunCommand.IsFromSubjectAssembly(assembly, AdapterClosure()));
        }

        public class ThroughTheCollector
        {
            private static (VersioningResult Result, List<FailureDiagnostic> Diags,
                            List<RunCommand.UnverifiedFailure> Unverified) Run(
                FakeTestResult tree, HashSet<string> namespaces, ClosureContext closure)
            {
                var diagnostics = new List<FailureDiagnostic>();
                var unverified = new List<RunCommand.UnverifiedFailure>();
                var result = RunCommand.ExtractFilteredResult(
                    tree,
                    (d, a) => RunCommand.AttributeToSubject(d, a, namespaces, closure),
                    unverified,
                    // Nothing answered for the type: this is the DeclaringTypeNotLoaded shape
                    // all 47 observed findings had, and the shape whose existing reclassification
                    // gate deliberately refuses to act. Attribution must therefore stop it, and
                    // this asserts that it does rather than relying on the classifier.
                    (_, _, _) => (null, ClassificationPath.DeclaringTypeNotLoaded, Array.Empty<string>()),
                    diagnostics, closure);
                return (result, diagnostics, unverified);
            }

            [Fact]
            public void ForeignFinding_IsDroppedAtAttributionRatherThanClassified()
            {
                var (result, diags, _) = Run(
                    Tree("BH.Adapter.ETABS.ETABSAdapter..ctor",
                         MethodEvent("BH.Adapter.ETABS.ETABSAdapter", "ETABS_Adapter")),
                    AdapterNamespaces, AdapterClosure());

                Assert.Equal(0, result.FailureCount);
                // No diagnostic at all: it was never the subject's failure, so there is nothing
                // to classify. A diagnostic here would mean attribution had let it through.
                Assert.Empty(diags);
            }

            [Fact]
            public void SubjectFinding_SurvivesAndRecordsTheAssemblyAsItsBasis()
            {
                var (result, diags, _) = Run(
                    Tree("BH.Adapter.BHoMAdapter.Push",
                         MethodEvent("BH.Adapter.BHoMAdapter", "BHoM_Adapter")),
                    AdapterNamespaces, AdapterClosure());

                Assert.Equal(1, result.FailureCount);
                Assert.Equal(AttributionBasis.DeclaringAssembly, Assert.Single(diags).AttributedBy);
            }

            // The counter the run output reports has to be able to see this path, so the basis
            // is recorded on the row rather than inferred from the absence of an assembly.
            //
            // The finding is kept and reported, and is deliberately NOT counted as real: a
            // prefix cannot say which of two repositories sharing a namespace owns a type, so
            // the verdict must not rest on it. Asserted here rather than only on DeriveStatus
            // because the routing is what makes the count zero, and a test on the status alone
            // would still pass if the finding were dropped outright.
            [Fact]
            public void FindingWithNoMethodEvent_IsReportedUnverifiedAndDoesNotGate()
            {
                var (result, diags, unverified) = Run(
                    Tree("BH.Adapter.BHoMAdapter.Push"),
                    AdapterNamespaces, AdapterClosure());

                Assert.Equal(0, result.FailureCount);
                Assert.Equal(VersioningStatus.Warning,
                    RunCommand.DeriveStatus(result.FailureCount, unverified.Count));
                Assert.Equal(0, RunCommand.ExitCodeFor(
                    RunCommand.DeriveStatus(result.FailureCount, unverified.Count)));

                // Not dropped: still one row, still labelled, with the basis and the reason on it.
                var only = Assert.Single(diags);
                Assert.Equal(AttributionBasis.NamespaceFallback, only.AttributedBy);
                Assert.False(only.CountedAsReal);
                Assert.Contains("inferred from a namespace prefix", only.Cause);
                Assert.Equal("BH.Adapter.BHoMAdapter.Push", Assert.Single(unverified).Description);
            }

            // A declaring assembly outranks the policy above: it is read from the record rather
            // than guessed, so a finding carrying one still gates. Without this, routing the
            // fallback to the unverified bucket could be widened to everything and no test
            // would notice the check had stopped failing altogether.
            [Fact]
            public void PolicyDoesNotDisarmFindingsAttributedByAssembly()
            {
                var (result, diags, unverified) = Run(
                    Tree("BH.Adapter.BHoMAdapter.Push",
                         MethodEvent("BH.Adapter.BHoMAdapter", "BHoM_Adapter")),
                    AdapterNamespaces, AdapterClosure());

                Assert.Equal(1, result.FailureCount);
                Assert.Empty(unverified);
                Assert.True(Assert.Single(diags).CountedAsReal);
                Assert.Equal(VersioningStatus.Error,
                    RunCommand.DeriveStatus(result.FailureCount, unverified.Count));
            }

            // The observed run in miniature: one of the subject's own failures among several
            // other repositories' entries under the same namespace root.
            [Fact]
            public void MixedTree_KeepsOnlyTheSubjectsOwn()
            {
                var outer = new FakeTestResult
                {
                    Status = "Error",
                    Information =
                    [
                        Tree("BH.Adapter.ETABS.ETABSAdapter..ctor", MethodEvent("BH.Adapter.ETABS.ETABSAdapter", "ETABS_Adapter")),
                        Tree("BH.Adapter.Revit.RevitAdapter..ctor", MethodEvent("BH.Adapter.Revit.RevitAdapter", "Revit_Adapter")),
                        Tree("BH.Adapter.Mongo.MongoAdapter..ctor", MethodEvent("BH.Adapter.Mongo.MongoAdapter", "Mongo_Adapter")),
                        Tree("BH.Adapter.BHoMAdapter.Push",        MethodEvent("BH.Adapter.BHoMAdapter", "BHoM_Adapter"))
                    ]
                };

                var (result, diags, _) = Run(outer, AdapterNamespaces, AdapterClosure());

                Assert.Equal(1, result.FailureCount);
                Assert.Equal("BH.Adapter.BHoMAdapter.Push", Assert.Single(result.Failures).Description);
                Assert.Equal(AttributionBasis.DeclaringAssembly, Assert.Single(diags).AttributedBy);
            }
        }

        // A lost subject set must stay Error. It is a precondition failure, not a finding, and
        // the "do not gate on inferred attribution" policy must not reach it.
        //
        // Why this needs pinning. When no subject assembly list is supplied or the file is
        // absent, ReadSubjectAssemblyList returns null, BuildSubjectNamespaces returns null and
        // attribution falls back to the whole loaded closure, which over-reports massively:
        // measured at 1056 of 1056 findings belonging to other repositories. That path records
        // AttributionBasis.NotRecorded, and the policy keys on NamespaceFallback, so the two do
        // not collide and the over-reporting run still fails. That safety is incidental rather
        // than designed: both paths are "match the description against a set of namespaces", so
        // unifying them is a natural-looking refactor, and doing so without noticing would
        // silently convert the whole-closure red into a green tick. These tests fail if that
        // happens.
        //
        // Observed on BHoM/Versioning_Toolkit PR #348, run 33619426914: the action's own guard
        // caught the missing list and exited 1 before the runner started, so this is defence in
        // depth behind that guard, not the only protection.
        public class WhenTheSubjectSetIsLost
        {
            // The three-segment prefix set the whole-closure path matches on.
            private static readonly HashSet<string> LoadedPrefixes =
                new(["BH.Adapter.ETABS", "BH.oM.Structure.Elements"], StringComparer.Ordinal);

            private static (VersioningResult Result, List<FailureDiagnostic> Diags,
                            List<RunCommand.UnverifiedFailure> Unverified) RunWholeClosure(FakeTestResult tree)
            {
                var diagnostics = new List<FailureDiagnostic>();
                var unverified = new List<RunCommand.UnverifiedFailure>();
                var result = RunCommand.ExtractFilteredResult(
                    tree,
                    // Exactly the lambda RunCommand.Execute installs when subjectNamespaces is null.
                    (d, _) => (RunCommand.IsFromLoadedNamespace(d, LoadedPrefixes), AttributionBasis.NotRecorded),
                    unverified,
                    (_, _, _) => (null, ClassificationPath.DeclaringTypeNotLoaded, Array.Empty<string>()),
                    diagnostics, closure: null);
                return (result, diagnostics, unverified);
            }

            [Fact]
            public void WholeClosureFindingWithNoMethodEvent_StillCountsAsRealAndStillGates()
            {
                var (result, diags, unverified) = RunWholeClosure(
                    Tree("BH.Adapter.ETABS.ETABSAdapter..ctor"));

                // No method event, so no declaring assembly — the same input shape that routes
                // to the unverified bucket under subject attribution. Here it must not.
                Assert.Equal(1, result.FailureCount);
                Assert.Empty(unverified);

                var only = Assert.Single(diags);
                Assert.Equal(AttributionBasis.NotRecorded, only.AttributedBy);
                Assert.True(only.CountedAsReal);
                Assert.Null(only.Cause);

                Assert.Equal(VersioningStatus.Error,
                    RunCommand.DeriveStatus(result.FailureCount, unverified.Count));
                Assert.Equal(1, RunCommand.ExitCodeFor(
                    RunCommand.DeriveStatus(result.FailureCount, unverified.Count)));
            }

            // The policy is keyed on the basis and nothing else, so state that directly: these
            // are distinct enum values and NotRecorded is not the fallback.
            [Fact]
            public void WholeClosureBasisIsNotTheNamespaceFallback()
                => Assert.NotEqual(AttributionBasis.NamespaceFallback, AttributionBasis.NotRecorded);
        }
    }
}
