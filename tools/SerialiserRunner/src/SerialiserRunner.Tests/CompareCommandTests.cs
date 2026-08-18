using System.Text.Json;
using System.Text.Json.Serialization;
using SerialiserRunner.Commands;
using SerialiserRunner.Models;
using Xunit;

namespace SerialiserRunner.Tests
{
    public class SerialiserResultTests
    {
        [Fact]
        public void SerialiserResult_RoundTrips_Via_Json()
        {
            var result = new SerialiserResult
            {
                Status = TestStatus.Error,
                FailureCount = 2,
                Failures =
                [
                    new FailureInfo { Description = "BH.oM.Foo.Bar", Message = "Error: not equal after round-trip." },
                    new FailureInfo { Description = "BH.oM.Foo.Baz", Message = "Error: failed to convert to json." }
                ]
            };

            string json = JsonSerializer.Serialize(result);
            SerialiserResult? deserialized = JsonSerializer.Deserialize<SerialiserResult>(json);

            Assert.NotNull(deserialized);
            Assert.Equal(TestStatus.Error, deserialized.Status);
            Assert.Equal(2, deserialized.FailureCount);
            Assert.Equal("BH.oM.Foo.Bar", deserialized.Failures[0].Description);
        }

        // The loaded set only earns its place if it survives to the artifact, which is
        // where a branch-vs-baseline or a-vs-BHoMBot diff would read it from.
        [Fact]
        public void LoadedAssemblies_SurviveTheJsonRoundTrip()
        {
            var result = new SerialiserResult
            {
                Status = TestStatus.Pass,
                LoadedAssemblies = ["BHoM.dll", "Geometry_oM.dll"]
            };

            SerialiserResult? deserialized =
                JsonSerializer.Deserialize<SerialiserResult>(JsonSerializer.Serialize(result));

            Assert.NotNull(deserialized);
            Assert.Equal(["BHoM.dll", "Geometry_oM.dll"], deserialized.LoadedAssemblies);
        }

        // A result written before this field existed must still deserialise, otherwise a
        // cached or in-flight baseline artifact would break CompareCommand on rollout.
        // Options mirror Program.cs:34, so this exercises the real read path.
        [Fact]
        public void ResultWithoutLoadedAssemblies_DeserialisesToEmpty()
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new JsonStringEnumConverter() }
            };

            SerialiserResult? deserialized = JsonSerializer.Deserialize<SerialiserResult>(
                """{"Status":"Pass","FailureCount":0,"Population":10,"Failures":[]}""", options);

            Assert.NotNull(deserialized);
            Assert.Empty(deserialized.LoadedAssemblies);
        }

        // Same contract as LoadedAssemblies above: the breakdown is only useful if it reaches
        // the artifact, which is where a branch-vs-baseline population diff reads it from.
        [Fact]
        public void Legs_SurviveTheJsonRoundTrip()
        {
            var result = new SerialiserResult
            {
                Status = TestStatus.Warning,
                Population = 7097,
                Legs =
                [
                    new() { Name = "MethodsToFromJson", Status = "Pass",    Population = 4505 },
                    new() { Name = "ObjectsToFromJson", Status = "Warning", Population = 1069, FailureCount = 1 },
                    new() { Name = "TypesToFromJson",   Status = "Pass",    Population = 1523 }
                ]
            };

            SerialiserResult? deserialized =
                JsonSerializer.Deserialize<SerialiserResult>(JsonSerializer.Serialize(result));

            Assert.NotNull(deserialized);
            Assert.Equal(3, deserialized.Legs.Count);
            Assert.Equal("ObjectsToFromJson", deserialized.Legs[1].Name);
            Assert.Equal(1069, deserialized.Legs[1].Population);
            Assert.Equal(1, deserialized.Legs[1].FailureCount);
        }

        // A baseline artifact written before Legs existed must still deserialise, or the first
        // comparison after rollout breaks against a cached or in-flight baseline.
        [Fact]
        public void ResultWithoutLegs_DeserialisesToEmpty()
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new JsonStringEnumConverter() }
            };

            SerialiserResult? deserialized = JsonSerializer.Deserialize<SerialiserResult>(
                """{"Status":"Pass","FailureCount":0,"Population":10,"Failures":[],"LoadedAssemblies":[]}""",
                options);

            Assert.NotNull(deserialized);
            Assert.Empty(deserialized.Legs);
        }

    }

    // ReadLeg is the seam that makes the breakdown verifiable without real BHoM assemblies.
    // Fakes are duck-typed to the shape the runner reflects over, matching NestedMessagesTests.
    public class ReadLegTests
    {
        private sealed class Event { public string Message { get; init; } = ""; }

        private sealed class Item
        {
            public string Description { get; init; } = "";
            public string Message { get; init; } = "";
            public List<Event> Information { get; init; } = [];
        }

        private sealed class Summary
        {
            public string Status { get; init; } = "";
            public string Description { get; init; } = "";
            public List<Item> Information { get; init; } = [];
        }

        // A summary with no Information at all, to prove a failing leg is still recorded.
        private sealed class SummaryWithoutInformation
        {
            public string Status { get; init; } = "";
            public string Description { get; init; } = "";
        }

        [Fact]
        public void PassingLeg_CarriesItsPopulationAndNoFailures()
        {
            var (leg, failures) = RunCommand.ReadLeg("MethodsToFromJson", new Summary
            {
                Status = "Pass",
                Description = "Serialisation of Methods via json: 4505 methods available."
            });

            Assert.Equal("MethodsToFromJson", leg.Name);
            Assert.Equal("Pass", leg.Status);
            Assert.Equal(4505, leg.Population);
            Assert.Equal(0, leg.FailureCount);
            Assert.Empty(failures);
        }

        [Fact]
        public void FailingLeg_CountsOnlyItsOwnFailures_AndKeepsTheEvents()
        {
            var (leg, failures) = RunCommand.ReadLeg("ObjectsToFromJson", new Summary
            {
                Status = "Warning",
                Description = "Serialisation of Objects via json: 1069 types of objects available in BH.oM.",
                Information =
                [
                    new() { Description = "BH.oM.Forms.WindowLayoutSettings", Message = "dummy failed",
                            Information = [new() { Message = "inner cause" }] }
                ]
            });

            Assert.Equal(1069, leg.Population);
            Assert.Equal(1, leg.FailureCount);
            Assert.Equal(leg.FailureCount, failures.Count);
            Assert.Equal("BH.oM.Forms.WindowLayoutSettings", failures[0].Description);
            Assert.Equal(["inner cause"], failures[0].Events);
        }

        // The population is the reason the breakdown exists, so a failing leg that reports no
        // Information must still contribute its count rather than vanishing from the list.
        [Fact]
        public void FailingLegWithoutInformation_StillCarriesItsPopulation()
        {
            var (leg, failures) = RunCommand.ReadLeg("TypesToFromJson", new SummaryWithoutInformation
            {
                Status = "Error",
                Description = "Serialisation of Types via json: 1523 types available."
            });

            Assert.Equal("TypesToFromJson", leg.Name);
            Assert.Equal("Error", leg.Status);
            Assert.Equal(1523, leg.Population);
            Assert.Equal(0, leg.FailureCount);
            Assert.Empty(failures);
        }

        // An unparseable summary yields 0, matching ParsePopulation's fail-open contract, so a
        // leg is still listed and the total is not inflated by a guess.
        [Fact]
        public void UnreadableSummary_YieldsZeroPopulationAndStillRecordsTheLeg()
        {
            var (leg, _) = RunCommand.ReadLeg("MethodsToFromJson", new Summary
            {
                Status = "Pass",
                Description = "no count in here"
            });

            Assert.Equal(0, leg.Population);
            Assert.Equal("MethodsToFromJson", leg.Name);
        }
    }

    public class CompareCommandTests
    {
        private static SerialiserResult MakePass() =>
            new() { Status = TestStatus.Pass, FailureCount = 0, Failures = [] };

        private static SerialiserResult MakeWarning() =>
            new() { Status = TestStatus.Warning, FailureCount = 1,
                    Failures = [new() { Description = "Foo", Message = "w" }] };

        private static SerialiserResult MakeErrors(params string[] descriptions) =>
            new()
            {
                Status = TestStatus.Error,
                FailureCount = descriptions.Length,
                Failures = descriptions.Select(d => new FailureInfo { Description = d, Message = "err" }).ToList()
            };

        private static SerialiserResult MakeErrors(params FailureInfo[] failures) =>
            new()
            {
                Status = TestStatus.Error,
                FailureCount = failures.Length,
                Failures = [..failures]
            };

        [Fact]
        public void Branch_Pass_IsNotRegression() =>
            Assert.False(CompareCommand.Compare(MakeErrors("A", "B"), MakePass()).IsRegression);

        [Fact]
        public void Branch_Warning_IsNotRegression() =>
            Assert.False(CompareCommand.Compare(MakeErrors("A"), MakeWarning()).IsRegression);

        [Fact]
        public void Branch_SameErrors_AsBaseline_IsNotRegression() =>
            Assert.False(CompareCommand.Compare(MakeErrors("A", "B"), MakeErrors("A", "B")).IsRegression);

        [Fact]
        public void Branch_FewerErrors_IsNotRegression() =>
            Assert.False(CompareCommand.Compare(MakeErrors("A", "B", "C"), MakeErrors("A")).IsRegression);

        [Fact]
        public void Branch_MoreErrors_IsRegression() =>
            Assert.True(CompareCommand.Compare(MakeErrors("A"), MakeErrors("A", "B")).IsRegression);

        [Fact]
        public void Branch_DifferentErrors_SameCount_IsRegression() =>
            Assert.True(CompareCommand.Compare(MakeErrors("A", "B"), MakeErrors("A", "C")).IsRegression);

        [Fact]
        public void Branch_Error_BaselineWasClean_IsRegression() =>
            Assert.True(CompareCommand.Compare(MakePass(), MakeErrors("A")).IsRegression);

        [Fact]
        public void Regression_Summary_Lists_NewFailure()
        {
            var result = CompareCommand.Compare(MakeErrors("A"), MakeErrors("A", "NewType"));
            Assert.Contains("NewType", result.Summary);
        }

        [Fact]
        public void Compare_NewCascadeFailure_SummaryIncludesCascadeAnnotation()
        {
            var branch = MakeErrors(new FailureInfo
            {
                Description = "BH.oM.CFD.Streamer",
                Message = "err",
                IsPotentialCascade = true,
                SuspectedRootCauses = ["BH.oM.CFD.Node"]
            });
            var result = CompareCommand.Compare(MakePass(), branch);
            Assert.Contains("possible cascade from", result.Summary);
            Assert.Contains("BH.oM.CFD.Node", result.Summary);
        }

        [Fact]
        public void Compare_NewCascadeFailure_IsStillRegression()
        {
            var branch = MakeErrors(new FailureInfo
            {
                Description = "BH.oM.CFD.Streamer",
                Message = "err",
                IsPotentialCascade = true,
                SuspectedRootCauses = ["BH.oM.CFD.Node"]
            });
            Assert.True(CompareCommand.Compare(MakePass(), branch).IsRegression);
        }
    }

    // Migration-parity suite for R1: proves Compare reproduces BHoMBot's count-aware
    // InformationIsEqual / InformationIsLessAndBetter semantics (List, not Set).
    public class LegacyCountParityTests
    {
        private static SerialiserResult Errors(params string[] descs) => new()
        {
            Status = TestStatus.Error,
            FailureCount = descs.Length,
            Failures = descs.Select(d => new FailureInfo { Description = d, Message = "err" }).ToList()
        };

        [Fact] // [X] vs [X] — equal count, present on baseline => not a regression
        public void X_vs_X_IsNotRegression() =>
            Assert.False(CompareCommand.Compare(Errors("X"), Errors("X")).IsRegression);

        [Fact] // [X] vs [X,X] — count rose 1->2 (same type, extra dimension) => regression
        public void X_vs_XX_IsRegression() =>
            Assert.True(CompareCommand.Compare(Errors("X"), Errors("X", "X")).IsRegression);

        [Fact] // [X] vs [X,Y] — new failing type => regression
        public void X_vs_XY_IsRegression() =>
            Assert.True(CompareCommand.Compare(Errors("X"), Errors("X", "Y")).IsRegression);

        [Fact] // [X,Y] vs [X] — fewer, all present => improvement, not a regression
        public void XY_vs_X_IsNotRegression() =>
            Assert.False(CompareCommand.Compare(Errors("X", "Y"), Errors("X")).IsRegression);

        [Fact] // [X,Y] vs [X,Z] — same count, Z not on baseline => regression
        public void XY_vs_XZ_SameCount_IsRegression() =>
            Assert.True(CompareCommand.Compare(Errors("X", "Y"), Errors("X", "Z")).IsRegression);

        [Fact] // [X,X] vs [X] — baseline holds the duplicate, branch fewer => improvement
        public void XX_vs_X_IsNotRegression() =>
            Assert.False(CompareCommand.Compare(Errors("X", "X"), Errors("X")).IsRegression);

        [Fact] // count-increase regression reported as such (not a misleading "0 new failures")
        public void X_vs_XX_Summary_MentionsCountIncrease()
        {
            var r = CompareCommand.Compare(Errors("X"), Errors("X", "X"));
            Assert.True(r.IsRegression);
            Assert.Contains("count increased", r.Summary);
        }

        [Fact] // Pass branch is never a regression regardless of baseline
        public void BranchPass_IsNotRegression() =>
            Assert.False(CompareCommand.Compare(Errors("A", "B"),
                new SerialiserResult { Status = TestStatus.Pass }).IsRegression);
    }

    // Implausible-baseline guard: a baseline failing at or above ImplausibleBaselineRatio
    // of its population cannot establish what the branch changed, so no diff is attempted.
    public class ImplausibleBaselineTests
    {
        private static SerialiserResult Baseline(int failures, int population) => new()
        {
            Status = TestStatus.Error,
            FailureCount = failures,
            Population = population,
            Failures = Enumerable.Range(0, failures)
                .Select(i => new FailureInfo { Description = $"T{i}", Message = "err" }).ToList()
        };

        private static SerialiserResult Branch(params string[] descs) => new()
        {
            Status = TestStatus.Error,
            FailureCount = descs.Length,
            Failures = descs.Select(d => new FailureInfo { Description = d, Message = "err" }).ToList()
        };

        [Fact] // observed shape on a private Revit tool repo: 5981 of 6024
        public void BaselineAtObservedFleetRate_IsUnusable_AndNotARegression()
        {
            var r = CompareCommand.Compare(Baseline(5981, 6024), Branch("NewMethod"));
            Assert.True(r.IsBaselineUnusable);
            Assert.False(r.IsRegression);
            Assert.Contains("Baseline unusable", r.Summary);
        }

        [Fact] // exactly at the threshold trips it
        public void BaselineExactlyAtThreshold_IsUnusable() =>
            Assert.True(CompareCommand.Compare(Baseline(50, 100), Branch("New")).IsBaselineUnusable);

        [Fact] // just under the threshold behaves exactly as before
        public void BaselineBelowThreshold_StillDiffsNormally()
        {
            var r = CompareCommand.Compare(Baseline(49, 100), Branch("T0", "New"));
            Assert.False(r.IsBaselineUnusable);
            Assert.True(r.IsRegression);
        }

        [Fact] // unknown population must not fabricate a denominator
        public void UnknownPopulation_SkipsGuard()
        {
            var r = CompareCommand.Compare(Baseline(5981, 0), Branch("New"));
            Assert.False(r.IsBaselineUnusable);
            Assert.True(r.IsRegression);
        }

        [Fact] // a healthy branch short-circuits before the guard is consulted
        public void BranchPass_ShortCircuitsBeforeGuard()
        {
            var r = CompareCommand.Compare(Baseline(5981, 6024), new SerialiserResult { Status = TestStatus.Pass });
            Assert.False(r.IsBaselineUnusable);
            Assert.False(r.IsRegression);
        }

        [Fact] // the policy value is the documented default when callers do not override
        public void DefaultRatio_IsThePolicyValue() =>
            Assert.Equal(0.5, CompareCommand.DefaultImplausibleBaselineRatio);

        [Fact] // a per-repo override raises the bar without touching CompareCommand
        public void RatioOverride_RaisesTheBar()
        {
            var baseline = Baseline(60, 100);
            Assert.True(CompareCommand.Compare(baseline, Branch("New"), 0.5).IsBaselineUnusable);
            Assert.False(CompareCommand.Compare(baseline, Branch("New"), 0.9).IsBaselineUnusable);
        }
    }

    public class AnnotateCascadesTests
    {
        private static string Fqn<T>() => typeof(T).FullName!;

        [Fact]
        public void AnnotateCascades_ListProperty_MarksAsCascade()
        {
            var failures = new List<FailureInfo>
            {
                new() { Description = Fqn<Fixtures.FixtureNode>(), Message = "err" },
                new() { Description = Fqn<Fixtures.FixtureStreamer>(), Message = "err" }
            };

            var annotated = RunCommand.AnnotateCascades(failures);

            var streamer = annotated.First(f => f.Description == Fqn<Fixtures.FixtureStreamer>());
            Assert.True(streamer.IsPotentialCascade);
            Assert.Contains(Fqn<Fixtures.FixtureNode>(), streamer.SuspectedRootCauses);
        }

        [Fact]
        public void AnnotateCascades_DirectProperty_MarksAsCascade()
        {
            var failures = new List<FailureInfo>
            {
                new() { Description = Fqn<Fixtures.FixtureNode>(), Message = "err" },
                new() { Description = Fqn<Fixtures.FixtureStreamerDirect>(), Message = "err" }
            };

            var annotated = RunCommand.AnnotateCascades(failures);

            var streamer = annotated.First(f => f.Description == Fqn<Fixtures.FixtureStreamerDirect>());
            Assert.True(streamer.IsPotentialCascade);
            Assert.Contains(Fqn<Fixtures.FixtureNode>(), streamer.SuspectedRootCauses);
        }

        [Fact]
        public void AnnotateCascades_NoMatchingPropertyType_DoesNotMark()
        {
            var failures = new List<FailureInfo>
            {
                new() { Description = Fqn<Fixtures.FixtureUnrelated>(), Message = "err" }
            };

            var annotated = RunCommand.AnnotateCascades(failures);

            Assert.False(annotated[0].IsPotentialCascade);
            Assert.Empty(annotated[0].SuspectedRootCauses);
        }

        [Fact]
        public void AnnotateCascades_DoesNotMarkRootCauseTypeAsCascade()
        {
            var failures = new List<FailureInfo>
            {
                new() { Description = Fqn<Fixtures.FixtureNode>(), Message = "err" },
                new() { Description = Fqn<Fixtures.FixtureStreamer>(), Message = "err" }
            };

            var annotated = RunCommand.AnnotateCascades(failures);

            var node = annotated.First(f => f.Description == Fqn<Fixtures.FixtureNode>());
            Assert.False(node.IsPotentialCascade);
        }
    }
}

namespace SerialiserRunner.Tests.Fixtures
{
    internal class FixtureNode { }
    internal class FixtureStreamer { public List<FixtureNode> Nodes { get; set; } = []; }
    internal class FixtureStreamerDirect { public FixtureNode Node { get; set; } = new(); }
    internal class FixtureUnrelated { public string Name { get; set; } = ""; }
}
