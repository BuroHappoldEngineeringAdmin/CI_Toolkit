using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SerialiserRunner.Models;

namespace SerialiserRunner.Commands;

public static class RunCommand
{
    private static readonly string[] _assemblyFileSuffixes =
    [
        "oM.dll",
        "_Engine.dll",
        "_Adapter.dll",
        "_Test.dll"
    ];

    public static int Execute(string assembliesPath, string outputPath)
    {
        var loadedAssemblies = LoadAssemblies(assembliesPath);

        var (result, isConfigError) = InvokeTests(assembliesPath);
        result = result with { LoadedAssemblies = loadedAssemblies };

        string json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } });
        File.WriteAllText(outputPath, json);

        Console.WriteLine($"Status:   {result.Status}");
        Console.WriteLine($"Failures: {result.FailureCount}");
        Console.WriteLine($"Population: {result.Population}");
        foreach (LegResult leg in result.Legs)
            Console.WriteLine($"  {leg.Name}: {leg.Status}, population {leg.Population}, {leg.FailureCount} failure(s)");

        return isConfigError ? 1 : 0;
    }

    private static List<string> LoadAssemblies(string folder)
    {
        var loaded = new List<string>();
        int failed = 0;

        foreach (string file in Directory.GetFiles(folder))
        {
            if (!IsLoadableAssembly(file))
                continue;

            try
            {
                Assembly.LoadFrom(file);
                loaded.Add(Path.GetFileName(file));
            }
            catch (Exception ex)
            {
                failed++;
                Console.Error.WriteLine($"Warning: could not load {file}: {ex.Message}");
            }
        }

        loaded.Sort(StringComparer.OrdinalIgnoreCase);
        Console.WriteLine($"Assemblies: {loaded.Count} loaded, {failed} failed, from {folder}");
        return loaded;
    }

    public static bool IsLoadableAssembly(string filePath)
    {
        string fileName = Path.GetFileName(filePath);
        return _assemblyFileSuffixes.Any(s => fileName.EndsWith(s, StringComparison.OrdinalIgnoreCase));
    }

    private static (SerialiserResult result, bool isConfigError) InvokeTests(string assembliesPath)
    {
        var testMethods = new List<MethodInfo>();
        foreach (string file in Directory.GetFiles(assembliesPath))
        {
            if (!file.EndsWith("_Test.dll", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var asm = Assembly.LoadFrom(file);

                // Keep whatever did load. A single unloadable type used to take the entire
                // assembly with it, discarding every Verify method it declared and surfacing
                // as the "No Verify methods found" error below, which points at an unbuilt
                // Verification.sln and would misdirect the reader. Matches VersioningRunner.
                Type?[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types; }

                foreach (var type in types
                    .Where(t => t?.Name == "Verify"
                             && t.Namespace?.StartsWith("BH.Test.Serialiser", StringComparison.Ordinal) == true))
                {
                    testMethods.AddRange(type!
                        .GetMethods(BindingFlags.Static | BindingFlags.Public)
                        .Where(m => m.ReturnType.FullName == "BH.oM.Test.Results.TestResult"
                                 && m.GetParameters().Length == 0));
                }
            }
            catch (Exception ex) { Console.Error.WriteLine($"Warning: could not inspect {file}: {ex.Message}"); }
        }

        if (testMethods.Count == 0)
        {
            Console.Error.WriteLine(
                "::error title=Serialisation::No Verify methods found across loaded assemblies. " +
                "Ensure BHoM_Engine/.ci/code/Verification.sln was built and that the declaring " +
                "class is named 'Verify' in the 'BH.Test.Serialiser' namespace (or a sub-namespace).");
            return (new SerialiserResult
            {
                Status = TestStatus.Error,
                FailureCount = 1,
                Failures = [new() { Description = "Configuration", Message = "No Verify methods found." }]
            }, true);
        }

        var failures = new List<FailureInfo>();
        var statuses = new List<string>();
        var legs = new List<LegResult>();
        int population = 0;
        bool threw = false;

        foreach (var method in testMethods)
        {
            object? testResult;
            try
            {
                testResult = method.Invoke(null, null);
            }
            catch (Exception ex)
            {
                // A Verify method that throws contributes zero failures, so the run is
                // incomplete and must fail. BHoMBot swallows this silently
                // (SerialiserProcess/Program.cs HandleFile) and that is exactly how 1113
                // object types went unverified under a green check.
                //
                // Record it and carry on rather than returning here. Returning discarded
                // every failure, Event and population count the other Verify methods had
                // already produced, which disabled both diagnostics at once: Events became
                // unreachable, and the implausible-baseline guard needs Population > 0 so it
                // silently failed open. A throw should cost one Verify method's coverage,
                // not the whole run's.
                string reason = ex.InnerException?.Message ?? ex.Message;
                Console.Error.WriteLine(
                    $"::error title=Serialisation::Verify method '{method.Name}' threw and did not execute: {reason}. "
                  + "The serialisation result is incomplete and cannot be trusted.");
                failures.Add(new FailureInfo
                {
                    Description = "Configuration",
                    Message = $"Verify method '{method.Name}' threw: {reason}"
                });
                threw = true;
                legs.Add(new LegResult { Name = method.Name, Status = "Threw", FailureCount = 1 });
                continue;
            }

            if (testResult is null)
            {
                legs.Add(new LegResult { Name = method.Name, Status = "Null" });
                continue;
            }

            var (leg, legFailures) = ReadLeg(method.Name, testResult);
            statuses.Add(leg.Status);
            population += leg.Population;
            failures.AddRange(legFailures);
            legs.Add(leg);
        }

        return (BuildResult(statuses, failures, population, threw) with { Legs = legs }, threw);
    }

    // Extracted so the per-leg breakdown is directly testable. InvokeTests needs real assemblies
    // on disk, so with the reflection inlined there the breakdown could only be verified by a full
    // CI run, which a mutation test confirmed: breaking it left the whole suite green.
    //
    // Returns the leg and its own failures rather than mutating a shared list, so a leg's
    // FailureCount cannot drift from the failures it actually contributed.
    internal static (LegResult leg, List<FailureInfo> failures) ReadLeg(string methodName, object testResult)
    {
        Type resultType = testResult.GetType();
        string status = resultType.GetProperty("Status")?.GetValue(testResult)?.ToString() ?? "Pass";
        int population = ParsePopulation(resultType.GetProperty("Description")?.GetValue(testResult)?.ToString());

        var failures = new List<FailureInfo>();

        // Deliberately not an early return when Information is absent: a failing leg with no
        // Information still has to appear in the breakdown, or the legs stop accounting for the
        // totals and the breakdown cannot be reconciled.
        if (status is "Error" or "Warning"
            && resultType.GetProperty("Information")?.GetValue(testResult) is System.Collections.IEnumerable information)
        {
            foreach (object item in information)
            {
                Type itemType = item.GetType();
                string desc = itemType.GetProperty("Description")?.GetValue(item)?.ToString() ?? "";
                string msg  = itemType.GetProperty("Message")?.GetValue(item)?.ToString() ?? "";
                failures.Add(new FailureInfo { Description = desc, Message = msg, Events = NestedMessages(item) });
            }
        }

        return (new LegResult
        {
            Name = methodName,
            Status = status,
            Population = population,
            FailureCount = failures.Count
        }, failures);
    }

    // Extracted so the invariant that a thrown Verify method does not erase the other
    // methods' results is directly testable. InvokeTests itself needs real assemblies on
    // disk, so it cannot be exercised from a unit test.
    internal static SerialiserResult BuildResult(
        List<string> statuses, List<FailureInfo> failures, int population, bool threw)
    {
        // A throw forces Error regardless of what the methods that did run reported. Without
        // this, a run where the only surviving Verify method passed would report Pass, and
        // action.yml's Pass/Warning short-circuit would skip the comparison and go green over
        // a test that never executed.
        TestStatus status = threw || statuses.Contains("Error") ? TestStatus.Error
                          : statuses.Contains("Warning")       ? TestStatus.Warning
                          : TestStatus.Pass;

        return new SerialiserResult
        {
            Status = status,
            FailureCount = failures.Count,
            Population = population,
            Failures = AnnotateCascades(failures)
        };
    }

    // Each per-item TestResult carries its own Information list holding the events the
    // serialiser recorded while failing. That is the only place the underlying exception
    // text exists, so it has to survive the projection into FailureInfo.
    internal static List<string> NestedMessages(object item)
    {
        try
        {
            if (item.GetType().GetProperty("Information")?.GetValue(item) is not System.Collections.IEnumerable nested)
                return [];

            return nested.Cast<object>()
                .Select(e => e.GetType().GetProperty("Message")?.GetValue(e)?.ToString() ?? "")
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .ToList();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: could not read nested events on '{item.GetType().Name}': {ex.Message}");
            return [];
        }
    }

    // Verify summaries state their population in prose, e.g. "Serialisation of Types via
    // json: 1524 types available." The count is not exposed structurally and the
    // implausible-baseline guard needs a denominator. Returns 0 when it cannot be read, so
    // the guard fails open rather than inventing one.
    internal static int ParsePopulation(string? summaryDescription)
    {
        if (string.IsNullOrWhiteSpace(summaryDescription))
            return 0;

        var match = Regex.Match(summaryDescription, @":\s*(\d+)\s");
        return match.Success && int.TryParse(match.Groups[1].Value, out int count) ? count : 0;
    }

    public static List<FailureInfo> AnnotateCascades(List<FailureInfo> failures)
    {
        var failingFqns = failures.Select(f => f.Description).ToHashSet(StringComparer.Ordinal);

        var typesByFqn = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => { try { return a.GetTypes(); } catch { return []; } })
            .Where(t => failingFqns.Contains(t.FullName ?? ""))
            .ToDictionary(t => t.FullName!, StringComparer.Ordinal);

        return failures.Select(f =>
        {
            if (!typesByFqn.TryGetValue(f.Description, out Type? type))
                return f;

            var rootCauses = type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .SelectMany(SafeCandidateTypes)
                .Where(t => t.FullName is not null
                         && failingFqns.Contains(t.FullName)
                         && t.FullName != f.Description)
                .Select(t => t.FullName!)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x)
                .ToList();

            return rootCauses.Count == 0
                ? f
                : f with { IsPotentialCascade = true, SuspectedRootCauses = rootCauses };
        }).ToList();
    }

    // A failing type may declare a property whose type lives in an assembly the runner
    // cannot resolve (e.g. System.Drawing.Bitmap behind the .NET Framework System.Drawing
    // facade, or a Revit API type). Reading PropertyType then throws a TypeLoadException.
    // Cascade annotation is best-effort enrichment that must never fail the run, so a
    // property whose type cannot be resolved is skipped with a warning — mirroring the
    // swallow-and-continue posture of GetTypes() above and of the legacy BHoMBot serialiser.
    internal static IReadOnlyList<Type> SafeCandidateTypes(PropertyInfo property)
    {
        try
        {
            return CandidateTypes(property.PropertyType).ToList();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: could not resolve type of property '{property.Name}' on '{property.DeclaringType?.FullName}': {ex.Message}");
            return [];
        }
    }

    private static IEnumerable<Type> CandidateTypes(Type propertyType)
    {
        yield return propertyType;
        if (propertyType.IsGenericType)
        {
            foreach (Type arg in propertyType.GetGenericArguments())
                yield return arg;
        }
        else if (propertyType.IsArray && propertyType.GetElementType() is Type elem)
        {
            yield return elem;
        }
    }
}
