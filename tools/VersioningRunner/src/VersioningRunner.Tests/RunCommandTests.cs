using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using VersioningRunner.Commands;
using VersioningRunner.Models;
using VersioningRunner.Tests.Fixtures;
using Xunit;

namespace VersioningRunner.Tests
{
    public class VersioningResultTests
    {
        [Fact]
        public void VersioningResult_RoundTrips_Via_Json()
        {
            var result = new VersioningResult
            {
                Status = VersioningStatus.Error,
                FailureCount = 1,
                Failures = [new FailureInfo("BH.oM.Foo.Bar", "Error: returned null.")]
            };

            var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
            string json = JsonSerializer.Serialize(result, options);
            VersioningResult? deserialized = JsonSerializer.Deserialize<VersioningResult>(json, options);

            Assert.NotNull(deserialized);
            Assert.Equal(VersioningStatus.Error, deserialized.Status);
            Assert.Equal(1, deserialized.FailureCount);
            Assert.Equal("BH.oM.Foo.Bar", deserialized.Failures[0].Description);
        }
    }

    public class IsFromLoadedNamespaceTests
    {
        private static HashSet<string> Prefixes(params string[] prefixes) =>
            new(prefixes, StringComparer.Ordinal);

        [Fact]
        public void TypeInLoadedNamespace_ReturnsTrue()
        {
            var prefixes = Prefixes("BH.oM.Base", "BH.oM.Geometry", "BH.Engine.Base");
            Assert.True(RunCommand.IsFromLoadedNamespace("BH.oM.Base.BHoMObject", prefixes));
            Assert.True(RunCommand.IsFromLoadedNamespace("BH.oM.Geometry.Point", prefixes));
        }

        [Fact]
        public void TypeNotInLoadedNamespace_ReturnsFalse()
        {
            var prefixes = Prefixes("BH.oM.Base", "BH.oM.Geometry");
            Assert.False(RunCommand.IsFromLoadedNamespace("BH.oM.Acoustic.Panel", prefixes));
            Assert.False(RunCommand.IsFromLoadedNamespace("BH.oM.Structure.Elements.Bar", prefixes));
        }

        [Fact]
        public void MethodSignature_ExtractsTypePrefix()
        {
            var prefixes = Prefixes("BH.Engine.Base");
            Assert.True(RunCommand.IsFromLoadedNamespace("BH.Engine.Base.Query.DeepClone(BH.oM.Base.IObject)", prefixes));
        }

        [Fact]
        public void EmptyDescription_ReturnsFalse()
        {
            Assert.False(RunCommand.IsFromLoadedNamespace("", Prefixes("BH.oM.Base")));
            Assert.False(RunCommand.IsFromLoadedNamespace("   ", Prefixes("BH.oM.Base")));
        }

        [Fact]
        public void TooShortDescription_ReturnsFalse()
        {
            Assert.False(RunCommand.IsFromLoadedNamespace("BH.oM", Prefixes("BH.oM.Base")));
            Assert.False(RunCommand.IsFromLoadedNamespace("BHoMObject", Prefixes("BH.oM.Base")));
        }
    }

    public class ExtractFilteredResultTests
    {
        private static HashSet<string> PrefixesFor(params string[] types) =>
            RunCommand.BuildNamespacePrefixes([typeof(object).Assembly]);  // use mscorlib as stand-in

        [Fact]
        public void ExtractFilteredResult_Null_ReturnsError()
        {
            var result = RunCommand.ExtractFilteredResult(null, []);
            Assert.Equal(VersioningStatus.Error, result.Status);
        }

        [Fact]
        public void ExtractFilteredResult_NoLoadedAssemblies_FiltersAllFailures()
        {
            // Build a nested fake result: outer → version summary → individual type failure
            var leafFailure = new FakeTestInfo
            {
                Status = "Error",
                Description = "BH.oM.Acoustic.Panel",
                Message = "Result returned as CustomObject"
            };
            var versionSummary = new FakeTestResult
            {
                Status = "Error",
                Information = [leafFailure]
            };
            var outer = new FakeTestResult
            {
                Status = "Error",
                Information = [versionSummary]
            };

            // No loaded assemblies → no namespace prefixes → all filtered
            var result = RunCommand.ExtractFilteredResult(outer, []);
            Assert.Equal(VersioningStatus.Pass, result.Status);
            Assert.Equal(0, result.FailureCount);
        }

        [Fact]
        public void ExtractFilteredResult_LoadedAssemblyMatches_IncludesFailure()
        {
            // A failure for a type in the System.* namespace (mscorlib / System.Private.CoreLib)
            var leafFailure = new FakeTestInfo
            {
                Status = "Error",
                Description = "System.Collections.Generic.List",
                Message = "err"
            };
            var versionSummary = new FakeTestResult
            {
                Status = "Error",
                Information = [leafFailure]
            };
            var outer = new FakeTestResult
            {
                Status = "Error",
                Information = [versionSummary]
            };

            // Load the core assembly which contains System.Collections.Generic.*
            var assemblies = new List<System.Reflection.Assembly> { typeof(List<>).Assembly };
            var result = RunCommand.ExtractFilteredResult(outer, assemblies);

            Assert.Equal(VersioningStatus.Error, result.Status);
            Assert.Equal(1, result.FailureCount);
            Assert.Equal("System.Collections.Generic.List", result.Failures[0].Description);
        }

        [Fact]
        public void ExtractFilteredResult_MixedFailures_OnlyIncludesLoadedOnes()
        {
            // Two failures: one from System (loaded), one from BH.oM.Acoustic (not loaded)
            var systemFailure = new FakeTestInfo
            {
                Status = "Error",
                Description = "System.Collections.Generic.Dictionary",
                Message = "err"
            };
            var bhomFailure = new FakeTestInfo
            {
                Status = "Error",
                Description = "BH.oM.Acoustic.Panel",
                Message = "err"
            };
            var versionSummary = new FakeTestResult
            {
                Status = "Error",
                Information = [systemFailure, bhomFailure]
            };
            var outer = new FakeTestResult
            {
                Status = "Error",
                Information = [versionSummary]
            };

            var assemblies = new List<System.Reflection.Assembly> { typeof(Dictionary<,>).Assembly };
            var result = RunCommand.ExtractFilteredResult(outer, assemblies);

            Assert.Equal(VersioningStatus.Error, result.Status);
            Assert.Equal(1, result.FailureCount);
            Assert.Equal("System.Collections.Generic.Dictionary", result.Failures[0].Description);
        }

        [Fact]
        public void ExtractFilteredResult_LeafWithEventMessages_IncludesFailure()
        {
            // BHoM's FromJsonItem populates Information with EventMessage objects from
            // CurrentEvents() when FromJson returns a CustomObject (unknown type).
            // EventMessage carries a Status but no Information, so it must not be
            // confused with a nested TestResult: walking into it means the real
            // failure above is read as an internal node and its Description — the
            // only thing the namespace filter can match — is never seen.
            var eventMessage = new FakeEventMessage { Message = "Could not find type System.Collections.Generic.FakeType." };
            var leafFailure = new FakeTestInfo
            {
                Status = "Error",
                Description = "System.Collections.Generic.FakeType",
                Message = "Error: Result returned as CustomObject",
                Information = [eventMessage]
            };
            var versionSummary = new FakeTestResult
            {
                Status = "Error",
                Information = [leafFailure]
            };
            var outer = new FakeTestResult
            {
                Status = "Error",
                Information = [versionSummary]
            };

            var assemblies = new List<System.Reflection.Assembly> { typeof(List<>).Assembly };
            var result = RunCommand.ExtractFilteredResult(outer, assemblies);

            Assert.Equal(VersioningStatus.Error, result.Status);
            Assert.Equal(1, result.FailureCount);
            Assert.Equal("System.Collections.Generic.FakeType", result.Failures[0].Description);
        }
    }

    public class BuildNamespacePrefixesTests
    {
        [Fact]
        public void PartiallyLoadedAssembly_StillRegistersLoadableTypes()
        {
            // Simulates the Revit-versioned assembly case: GetTypes() throws
            // ReflectionTypeLoadException because some types couldn't resolve their
            // dependencies (e.g. RevitAPI.dll absent), but ex.Types contains the
            // types that DID load. Those must still contribute their namespace
            // prefixes — otherwise the namespace filter silently drops genuine
            // failures targeting those prefixes.
            var partial = new[]
            {
                typeof(System.Collections.Generic.List<>),  // → prefix "System.Collections.Generic"
                null,
                typeof(System.Threading.Tasks.Task)         // → prefix "System.Threading.Tasks"
            };
            var stub = new ThrowingAssembly(
                new ReflectionTypeLoadException(partial, [null, new Exception("missing dep"), null]));

            var prefixes = RunCommand.BuildNamespacePrefixes([stub]);

            Assert.Contains("System.Collections.Generic", prefixes);
            Assert.Contains("System.Threading.Tasks", prefixes);
        }

        [Fact]
        public void AssemblyThrowingNonReflectionLoadError_IsSkippedCleanly()
        {
            var stub = new ThrowingAssembly(new InvalidOperationException("boom"));
            var prefixes = RunCommand.BuildNamespacePrefixes([stub]);

            Assert.Empty(prefixes);
        }

        // Minimal Assembly subclass for tests. Overrides only GetTypes; other members
        // are not exercised by BuildNamespacePrefixes.
        private sealed class ThrowingAssembly : Assembly
        {
            private readonly Exception _toThrow;
            public ThrowingAssembly(Exception toThrow) => _toThrow = toThrow;
            public override Type[] GetTypes() => throw _toThrow;
        }
    }

    public class ClassifyInvocationFailureTests
    {
        private static HashSet<string> BHoMPrefixes() =>
            new(["BH.oM.Base", "BH.Engine.Base", "BH.Adapter.OpenAI"], StringComparer.Ordinal);

        [Fact]
        public void FileNotFoundException_ForSystemDrawingCommon_IsInfrastructure()
        {
            // Models the live failure: VersioningRunner reflects over BHoM types and the
            // System.Drawing.Common assembly can't be located on the modern .NET runtime.
            var fnf = new FileNotFoundException(
                "Could not load file or assembly 'System.Drawing.Common'",
                "System.Drawing.Common");
            var (name, isInfra) = RunCommand.ClassifyInvocationFailure(fnf, BHoMPrefixes());

            Assert.True(isInfra);
            Assert.Equal("System.Drawing.Common", name);
        }

        [Fact]
        public void FileNotFoundException_ForBHoMAssembly_IsNotInfrastructure()
        {
            var fnf = new FileNotFoundException("Could not load", "BH.oM.Base.Geometry.Point");
            var (name, isInfra) = RunCommand.ClassifyInvocationFailure(fnf, BHoMPrefixes());

            Assert.False(isInfra);
            Assert.Equal("BH.oM.Base.Geometry.Point", name);
        }

        [Fact]
        public void FileLoadException_ForMicrosoftAssembly_IsInfrastructure()
        {
            var fle = new FileLoadException("could not load", "Microsoft.Identity.Client");
            var (name, isInfra) = RunCommand.ClassifyInvocationFailure(fle, BHoMPrefixes());

            Assert.True(isInfra);
            Assert.Equal("Microsoft.Identity.Client", name);
        }

        [Fact]
        public void UnknownThirdPartyAssembly_DefaultsToRealFailure()
        {
            // Third-party deps we don't recognise: be conservative.
            var fnf = new FileNotFoundException("could not load", "Newtonsoft.Json");
            var (name, isInfra) = RunCommand.ClassifyInvocationFailure(fnf, BHoMPrefixes());

            Assert.False(isInfra);
            Assert.Equal("Newtonsoft.Json", name);
        }

        [Fact]
        public void ExceptionWithNoExtractableName_DefaultsToRealFailure()
        {
            var ex = new InvalidOperationException("something broke");
            var (name, isInfra) = RunCommand.ClassifyInvocationFailure(ex, BHoMPrefixes());

            Assert.False(isInfra);
            Assert.Equal(string.Empty, name);
        }

        [Fact]
        public void TypeIdentityWithCommaTail_IsStrippedToBareName()
        {
            var fnf = new FileNotFoundException(
                "could not load",
                "System.Drawing.Common, Version=4.0.0.1, Culture=neutral");
            var (name, isInfra) = RunCommand.ClassifyInvocationFailure(fnf, BHoMPrefixes());

            Assert.True(isInfra);
            Assert.Equal("System.Drawing.Common", name);
        }
    }

    public class IsLoadableAssemblyTests
    {
        [Theory]
        [InlineData(@"C:\assemblies\Rhino_oM.dll")]
        [InlineData(@"C:\assemblies\Structure_Engine.dll")]
        [InlineData(@"C:\assemblies\BHoM_Adapter.dll")]
        [InlineData(@"C:\assemblies\Versioning_Test.dll")]
        public void StandardSuffixes_ReturnTrue(string path)
        {
            Assert.True(RunCommand.IsLoadableAssembly(path));
        }

        [Theory]
        [InlineData(@"C:\assemblies\Revit_Toolkit2024.dll")]
        [InlineData(@"C:\assemblies\Revit_Toolkit2023.dll")]
        [InlineData(@"C:\assemblies\BH_Revit_Toolkit2025.dll")]
        public void RevitVersionedAssembly_ReturnTrue(string path)
        {
            Assert.True(RunCommand.IsLoadableAssembly(path));
        }

        [Theory]
        [InlineData(@"C:\assemblies\SomeLibrary.dll")]
        [InlineData(@"C:\assemblies\BH.oM.Base.pdb")]
        [InlineData(@"C:\assemblies\BH.oM.Base.xml")]
        [InlineData(@"C:\assemblies\Revit2024.dll")]
        public void NonBHoMFiles_ReturnFalse(string path)
        {
            Assert.False(RunCommand.IsLoadableAssembly(path));
        }
    }

    public class SubjectAttributionTests
    {
        private static HashSet<string> Namespaces(params string[] ns) =>
            new(ns, StringComparer.Ordinal);

        [Fact]
        public void TypeInSubjectNamespace_IsAttributed()
        {
            Assert.True(RunCommand.IsFromSubjectNamespace(
                "BH.oM.Adapters.File.FileSettings", Namespaces("BH.oM.Adapters.File")));
        }

        [Fact]
        public void TypeInNestedSubjectNamespace_IsAttributed()
        {
            Assert.True(RunCommand.IsFromSubjectNamespace(
                "BH.oM.Adapters.File.Requests.FileRequest", Namespaces("BH.oM.Adapters.File")));
        }

        [Fact]
        public void MethodDescription_IsAttributed()
        {
            // Versioning_Toolkit's DescriptionFromJson yields "DeclaringType.MethodName".
            Assert.True(RunCommand.IsFromSubjectNamespace(
                "BH.Revit.Engine.MechanicalPlumbing.Compute.SpaceRoomPairs",
                Namespaces("BH.Revit.Engine.MechanicalPlumbing")));
        }

        [Fact]
        public void SiblingNamespace_IsNotAttributed()
        {
            // The regression this exists for: a 3-segment prefix collapsed this to
            // BH.oM.Adapters and attributed every adapter's types to every repo.
            var subject = Namespaces("BH.oM.Adapters.File");
            Assert.False(RunCommand.IsFromSubjectNamespace("BH.oM.Adapters.ETABS.Elements.Pier", subject));
            Assert.False(RunCommand.IsFromSubjectNamespace("BH.oM.Adapters.Sharepoint.File", subject));
            Assert.False(RunCommand.IsFromSubjectNamespace("BH.oM.Structure.Elements.Bar", subject));
        }

        [Fact]
        public void NamespaceThatIsAStringPrefixButNotASegment_IsNotAttributed()
        {
            Assert.False(RunCommand.IsFromSubjectNamespace(
                "BH.oM.Adapters.FileSystem.Thing", Namespaces("BH.oM.Adapters.File")));
        }

        [Fact]
        public void TypeDirectlyInTheSubjectNamespaceItself_IsNotAttributed()
        {
            // The namespace alone is not a failure description; there is nothing to blame.
            Assert.False(RunCommand.IsFromSubjectNamespace(
                "BH.oM.Adapters.File", Namespaces("BH.oM.Adapters.File")));
        }

        [Fact]
        public void EmptyDescriptionOrEmptySubjectSet_IsNotAttributed()
        {
            Assert.False(RunCommand.IsFromSubjectNamespace("", Namespaces("BH.oM.Adapters.File")));
            Assert.False(RunCommand.IsFromSubjectNamespace("   ", Namespaces("BH.oM.Adapters.File")));
            Assert.False(RunCommand.IsFromSubjectNamespace("BH.oM.Adapters.File.FileSettings", Namespaces()));
        }

        [Fact]
        public void NoSubjectDirSupplied_FallsBackToWholeClosure()
        {
            Assert.Null(RunCommand.BuildSubjectNamespaces([typeof(object).Assembly], null));
            Assert.Null(RunCommand.BuildSubjectNamespaces([typeof(object).Assembly], "   "));
        }

        [Fact]
        public void MissingSubjectDir_FallsBackToWholeClosure()
        {
            string missing = Path.Combine(Path.GetTempPath(), "versioning-runner-no-such-dir-" + Guid.NewGuid());
            Assert.Null(RunCommand.BuildSubjectNamespaces([typeof(object).Assembly], missing));
        }

        [Fact]
        public void SubjectDirNamingALoadedAssembly_YieldsThatAssemblysNamespaces()
        {
            var asm = typeof(object).Assembly;
            string dir = Path.Combine(Path.GetTempPath(), "versioning-runner-subject-" + Guid.NewGuid());
            Directory.CreateDirectory(dir);
            try
            {
                // Only the file name is used; the content is never loaded, because the
                // namespaces come off the already-loaded assembly of the same name.
                File.WriteAllText(Path.Combine(dir, Path.GetFileName(asm.Location)), "");

                var ns = RunCommand.BuildSubjectNamespaces([asm], dir);

                Assert.NotNull(ns);
                Assert.Contains("System.Collections.Generic", ns);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void SubjectAssemblyInATfmSubdirectory_IsFound()
        {
            // SDK-style repos append the TFM to OutputPath, so the subject's assemblies sit
            // in Build\<tfm>\ rather than Build\. A top-level-only scan silently produced an
            // empty subject set, and an empty set means the check cannot fail.
            var asm = typeof(object).Assembly;
            string dir = Path.Combine(Path.GetTempPath(), "versioning-runner-subject-" + Guid.NewGuid());
            string nested = Path.Combine(dir, "netstandard2.0");
            Directory.CreateDirectory(nested);
            try
            {
                File.WriteAllText(Path.Combine(nested, Path.GetFileName(asm.Location)), "");

                var ns = RunCommand.BuildSubjectNamespaces([asm], dir);

                Assert.NotNull(ns);
                Assert.Contains("System.Collections.Generic", ns);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void SubjectDirNamingNothingLoaded_YieldsNoNamespacesRatherThanTheClosure()
        {
            string dir = Path.Combine(Path.GetTempPath(), "versioning-runner-subject-" + Guid.NewGuid());
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "Nothing_Built_Here.dll"), "");

                var ns = RunCommand.BuildSubjectNamespaces([typeof(object).Assembly], dir);

                Assert.NotNull(ns);
                Assert.Empty(ns);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        // Strings below are copied verbatim from a real run's EventMessages.
        private const string RevitCause =
            "Type Autodesk.Revit.DB.Document, RevitAPI, Version=9.0.0.0, Culture=neutral, PublicKeyToken=null failed to deserialise.";
        private const string MethodCause =
            "Method ApplyDuctInsulation from { \"_t\" : \"System.Type\", \"Name\" : \"BH.Revit.Engine.MechanicalPlumbing.Compute, Revit_MechanicalPlumbing_Engine_2022, Version=9.0.0.0, Culture=neutral, PublicKeyToken=null\", \"_bhomVersion\" : \"9.2\" } failed to deserialise.";

        [Fact]
        public void OnlyNonBHoMTypesFailed_IsClassifiedUnresolvable()
        {
            Assert.Equal("Autodesk.Revit.DB.Document",
                RunCommand.ClassifyUnresolvableCause([RevitCause, MethodCause]));
        }

        [Fact]
        public void MethodEventEmbeddingABHoMTypeInJson_DoesNotCountAsATypeFailure()
        {
            // The Method event embeds a BH. type in its JSON payload. Only "Type ..."
            // events name what failed, or every Revit failure would read as real.
            Assert.NotNull(RunCommand.ClassifyUnresolvableCause([RevitCause, MethodCause]));
        }

        [Fact]
        public void ABHoMTypeAmongTheFailures_IsReal()
        {
            Assert.Null(RunCommand.ClassifyUnresolvableCause([
                RevitCause,
                "Type BH.oM.Structure.Elements.Bar, Structure_oM, Version=9.0.0.0 failed to deserialise.",
                MethodCause]));
        }

        [Fact]
        public void NoTypeEventsAtAll_IsReal()
        {
            Assert.Null(RunCommand.ClassifyUnresolvableCause([MethodCause]));
            Assert.Null(RunCommand.ClassifyUnresolvableCause([]));
            Assert.Null(RunCommand.ClassifyUnresolvableCause(["", "   "]));
        }

        [Fact]
        public void UnresolvableLeaf_IsCollectedSeparatelyAndNotFailed()
        {
            var leaf = new FakeTestInfo
            {
                Status = "Error",
                Description = "BH.Revit.Engine.MechanicalPlumbing.Compute.ApplyDuctInsulation",
                Message = "Error: Returned null from json.",
                Information = [
                    new FakeEventMessage { Message = RevitCause },
                    new FakeEventMessage { Message = MethodCause }]
            };
            var versionSummary = new FakeTestResult { Status = "Error", Information = [leaf] };
            var outer = new FakeTestResult { Status = "Error", Information = [versionSummary] };

            var skips = new List<RunCommand.UnverifiedFailure>();
            var result = RunCommand.ExtractFilteredResult(outer, _ => true, skips);

            Assert.Equal(VersioningStatus.Pass, result.Status);
            Assert.Equal(0, result.FailureCount);
            Assert.Single(skips);
            Assert.Equal("Autodesk.Revit.DB.Document", skips[0].Cause);
        }

        [Fact]
        public void LeafNamingABHoMTypeFailure_StillFails()
        {
            var leaf = new FakeTestInfo
            {
                Status = "Error",
                Description = "BH.oM.Structure.Elements.Bar",
                Message = "Error: Returned null from json.",
                Information = [new FakeEventMessage
                {
                    Message = "Type BH.oM.Structure.Elements.Bar, Structure_oM, Version=9.0.0.0 failed to deserialise."
                }]
            };
            var versionSummary = new FakeTestResult { Status = "Error", Information = [leaf] };
            var outer = new FakeTestResult { Status = "Error", Information = [versionSummary] };

            var skips = new List<RunCommand.UnverifiedFailure>();
            var result = RunCommand.ExtractFilteredResult(outer, _ => true, skips);

            Assert.Equal(VersioningStatus.Error, result.Status);
            Assert.Equal(1, result.FailureCount);
            Assert.Empty(skips);
        }

        [Fact]
        public void MethodEvent_YieldsDeclaringTypeAndMethodName()
        {
            var (type, method) = RunCommand.ParseMethodEvent(MethodCause);
            Assert.Equal("BH.Revit.Engine.MechanicalPlumbing.Compute", type);
            Assert.Equal("ApplyDuctInsulation", method);
        }

        [Fact]
        public void NonMethodEvent_YieldsNothing()
        {
            Assert.Equal((null, null), RunCommand.ParseMethodEvent(RevitCause));
            Assert.Equal((null, null), RunCommand.ParseMethodEvent(""));
            Assert.Equal((null, null), RunCommand.ParseMethodEvent("Method with no json payload"));
        }

        [Fact]
        public void ResolvableSignature_HasNoBlocker()
        {
            Assert.Null(RunCommand.ProbeSignatureBlocker(typeof(string), nameof(string.Substring)));
        }

        [Fact]
        public void MethodNameThatDoesNotExist_IsAGenuineFindingNotABlocker()
        {
            // No overload of that name means the method is gone, which is exactly what
            // versioning exists to catch. It must not be classified away.
            Assert.Null(RunCommand.ProbeSignatureBlocker(typeof(string), "NoSuchMethodHere"));
        }

        [Fact]
        public void NoTypeEventsButAnUnresolvableSignature_IsUnverifiedNotFailed()
        {
            // The real case: Create.DuctLogicalOrFilter(bool,bool,bool) exists with exactly
            // the recorded signature, but returns Autodesk.Revit.DB.LogicalOrFilter, so no
            // type-level cause is ever recorded. The probe is the only way to tell.
            var leaf = new FakeTestInfo
            {
                Status = "Error",
                Description = "BH.Revit.Engine.MechanicalPlumbing.Create. }",
                Message = "Error: Returned null from json.",
                Information = [new FakeEventMessage
                {
                    Message = MethodCause.Replace("ApplyDuctInsulation", "DuctLogicalOrFilter")
                                         .Replace(".Compute,", ".Create,")
                }]
            };
            var versionSummary = new FakeTestResult { Status = "Error", Information = [leaf] };
            var outer = new FakeTestResult { Status = "Error", Information = [versionSummary] };

            var skips = new List<RunCommand.UnverifiedFailure>();
            var result = RunCommand.ExtractFilteredResult(outer, _ => true, skips,
                probeSignature: (_, _, _) => ("Autodesk.Revit.DB.LogicalOrFilter", ClassificationPath.SignatureBlockerOutsideBHoM, Array.Empty<string>()));

            Assert.Equal(0, result.FailureCount);
            Assert.Single(skips);
            Assert.Equal("Autodesk.Revit.DB.LogicalOrFilter", skips[0].Cause);
            // And the mangled description is replaced by the event's own naming.
            Assert.Equal("BH.Revit.Engine.MechanicalPlumbing.Create.DuctLogicalOrFilter", skips[0].Description);
        }

        [Fact]
        public void MangledDescriptionOnARealFailure_IsRelabelledFromTheMethodEvent()
        {
            var leaf = new FakeTestInfo
            {
                Status = "Error",
                Description = "BH.Revit.Engine.MechanicalPlumbing.Create. }",
                Message = "Error: Returned null from json.",
                Information = [new FakeEventMessage
                {
                    Message = MethodCause.Replace("ApplyDuctInsulation", "SomethingGenuine")
                                         .Replace(".Compute,", ".Create,")
                }]
            };
            var versionSummary = new FakeTestResult { Status = "Error", Information = [leaf] };
            var outer = new FakeTestResult { Status = "Error", Information = [versionSummary] };

            // Probe finds no blocker, so this stays a real failure — but actionable.
            var result = RunCommand.ExtractFilteredResult(outer, _ => true, null, probeSignature: (_, _, _) => (null, ClassificationPath.NoOverloadFound, Array.Empty<string>()));

            Assert.Equal(1, result.FailureCount);
            Assert.Equal("BH.Revit.Engine.MechanicalPlumbing.Create.SomethingGenuine",
                result.Failures[0].Description);
        }

        [Fact]
        public void UnmangledDescriptionAlongsideAMethodEvent_IsStillLeftAlone()
        {
            // Relabelling exists only to repair "<Type>. }". A description that parsed
            // correctly must survive even when an event could supply another name.
            var leaf = new FakeTestInfo
            {
                Status = "Error",
                Description = "BH.oM.Adapters.File.FileSettings",
                Message = "Error: Result returned as CustomObject",
                Information = [new FakeEventMessage { Message = MethodCause }]
            };
            var versionSummary = new FakeTestResult { Status = "Error", Information = [leaf] };
            var outer = new FakeTestResult { Status = "Error", Information = [versionSummary] };

            var result = RunCommand.ExtractFilteredResult(outer, _ => true, null, probeSignature: (_, _, _) => (null, ClassificationPath.NoOverloadFound, Array.Empty<string>()));

            Assert.Equal(1, result.FailureCount);
            Assert.Equal("BH.oM.Adapters.File.FileSettings", result.Failures[0].Description);
        }

        [Fact]
        public void UnmangledDescription_IsLeftAlone()
        {
            var leaf = new FakeTestInfo
            {
                Status = "Error",
                Description = "BH.oM.Adapters.File.FileSettings",
                Message = "Error: Result returned as CustomObject",
                Information = []
            };
            var versionSummary = new FakeTestResult { Status = "Error", Information = [leaf] };
            var outer = new FakeTestResult { Status = "Error", Information = [versionSummary] };

            var result = RunCommand.ExtractFilteredResult(outer, _ => true);

            Assert.Equal(1, result.FailureCount);
            Assert.Equal("BH.oM.Adapters.File.FileSettings", result.Failures[0].Description);
        }

        [Fact]
        public void ExtractFilteredResult_HonoursTheSuppliedPredicate()
        {
            var leafFailure = new FakeTestInfo
            {
                Status = "Error",
                Description = "BH.oM.Adapters.ETABS.Elements.Pier",
                Message = "Error: Result returned as CustomObject"
            };
            var versionSummary = new FakeTestResult { Status = "Error", Information = [leafFailure] };
            var outer = new FakeTestResult { Status = "Error", Information = [versionSummary] };

            var subject = Namespaces("BH.oM.Adapters.File");
            var filtered = RunCommand.ExtractFilteredResult(outer, d => RunCommand.IsFromSubjectNamespace(d, subject));
            Assert.Equal(VersioningStatus.Pass, filtered.Status);
            Assert.Equal(0, filtered.FailureCount);

            var kept = RunCommand.ExtractFilteredResult(outer, _ => true);
            Assert.Equal(VersioningStatus.Error, kept.Status);
            Assert.Equal(1, kept.FailureCount);
        }
    }

    // Diagnostics only. "Real" is reachable by four different routes that imply different
    // fixes, so these assert that the route is recorded, not that any verdict changed.
    public class ClassificationDiagnosticsTests
    {
        private const string RevitCause =
            "Type Autodesk.Revit.DB.Document, RevitAPI, Version=9.0.0.0 failed to deserialise.";

        private const string MethodCause =
            "Method ApplyDuctInsulation from { \"_t\" : \"System.Type\", \"Name\" : \"BH.Revit.Engine.MechanicalPlumbing.Compute, Revit_MechanicalPlumbing_Engine_2022, Version=9.0.0.0, Culture=neutral, PublicKeyToken=null\", \"_bhomVersion\" : \"9.2\" } failed to deserialise.";

        private static FakeTestResult Tree(string description, params string[] eventMessages)
        {
            var leaf = new FakeTestInfo
            {
                Status = "Error",
                Description = description,
                Message = "Error: Returned null from json.",
                Information = eventMessages.Select(m => (object)new FakeEventMessage { Message = m }).ToList()
            };
            var versionSummary = new FakeTestResult { Status = "Error", Information = [leaf] };
            return new FakeTestResult { Status = "Error", Information = [versionSummary] };
        }

        [Fact]
        public void CauseFromEvents_IsRecordedAsUnresolvableFromEvents()
        {
            var diagnostics = new List<FailureDiagnostic>();
            RunCommand.ExtractFilteredResult(Tree("BH.oM.Adapters.File.FileSettings", RevitCause),
                _ => true, null, null, diagnostics);

            var only = Assert.Single(diagnostics);
            Assert.False(only.CountedAsReal);
            Assert.Equal(ClassificationPath.UnresolvableFromEvents, only.Path);
            Assert.Equal("Autodesk.Revit.DB.Document", only.Cause);
        }

        [Fact]
        public void NoMethodEvent_IsRecordedAsSuch_AndCountedReal()
        {
            // This is the path that never reaches the probe at all, so a failure here is
            // real by default rather than by evidence.
            var diagnostics = new List<FailureDiagnostic>();
            var result = RunCommand.ExtractFilteredResult(Tree("BH.oM.Adapters.File.FileSettings"),
                _ => true, null, (_, _, _) => (null, ClassificationPath.NoOverloadFound, Array.Empty<string>()), diagnostics);

            Assert.Equal(1, result.FailureCount);
            var only = Assert.Single(diagnostics);
            Assert.True(only.CountedAsReal);
            Assert.Equal(ClassificationPath.NoMethodEvent, only.Path);
            Assert.Null(only.DeclaringType);
        }

        [Fact]
        public void ProbeVerdict_AndDeclaringAssembly_AreCarriedIntoTheDiagnostic()
        {
            var diagnostics = new List<FailureDiagnostic>();
            RunCommand.ExtractFilteredResult(Tree("BH.Revit.Engine.MechanicalPlumbing.Compute. }", MethodCause),
                _ => true, null, (_, _, _) => (null, ClassificationPath.DeclaringTypeNotLoaded, Array.Empty<string>()), diagnostics);

            var only = Assert.Single(diagnostics);
            Assert.True(only.CountedAsReal);
            Assert.Equal(ClassificationPath.DeclaringTypeNotLoaded, only.Path);
            Assert.Equal("BH.Revit.Engine.MechanicalPlumbing.Compute", only.DeclaringType);
            Assert.Equal("Revit_MechanicalPlumbing_Engine_2022", only.DeclaringAssembly);
        }

        [Fact]
        public void DeclaringAssemblyFromTheEvent_IsHandedToTheProbe()
        {
            // The whole fix depends on the probe knowing which assembly to interrogate, and
            // the only source is the Method event's assembly-qualified "Name".
            string? seen = "not called";
            RunCommand.ExtractFilteredResult(
                Tree("BH.Revit.Engine.MechanicalPlumbing.Compute. }", MethodCause),
                _ => true, null,
                (_, _, asm) => { seen = asm; return (null, ClassificationPath.DeclaringTypeNotLoaded, Array.Empty<string>()); },
                null);

            Assert.Equal("Revit_MechanicalPlumbing_Engine_2022", seen);
        }

        [Fact]
        public void EveryAttributedFailure_GetsExactlyOneDiagnostic()
        {
            var diagnostics = new List<FailureDiagnostic>();
            var skips = new List<RunCommand.UnverifiedFailure>();
            var leafReal = new FakeTestInfo
            {
                Status = "Error", Description = "BH.oM.Adapters.File.A", Message = "m",
                Information = []
            };
            var leafUnverified = new FakeTestInfo
            {
                Status = "Error", Description = "BH.oM.Adapters.File.B", Message = "m",
                Information = [new FakeEventMessage { Message = RevitCause }]
            };
            var versionSummary = new FakeTestResult { Status = "Error", Information = [leafReal, leafUnverified] };
            var outer = new FakeTestResult { Status = "Error", Information = [versionSummary] };

            var result = RunCommand.ExtractFilteredResult(outer, _ => true, skips, null, diagnostics);

            Assert.Equal(1, result.FailureCount);
            Assert.Single(skips);
            Assert.Equal(2, diagnostics.Count);
            Assert.Equal(result.FailureCount, diagnostics.Count(d => d.CountedAsReal));
            Assert.Equal(skips.Count, diagnostics.Count(d => !d.CountedAsReal));
            Assert.Equal(diagnostics, result.Diagnostics);
        }

        [Fact]
        public void ProbePaths_DistinguishNoOverloadFromResolvedSignature()
        {
            Assert.Null(RunCommand.ProbeSignatureBlocker(typeof(string), "NoSuchMethodHere", out var missing));
            Assert.Equal(ClassificationPath.NoOverloadFound, missing);

            Assert.Null(RunCommand.ProbeSignatureBlocker(typeof(string), nameof(string.Substring), out var fine));
            Assert.Equal(ClassificationPath.SignatureResolved, fine);
        }

        [Fact]
        public void AssemblyParser_ReturnsNullWhenThereIsNothingToParse()
        {
            Assert.Equal("Revit_MechanicalPlumbing_Engine_2022", RunCommand.ParseMethodEventAssembly(MethodCause));
            Assert.Null(RunCommand.ParseMethodEventAssembly(RevitCause));
            Assert.Null(RunCommand.ParseMethodEventAssembly(""));
            Assert.Null(RunCommand.ParseMethodEventAssembly("Method with no json payload"));
        }
    }

    // GetType(throwOnError: false) returns null and discards the reason, which made "could
    // not be loaded" indistinguishable from "was deleted" and defaulted both to a real
    // failure. Measured on a live repo: 443 of 443 attributed failures took that route and
    // none were genuine. These cover the resolution logic with real assemblies.
    //
    // The missing-dependency branch cannot be built from BCL types, since every BCL
    // dependency is present. It was validated read-only against mirrored assembly sets with
    // and without the Revit mocks: mocks absent gave FileNotFoundException naming
    // 'RevitAPI, Version=22.0.0.0', which NonBHoMTypeFrom reduces to 'RevitAPI'.
    public class ProbeDeclaringTypeTests
    {
        private static readonly List<Assembly> Loaded = [typeof(string).Assembly];
        private static readonly string CoreLib = typeof(string).Assembly.GetName().Name!;

        [Fact]
        public void TypeInTwoLoadedAssemblies_RecordsBothCandidates()
        {
            // The same assembly listed twice stands in for two repos declaring into one
            // namespace, which is the real shape of CI_Toolkit#161. The probe result must be
            // unchanged and the ambiguity must be visible.
            var twice = new List<Assembly> { typeof(string).Assembly, typeof(string).Assembly };

            var (cause, path, candidates) = RunCommand.ProbeDeclaringType(
                twice, "System.String", nameof(string.Substring), CoreLib);

            Assert.Null(cause);
            Assert.Equal(ClassificationPath.SignatureResolved, path);
            Assert.Equal(2, candidates.Count);
        }

        [Fact]
        public void TypeThatResolves_FallsThroughToTheSignatureProbe()
        {
            var (cause, path, candidates) = RunCommand.ProbeDeclaringType(
                Loaded, "System.String", nameof(string.Substring), CoreLib);

            Assert.Null(cause);
            Assert.Equal(ClassificationPath.SignatureResolved, path);
            // The type resolves in exactly one loaded assembly, so it is unambiguous.
            Assert.Single(candidates);
        }

        [Fact]
        public void TypeAbsentFromItsOwnAssembly_StaysRealAndIsNamedAsSuch()
        {
            // The genuine-removal case, and the one versioning exists to catch. Decided on
            // the exception rather than on the type's name, so a subject namespace outside
            // BH. cannot be classified away as infrastructure.
            var (cause, path, candidates) = RunCommand.ProbeDeclaringType(
                Loaded, "System.NoSuchTypeAnywhere", "Whatever", CoreLib);

            Assert.Null(cause);
            Assert.Equal(ClassificationPath.DeclaringTypeAbsent, path);
        }

        [Fact]
        public void NoDeclaringAssemblyNamed_KeepsThePreviousBehaviour()
        {
            var (cause, path, candidates) = RunCommand.ProbeDeclaringType(
                Loaded, "Nowhere.At.All", "Whatever", null);

            Assert.Null(cause);
            Assert.Equal(ClassificationPath.DeclaringTypeNotLoaded, path);
        }

        [Fact]
        public void DeclaringAssemblyOutsideTheClosure_KeepsThePreviousBehaviour()
        {
            // Nothing to interrogate, so it must fail safe as a real failure rather than be
            // assumed environmental.
            var (cause, path, candidates) = RunCommand.ProbeDeclaringType(
                Loaded, "Nowhere.At.All", "Whatever", "Some_Assembly_Not_Loaded");

            Assert.Null(cause);
            Assert.Equal(ClassificationPath.DeclaringTypeNotLoaded, path);
        }

        // The verdict is keyed on the exception, so each branch is testable directly rather
        // than needing an assembly with a genuinely missing dependency.
        [Fact]
        public void TypeLoadException_MeansTheTypeIsGone_AndStaysReal()
        {
            var (cause, path) = RunCommand.ClassifyDeclaringTypeFailure(
                new TypeLoadException("Could not load type 'BH.Whatever.Query' from assembly 'X'."),
                "BH.Whatever.Query");

            Assert.Null(cause);
            Assert.Equal(ClassificationPath.DeclaringTypeAbsent, path);
        }

        [Fact]
        public void MissingNonBHoMDependency_IsUnverified()
        {
            // Exactly the shape measured against a mirrored assembly set with the Revit mocks
            // removed: the assembly identity is trimmed at the first comma.
            var (cause, path) = RunCommand.ClassifyDeclaringTypeFailure(
                new FileNotFoundException(
                    "Could not load file or assembly 'RevitAPI, Version=22.0.0.0'.",
                    "RevitAPI, Version=22.0.0.0, Culture=neutral, PublicKeyToken=null"),
                "BH.Revit.Engine.Core.Query");

            Assert.Equal("RevitAPI", cause);
            Assert.Equal(ClassificationPath.DeclaringTypeUnloadable, path);
        }

        [Fact]
        public void MissingBHoMDependency_StaysRealRatherThanBeingClassifiedAway()
        {
            // A BHoM assembly missing from the closure is our problem, not the author's, but
            // it must not silently excuse a failure the way a third-party blocker does.
            var (cause, path) = RunCommand.ClassifyDeclaringTypeFailure(
                new FileNotFoundException("Could not load file or assembly.",
                    "BH.Engine.Something, Version=9.0.0.0"), "BH.Revit.Engine.Core.Query");

            Assert.Null(cause);
            Assert.Equal(ClassificationPath.DeclaringTypeAbsent, path);
        }

        [Fact]
        public void BlockerNamingTheRequestedTypeItself_StaysReal_EvenOutsideBH()
        {
            // The runner deliberately allows any namespace so extending orgs can ship their own
            // Verify class, so a subject type need not start with BH. Without the equality
            // guard, such a type's own name reads as a third-party blocker and a genuine
            // removal would be classified away as infrastructure. That is a false pass in the
            // one case this check exists to catch.
            var (cause, path) = RunCommand.ClassifyDeclaringTypeFailure(
                new FileNotFoundException("Could not load file or assembly.", "Acme.Tools.Query"),
                "Acme.Tools.Query");

            Assert.Null(cause);
            Assert.Equal(ClassificationPath.DeclaringTypeAbsent, path);
        }

        [Fact]
        public void UnnamedFailure_StaysReal()
        {
            var (cause, path) = RunCommand.ClassifyDeclaringTypeFailure(new InvalidOperationException("nope"), "BH.Whatever.Query");

            Assert.Null(cause);
            Assert.Equal(ClassificationPath.DeclaringTypeAbsent, path);
        }
    }

    // The check's verdict and its exit code. ci-versioning reads the exit code through
    // $LASTEXITCODE and branches its job summary on it, and the Warning/exit-0 versus
    // Error/exit-1 split is relied on downstream, so both are pinned here. Neither had
    // any coverage before: the logic lived inline in Execute, which no test reaches.
    public class StatusAndExitCodeTests
    {
        [Fact]
        public void NoFailuresAndNothingUnverified_IsPass()
            => Assert.Equal(VersioningStatus.Pass, RunCommand.DeriveStatus(0, 0));

        [Fact]
        public void NothingFailedButSomethingWasUnverifiable_IsWarningNotPass()
            => Assert.Equal(VersioningStatus.Warning, RunCommand.DeriveStatus(0, 1));

        [Fact]
        public void AnyRealFailure_IsError()
            => Assert.Equal(VersioningStatus.Error, RunCommand.DeriveStatus(1, 0));

        [Fact]
        public void RealFailuresOutrankUnverified()
            => Assert.Equal(VersioningStatus.Error, RunCommand.DeriveStatus(1, 99));

        [Fact]
        public void OnlyErrorFailsTheJob()
        {
            Assert.Equal(0, RunCommand.ExitCodeFor(VersioningStatus.Pass));
            Assert.Equal(0, RunCommand.ExitCodeFor(VersioningStatus.Warning));
            Assert.Equal(1, RunCommand.ExitCodeFor(VersioningStatus.Error));
        }

        // The two observed sandbox legs of the CI_Toolkit#159 A/B, which straddle the
        // boundary: leg A 0 failures with 805 unverified, leg B 2 real failures.
        [Theory]
        [InlineData(0, 805, VersioningStatus.Warning, 0)]
        [InlineData(2, 0, VersioningStatus.Error, 1)]
        public void ObservedRuns_ReproduceTheirRecordedVerdict(
            int failures, int unverified, VersioningStatus expected, int expectedExit)
        {
            var status = RunCommand.DeriveStatus(failures, unverified);

            Assert.Equal(expected, status);
            Assert.Equal(expectedExit, RunCommand.ExitCodeFor(status));
        }
    }

    namespace Fixtures
    {
        internal class FakeTestResult
        {
            public string Status { get; set; } = "Pass";
            public List<object> Information { get; set; } = [];
        }

        internal class FakeTestInfo
        {
            public string Status { get; set; } = "Error";
            public string Description { get; set; } = "";
            public string Message { get; set; } = "";
            public List<object> Information { get; set; } = [];
        }

        // Mirrors BH.oM.Test.Results.EventMessage exactly as reflection reports it:
        // Message, Status, UTCTime, StackTrace — and no Information. It does carry a
        // Status, so a discriminator based on Status alone treats it as a nested
        // result and walks past the real failure above it.
        internal class FakeEventMessage
        {
            public string Message { get; set; } = "";
            public string Status { get; set; } = "Error";
            public DateTime UTCTime { get; set; }
            public string StackTrace { get; set; } = "";
        }
    }
}
