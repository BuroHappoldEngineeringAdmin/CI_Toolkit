using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using SerialiserRunner.Commands;
using SerialiserRunner.Models;
using Xunit;

namespace SerialiserRunner.Tests
{
    public class NoVerifyMethodsTests
    {
        [Fact]
        public void Execute_NoVerifyMethods_ReturnsOneAndWritesErrorJson()
        {
            var tempDir = Directory.CreateTempSubdirectory("srtest_").FullName;
            var outputFile = Path.Combine(tempDir, "result.json");
            try
            {
                int exitCode = RunCommand.Execute(tempDir, outputFile);

                Assert.Equal(1, exitCode);
                Assert.True(File.Exists(outputFile));

                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    Converters = { new JsonStringEnumConverter() }
                };
                var result = JsonSerializer.Deserialize<SerialiserResult>(
                    File.ReadAllText(outputFile), options);

                Assert.NotNull(result);
                Assert.Equal(TestStatus.Error, result!.Status);
                Assert.Equal(1, result.FailureCount);
                Assert.Single(result.Failures);
                Assert.Equal("Configuration", result.Failures[0].Description);

                // The breakdown has to reach the artifact through Execute's own writer options,
                // not just through the default ones the round-trip test uses. A configuration
                // error runs no legs, so the list is empty but must still be present: an omitted
                // property would leave a consumer unable to tell "no legs ran" from "old schema".
                Assert.Empty(result.Legs);
                Assert.Contains("\"Legs\"", File.ReadAllText(outputFile));
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
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
        // Legacy parity: BHoMBot's SerialiserProcess.LoadAssemblies only loaded the
        // four suffixes below. Revit year-versioned DLLs match none of them, so they
        // are NOT loadable — matching legacy behaviour (the Revit expansion is removed).
        [InlineData(@"C:\assemblies\Revit_Toolkit2024.dll")]
        [InlineData(@"C:\assemblies\Revit_Toolkit2023.dll")]
        [InlineData(@"C:\assemblies\BH_Revit_Toolkit2025.dll")]
        public void RevitVersionedAssembly_ReturnsFalse(string path)
        {
            Assert.False(RunCommand.IsLoadableAssembly(path));
        }

        [Theory]
        [InlineData(@"C:\assemblies\SomeLibrary.dll")]
        [InlineData(@"C:\assemblies\BH.oM.Base.pdb")]
        [InlineData(@"C:\assemblies\BH.oM.Base.xml")]
        [InlineData(@"C:\assemblies\Revit2024.dll")]
        [InlineData(@"C:\assemblies\BuroHappold_Revit2023.dll")]
        public void NonBHoMFiles_ReturnFalse(string path)
        {
            Assert.False(RunCommand.IsLoadableAssembly(path));
        }
    }

    public class SafeCandidateTypesTests
    {
        // Reproduces the production crash: a failing type declared a property whose type
        // (e.g. System.Drawing.Bitmap, resolved against the .NET Framework System.Drawing
        // facade) cannot be loaded by the runner, so reading PropertyType throws a
        // TypeLoadException. Cascade annotation is best-effort enrichment and must never
        // abort the run, so such a property is skipped rather than crashing the process.
        [Fact]
        public void SafeCandidateTypes_WhenPropertyTypeCannotBeResolved_ReturnsEmptyAndDoesNotThrow()
        {
            var property = new ThrowingPropertyInfo();

            IReadOnlyList<Type> result = RunCommand.SafeCandidateTypes(property);

            Assert.Empty(result);
        }

        [Fact]
        public void SafeCandidateTypes_ForResolvableProperty_IncludesThePropertyType()
        {
            PropertyInfo property = typeof(Holder).GetProperty(nameof(Holder.Value))!;

            IReadOnlyList<Type> result = RunCommand.SafeCandidateTypes(property);

            Assert.Contains(typeof(string), result);
        }

        private sealed class Holder
        {
            public string Value { get; set; } = "";
        }

        // Minimal PropertyInfo whose PropertyType getter throws, standing in for a
        // property whose declared type lives in an unresolvable assembly.
        private sealed class ThrowingPropertyInfo : PropertyInfo
        {
            public override Type PropertyType => throw new TypeLoadException(
                "Could not load type 'System.Drawing.Bitmap' from assembly 'System.Drawing, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a'.");

            public override string Name => "Icon";
            public override Type? DeclaringType => typeof(Holder);
            public override Type? ReflectedType => typeof(Holder);
            public override PropertyAttributes Attributes => PropertyAttributes.None;
            public override bool CanRead => true;
            public override bool CanWrite => false;
            public override MethodInfo[] GetAccessors(bool nonPublic) => throw new NotImplementedException();
            public override MethodInfo? GetGetMethod(bool nonPublic) => throw new NotImplementedException();
            public override MethodInfo? GetSetMethod(bool nonPublic) => throw new NotImplementedException();
            public override ParameterInfo[] GetIndexParameters() => throw new NotImplementedException();
            public override object? GetValue(object? obj, BindingFlags invokeAttr, Binder? binder, object?[]? index, CultureInfo? culture) => throw new NotImplementedException();
            public override void SetValue(object? obj, object? value, BindingFlags invokeAttr, Binder? binder, object?[]? index, CultureInfo? culture) => throw new NotImplementedException();
            public override object[] GetCustomAttributes(bool inherit) => throw new NotImplementedException();
            public override object[] GetCustomAttributes(Type attributeType, bool inherit) => throw new NotImplementedException();
            public override bool IsDefined(Type attributeType, bool inherit) => throw new NotImplementedException();
        }
    }

    // Population is read out of the Verify summary Description prose because the count is
    // not exposed structurally. The implausible-baseline guard depends on it.
    public class ParsePopulationTests
    {
        [Theory]
        [InlineData("Serialisation of Methods via json: 4500 methods available.", 4500)]
        [InlineData("Serialisation of Types via json: 1524 types available.", 1524)]
        [InlineData("Serialisation of Objects via json: 1113 types of objects available in BH.oM.", 1113)]
        public void RealSummaryStrings_YieldTheirCount(string description, int expected) =>
            Assert.Equal(expected, RunCommand.ParsePopulation(description));

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("Serialisation of Types via json: no count here.")]
        public void UnreadableSummary_YieldsZeroSoTheGuardFailsOpen(string? description) =>
            Assert.Equal(0, RunCommand.ParsePopulation(description));
    }

    // The nested Information on each per-item TestResult is the only place the underlying
    // serialiser exception text exists.
    public class NestedMessagesTests
    {
        private sealed class Event { public string Message { get; init; } = ""; }
        private sealed class ItemWithEvents { public List<Event> Information { get; init; } = []; }
        private sealed class ItemWithoutInformation { public string Description { get; init; } = ""; }

        [Fact]
        public void ExtractsEachEventMessage()
        {
            var item = new ItemWithEvents { Information = [new() { Message = "boom" }, new() { Message = "bang" }] };
            Assert.Equal(["boom", "bang"], RunCommand.NestedMessages(item));
        }

        [Fact]
        public void SkipsBlankMessages()
        {
            var item = new ItemWithEvents { Information = [new() { Message = "  " }, new() { Message = "real" }] };
            Assert.Equal(["real"], RunCommand.NestedMessages(item));
        }

        [Fact]
        public void ShapeWithoutInformation_ReturnsEmptyAndDoesNotThrow() =>
            Assert.Empty(RunCommand.NestedMessages(new ItemWithoutInformation()));
    }

    public class BuildResultTests
    {
        // These lock in the invariant that a thrown Verify method costs one method's coverage
        // rather than the whole run's. No unit test could catch the old behaviour, because the
        // discard happened inside a private method that needs real assemblies on disk, which is
        // part of why it went unnoticed: it was only visible as a drop from 5981 reported
        // failures to 1 on an otherwise identical run.

        private static List<FailureInfo> TwoRealFailures() =>
        [
            new() { Description = "BH.oM.Geometry.Point", Message = "failed", Events = ["inner cause"] },
            new() { Description = "BH.oM.Geometry.Line",  Message = "failed" }
        ];

        [Fact]
        public void Throw_KeepsFailuresFromTheMethodsThatDidRun()
        {
            var failures = TwoRealFailures();
            failures.Add(new FailureInfo { Description = "Configuration", Message = "Verify method 'X' threw: boom" });

            var result = RunCommand.BuildResult(["Error"], failures, 6024, threw: true);

            Assert.Equal(3, result.FailureCount);
            Assert.Contains(result.Failures, f => f.Description == "BH.oM.Geometry.Point");
            Assert.Contains(result.Failures, f => f.Description == "Configuration");
        }

        [Fact]
        public void Throw_KeepsTheEventsPayloadThatFeedsDiagnosis()
        {
            var result = RunCommand.BuildResult(["Error"], TwoRealFailures(), 10, threw: true);

            var point = Assert.Single(result.Failures, f => f.Description == "BH.oM.Geometry.Point");
            Assert.Equal(["inner cause"], point.Events);
        }

        [Fact]
        public void Throw_KeepsPopulation_SoTheImplausibleBaselineGuardStillHasADenominator()
        {
            // Population 0 makes CompareCommand's guard fail open, so losing it silently
            // disables the guard as well as the Events payload.
            var result = RunCommand.BuildResult(["Error"], TwoRealFailures(), 6024, threw: true);

            Assert.Equal(6024, result.Population);
        }

        [Fact]
        public void Throw_ForcesError_EvenWhenEverySurvivingMethodPassed()
        {
            // Otherwise action.yml's Pass/Warning short-circuit skips the comparison entirely
            // and the job goes green over a Verify method that never executed.
            var result = RunCommand.BuildResult(["Pass"], [], 100, threw: true);

            Assert.Equal(TestStatus.Error, result.Status);
        }

        [Theory]
        [InlineData("Pass", TestStatus.Pass)]
        [InlineData("Warning", TestStatus.Warning)]
        [InlineData("Error", TestStatus.Error)]
        public void NoThrow_AggregatesStatusUnchanged(string reported, TestStatus expected) =>
            Assert.Equal(expected, RunCommand.BuildResult([reported], [], 1, threw: false).Status);

        [Fact]
        public void NoThrow_ErrorWinsOverWarning() =>
            Assert.Equal(TestStatus.Error,
                RunCommand.BuildResult(["Warning", "Error", "Pass"], [], 1, threw: false).Status);
    }
}
