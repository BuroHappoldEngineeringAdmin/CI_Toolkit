using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using VersioningRunner.Models;

namespace VersioningRunner.Commands;

public static class RunCommand
{

    public static int Execute(
        string assembliesPath,
        string? outputPath,
        bool testAll,
        string? subjectAssemblyList = null,
        string? configuration = null,
        IReadOnlyCollection<string>? versionConditionalMethods = null)
    {
        s_configuration = configuration;
        s_versionConditional = versionConditionalMethods is null
            ? null
            : new HashSet<string>(versionConditionalMethods, StringComparer.Ordinal);

        if (s_versionConditional is null)
            Console.WriteLine("Version-conditional method list: no scan was performed, so findings record Unknown rather than not-version-conditional.");
        else if (s_versionConditional.Count == 0)
            Console.WriteLine("Version-conditional method list: scan performed, no version-conditional methods in this repository. Findings record No.");
        else
            Console.WriteLine($"Version-conditional method list: scan performed, {s_versionConditional.Count} method(s) found.");

        var loaded = LoadAssemblies(assembliesPath);

        var methods = FindAllFromJsonDatasetsMethods(loaded);
        if (methods.Count == 0)
        {
            Console.Error.WriteLine(
                $"::error title=Versioning::No Verify.FromJsonDatasets(bool) method found across {loaded.Count} loaded assemblies. " +
                "Ensure Versioning_Toolkit/.ci/code/Verification.sln was built. " +
                "If it was, check that the declaring class is named exactly 'Verify'.");
            return 1;
        }

        Console.WriteLine($"Versioning sources ({methods.Count}): {string.Join(", ", methods.Select(m => m.DeclaringType?.FullName ?? "unknown"))}");

        // Build the loaded-namespace prefix set once, up front. Used both to
        // filter result trees (existing behaviour) and to classify an invocation
        // exception as either an infrastructure problem (a non-BHoM type that
        // failed to load — e.g. System.Drawing.Bitmap on a .NET-stack mismatch)
        // versus a genuine BHoM-side failure that should fail the check.
        var nsPrefixes = BuildNamespacePrefixes(loaded);

        // Attribution. The 3-segment prefix set over the whole closure cannot tell a
        // subject failure from a dependency's: BH.oM.Adapters.ETABS.Pier collapses to
        // BH.oM.Adapters, which any repo carrying an Adapter oM assembly owns. The v9.2
        // datasets name thousands of types a per-repo closure never builds, and each
        // comes back as CustomObject or null, so measured on a real PR that filter
        // attributed 1056 of 1056 failures to a repo that owned none of them.
        // Attribute to the exact namespaces the subject repo's own assemblies declare.
        //
        // What this run built, which the classifier needs to read a missing declaring
        // assembly correctly. Null when no subject build dir was supplied: with whole-closure
        // attribution there is no basis for calling any assembly foreign, so the
        // reclassification is disabled and the fail-safe default stands.
        HashSet<string>? subjectFileNames = ReadSubjectAssemblyList(subjectAssemblyList);

        ClosureContext? closure = null;
        if (subjectFileNames is { Count: > 0 })
        {
            var loadedNames = new HashSet<string>(
                loaded.Select(a => { try { return a.GetName().Name; } catch { return null; } })
                      .Where(n => !string.IsNullOrEmpty(n))!,
                StringComparer.Ordinal);
            var subjectBases = new HashSet<string>(
                subjectFileNames.Select(f => StripConfigSuffix(Path.GetFileNameWithoutExtension(f))),
                StringComparer.Ordinal);
            closure = new ClosureContext(
                loadedNames,
                new HashSet<string>(loadedNames.Select(StripConfigSuffix), StringComparer.Ordinal),
                subjectBases);
        }

        var subjectNamespaces = BuildSubjectNamespaces(loaded, subjectFileNames);
        Func<string, bool> isAttributable;
        if (subjectNamespaces is null)
        {
            Console.WriteLine($"Attribution: whole closure, {nsPrefixes.Count} namespace prefix(es)");
            isAttributable = d => IsFromLoadedNamespace(d, nsPrefixes);
        }
        else
        {
            // Zero namespaces is annotated rather than escalated: some repos legitimately
            // emit nothing that declares a type, so failing them would be a false block.
            // It is surfaced as a warning rather than a log line because in that state the
            // check cannot report any failure, and a green tick that means "nothing was
            // measured" is the exact shape of the bug above.
            if (subjectNamespaces.Count == 0)
            {
                Console.Error.WriteLine(
                    "::warning title=Versioning::No namespaces found for this repository's own assemblies, " +
                    "so no versioning failure can be attributed to it and this check cannot fail. " +
                    "Expected if the repo builds no type-bearing assembly; otherwise its build output is missing.");
            }

            Console.WriteLine($"Attribution: subject assemblies, {subjectNamespaces.Count} namespace(s)"
                + (subjectNamespaces.Count == 0
                    ? " — nothing to attribute to this repo, so no failure can be reported"
                    : $": {string.Join(", ", subjectNamespaces.OrderBy(x => x, StringComparer.Ordinal).Take(20))}"));
            var subject = subjectNamespaces;
            isAttributable = d => IsFromSubjectNamespace(d, subject);
        }

        var allFailures = new List<FailureInfo>();
        var infrastructureSkips = new List<string>();
        var unresolvableSkips = new List<UnverifiedFailure>();
        var diagnostics = new List<FailureDiagnostic>();

        Func<string, string, string?, (string? Cause, ClassificationPath Path, IReadOnlyList<string> Candidates)> probeSignature =
            (typeFullName, methodName, declaringAssembly) =>
                ProbeDeclaringType(loaded, typeFullName, methodName, declaringAssembly);
        foreach (var method in methods)
        {
            object? rawResult;
            try
            {
                rawResult = method.Invoke(null, new object[] { testAll });
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                var (typeName, isInfra) = ClassifyInvocationFailure(inner, nsPrefixes);

                if (isInfra)
                {
                    string label = string.IsNullOrEmpty(typeName) ? inner.GetType().Name : typeName;
                    Console.Error.WriteLine(
                        $"::warning title=Versioning::Skipping {method.DeclaringType?.FullName} — infrastructure load failure for non-BHoM type '{label}': {inner.Message}");
                    infrastructureSkips.Add($"{method.DeclaringType?.FullName} (non-BHoM type {label})");
                    continue;
                }

                Console.Error.WriteLine(
                    $"::error title=Versioning::Test execution failed ({method.DeclaringType?.FullName}): {inner.Message}");
                return 1;
            }

            var partial = ExtractFilteredResult(rawResult, isAttributable, unresolvableSkips, probeSignature, diagnostics, closure);
            allFailures.AddRange(partial.Failures);
        }

        // Attribution before remediation: print how each attributed failure was classified.
        // A total on its own cannot say whether a red is a genuine regression, a declaring
        // type that could not be loaded, or an overload that reflection could not see.
        if (diagnostics.Count > 0)
        {
            var byPath = diagnostics
                .GroupBy(d => (d.CountedAsReal, d.Path))
                .OrderByDescending(g => g.Count())
                .Select(g => $"{(g.Key.CountedAsReal ? "real" : "unverified")}/{g.Key.Path}={g.Count()}");
            Console.WriteLine($"Classification: {string.Join(", ", byPath)}");

            var realAssemblies = diagnostics
                .Where(d => d.CountedAsReal && d.DeclaringAssembly is not null)
                .GroupBy(d => d.DeclaringAssembly!, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count())
                .Take(10)
                .Select(g => $"{g.Key}={g.Count()}");
            string assemblyList = string.Join(", ", realAssemblies);
            if (assemblyList.Length > 0)
                Console.WriteLine($"Real failures by declaring assembly: {assemblyList}");

            int noAssembly = diagnostics.Count(d => d.CountedAsReal && d.DeclaringAssembly is null);
            if (noAssembly > 0)
                Console.WriteLine($"Real failures with no declaring assembly recorded: {noAssembly}");
        }

        if (infrastructureSkips.Count > 0)
        {
            Console.Error.WriteLine(
                $"::warning title=Versioning::{infrastructureSkips.Count} method(s) skipped due to infrastructure issues. Real versioning regressions on those methods will not be detected this run.");
        }

        if (unresolvableSkips.Count > 0)
        {
            var causes = unresolvableSkips
                .Select(s => s.Cause)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(c => c, StringComparer.Ordinal)
                .ToList();
            Console.Error.WriteLine(
                $"::warning title=Versioning::{unresolvableSkips.Count} failure(s) attributed to this repo were not verified, " +
                "because the only types that failed to deserialise are outside BHoM and cannot be resolved in CI: " +
                $"{string.Join(", ", causes.Take(8))}{(causes.Count > 8 ? $" and {causes.Count - 8} more" : "")}. " +
                "A genuine versioning regression in those same methods would not be detected this run.");
        }

        var status = DeriveStatus(allFailures.Count, unresolvableSkips.Count);

        // Coverage denominator. BHoMBot printed object types, methods, datasets and adapters
        // on failure only, so a green carried no evidence of what was examined. This is
        // recorded either way, which is the point: a pass over zero methods and a pass over
        // seven thousand are indistinguishable without it.
        var coverage = new CoverageCounts(
            LoadedAssemblies: loaded.Count,
            VerifyEntryPoints: methods.Count,
            SubjectAssemblies: s_subjectAssemblyCount,
            SubjectTypes: s_subjectTypeCount,
            DatasetVersions: testAll ? 0 : 1);

        var result = new VersioningResult
        {
            Status = status,
            FailureCount = allFailures.Count,
            Failures = allFailures,
            Diagnostics = diagnostics,
            Coverage = coverage,
            Configuration = configuration
        };

        Console.WriteLine($"Status:   {result.Status}");
        Console.WriteLine($"Failures: {result.FailureCount}");
        // Leads with the surface actually examined. An earlier version led with the entry-point
        // count, which is one per repository, so a coverage line intended to make a green
        // interpretable read "1" and undermined the field's whole purpose.
        Console.WriteLine(
            $"Coverage: {coverage.SubjectTypes} subject type(s) across {coverage.SubjectAssemblies} subject assembl(ies), " +
            $"checked against {(testAll ? "all staged dataset versions" : "the previous dataset version")}; " +
            $"{coverage.LoadedAssemblies} assembl(ies) loaded; " +
            $"{coverage.VerifyEntryPoints} FromJsonDatasets entry point(s) invoked");
        if (configuration is not null)
            Console.WriteLine($"Configuration: {configuration}");

        // Counted over real findings only, because the per-finding note below is printed from
        // result.Failures. Counting every diagnostic made the total disagree with the detail as
        // soon as a finding could be reclassified to unverified: the warning claimed N ambiguous
        // findings while fewer than N were listed.
        int ambiguous = diagnostics.Count(d => d.CountedAsReal && d.DeclaringTypeCandidates is { Count: > 1 });
        if (ambiguous > 0)
            Console.Error.WriteLine(
                $"::warning title=Versioning::{ambiguous} finding(s) have a declaring type present in more than one loaded assembly, " +
                "so their classification depended on assembly enumeration order. See CI_Toolkit#161.");

        const int maxLogged = 50;
        foreach (var failure in result.Failures.Take(maxLogged))
        {
            // Append the known cause when there is one, so a finding is interpretable from the
            // annotation alone. Silence here means no known cause, not that the finding is
            // unexplained by definition: VersionConditional=Unknown says the source was never
            // inspected, and that must not read as a clean bill of health.
            var d = diagnostics.FirstOrDefault(x => x.Label == failure.Description);
            var context = new List<string>();
            if (d?.VersionConditional == VersionConditionalState.Yes)
                context.Add($"Signature is version-conditional. Built as {d.Configuration ?? "an unrecorded configuration"}.");
            if (d?.DeclaringTypeCandidates is { Count: > 1 } cands)
                context.Add($"Declaring type present in {cands.Count} loaded assemblies ({string.Join(", ", cands.Take(3))}), so classification depended on enumeration order.");

            string suffix = context.Count > 0 ? " " + string.Join(" ", context) : string.Empty;
            Console.Error.WriteLine($"::error title=Versioning::{failure.Description}: {failure.Message}{suffix}");
        }
        if (result.FailureCount > maxLogged)
            Console.Error.WriteLine(
                $"::error title=Versioning::... and {result.FailureCount - maxLogged} more failures (see artifact).");

        if (outputPath is not null)
        {
            var opts = new JsonSerializerOptions
            {
                WriteIndented = true,
                Converters = { new JsonStringEnumConverter() }
            };
            File.WriteAllText(outputPath, JsonSerializer.Serialize(result, opts));
        }

        return ExitCodeFor(result.Status);
    }

    // The check's verdict, from the two counts that decide it.
    //
    // Warning, not Pass, when everything attributed was unverifiable: no genuine
    // regression was found, but nothing was actually verified either, and a bare green
    // tick is what let this check report success for so long. Any real failure outranks
    // that, so failures are tested first.
    internal static VersioningStatus DeriveStatus(int failureCount, int unverifiedCount)
        => failureCount > 0
            ? VersioningStatus.Error
            : unverifiedCount > 0 ? VersioningStatus.Warning : VersioningStatus.Pass;

    // Only Error fails the job. Warning is deliberately exit 0: it means nothing could be
    // verified, which must be visible in the log and the artefact without blocking a PR
    // over an environment the author cannot fix. ci-versioning reads this through
    // $LASTEXITCODE and branches its job summary on it, so the mapping is public API.
    internal static int ExitCodeFor(VersioningStatus status)
        => status == VersioningStatus.Error ? 1 : 0;

    private static readonly string[] _assemblyFileSuffixes =
    [
        "oM.dll",
        "_Engine.dll",
        "_Adapter.dll",
        "_Test.dll"
    ];

    // Revit version assemblies use a year-suffixed naming convention (e.g. Revit_Toolkit2024.dll).
    // The pattern is intentionally permissive — not constrained to known toolkit names — so that
    // new Revit toolkits are picked up automatically. The assembly folder contains only BHoM-built
    // DLLs, so false positives are not a concern in practice.
    private static readonly Regex _revitAssemblyPattern =
        new(@"Revit_\w+20\d{2}\.dll$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Load order decides classification, so it must not be left to the filesystem.
    //
    // ProbeDeclaringType walks the loaded list and takes its verdict from the FIRST
    // assembly that yields the declaring type. Where two repos declare the same type,
    // whichever is enumerated first decides whether the finding reads as a genuine
    // regression or as an infrastructure problem. That is CI_Toolkit#161's mechanism:
    // BH.Revit.Engine.Core.Compute is defined by both Revit_Core_Engine and
    // Revit_ModelQA_Engine, and 42 such type-level collisions exist across the fleet.
    //
    // Directory.GetFiles documents no ordering. Measured on windows-2025-vs2026 across
    // four cold-rebuild runs on separate runners, NTFS returned exactly
    // StringComparer.OrdinalIgnoreCase order every time (132 and 111 entries), so
    // sorting is a no-op there and this is defensive rather than corrective. The
    // comparer is not interchangeable: OrdinalIgnoreCase uppercases before comparing,
    // which puts '_' (0x5F) after letters, so RevitAPIUI sorts before Revit_Adapter.
    // A lowercase-based sort reverses that pair and would change which assembly answers.
    internal static IReadOnlyList<string> OrderForLoad(IEnumerable<string> files)
        => files.OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase).ToArray();

    private static List<Assembly> LoadAssemblies(string folder)
    {
        var loaded = new List<Assembly>();
        foreach (string file in OrderForLoad(Directory.GetFiles(folder)))
        {
            if (IsLoadableAssembly(file))
            {
                try { loaded.Add(Assembly.LoadFrom(file)); }
                catch (Exception ex)
                { Console.Error.WriteLine($"Warning: could not load {file}: {ex.Message}"); }
            }
        }
        return loaded;
    }

    public static bool IsLoadableAssembly(string filePath)
    {
        string fileName = Path.GetFileName(filePath);
        return _assemblyFileSuffixes.Any(s => fileName.EndsWith(s, StringComparison.OrdinalIgnoreCase))
            || _revitAssemblyPattern.IsMatch(fileName);
    }

    // Finds all static FromJsonDatasets(bool) : TestResult methods on any class named "Verify"
    // across all loaded assemblies. The namespace is intentionally unconstrained so that
    // extending orgs (e.g. BHE) can ship their own Verify class in any namespace alongside
    // BHoM's BH.Test.Versioning.Verify without requiring changes to BHoM/Versioning_Toolkit.
    private static IReadOnlyList<MethodInfo> FindAllFromJsonDatasetsMethods(IEnumerable<Assembly> assemblies)
    {
        var methods = new List<MethodInfo>();
        foreach (var asm in assemblies)
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch { continue; }

            foreach (var type in types.Where(t => t.Name == "Verify"))
            {
                var method = type
                    .GetMethods(BindingFlags.Static | BindingFlags.Public)
                    .FirstOrDefault(m =>
                        m.Name == "FromJsonDatasets"
                        && m.ReturnType.FullName == "BH.oM.Test.Results.TestResult"
                        && m.GetParameters().Length == 1
                        && m.GetParameters()[0].ParameterType == typeof(bool));

                if (method is not null)
                    methods.Add(method);
            }
        }
        return methods;
    }

    // ---------------------------------------------------------------------------
    // Filtered extraction — traverses the nested TestResult tree returned by
    // FromJsonDatasets and keeps only leaf failures whose 3-segment namespace
    // prefix (e.g. "BH.oM.Base") is present in the loaded assembly set.
    // This prevents false positives from BHoM toolkit types that are referenced
    // in the v9.1 test datasets but whose assemblies are not loaded for this repo.
    // ---------------------------------------------------------------------------

    public static VersioningResult ExtractFilteredResult(object? rawResult, List<Assembly> loaded)
    {
        var nsPrefixes = BuildNamespacePrefixes(loaded);
        return ExtractFilteredResult(rawResult, d => IsFromLoadedNamespace(d, nsPrefixes));
    }

    public static VersioningResult ExtractFilteredResult(
        object? rawResult, Func<string, bool> isAttributable, List<UnverifiedFailure>? unresolvableSkips = null,
        Func<string, string, string?, (string? Cause, ClassificationPath Path, IReadOnlyList<string> Candidates)>? probeSignature = null,
        List<FailureDiagnostic>? diagnostics = null,
        ClosureContext? closure = null)
    {
        if (rawResult is null)
            return new VersioningResult
            {
                Status = VersioningStatus.Error,
                FailureCount = 1,
                Failures = [new FailureInfo("Unknown", "Test returned null.")]
            };

        var failures = new List<FailureInfo>();
        CollectLeafFailures(rawResult, isAttributable, failures, unresolvableSkips, probeSignature, diagnostics, closure, depth: 0);

        var status = failures.Count > 0 ? VersioningStatus.Error : VersioningStatus.Pass;
        return new VersioningResult
        {
            Status = status,
            FailureCount = failures.Count,
            Failures = failures,
            Diagnostics = diagnostics ?? []
        };
    }

    private static void CollectLeafFailures(
        object node, Func<string, bool> isAttributable, List<FailureInfo> failures,
        List<UnverifiedFailure>? unresolvableSkips,
        Func<string, string, string?, (string? Cause, ClassificationPath Path, IReadOnlyList<string> Candidates)>? probeSignature,
        List<FailureDiagnostic>? diagnostics, ClosureContext? closure, int depth)
    {
        // BHoM's TestResult tree has at most 3 levels under the root (outer → per-version
        // summary → individual type result). Depth 5 gives headroom for unexpected nesting
        // without risking unbounded recursion on malformed results.
        if (depth > 5) return;

        var nodeType = node.GetType();
        var infoObj = nodeType.GetProperty("Information")?.GetValue(node);
        var allChildren = (infoObj as IEnumerable)?.Cast<object>().ToList() ?? [];

        // BHoM's FromJsonItem populates Information with EventMessage objects from
        // CurrentEvents() — logging artifacts that must not be walked into, or the
        // real failure node above them is treated as an internal node and its
        // Description (the only thing attribution can match on) is never
        // read. EventMessage does expose Status, so Status alone cannot tell the two
        // apart; measured shapes are:
        //   TestResult   Description, Status, Information, Message, UTCTime, ID
        //   EventMessage Message, Status, UTCTime, StackTrace
        // Information is the discriminator: only a nested result can carry children.
        var resultChildren = allChildren
            .Where(c => c.GetType().GetProperty("Status") is not null
                     && c.GetType().GetProperty("Information") is not null)
            .ToList();

        if (resultChildren.Count == 0)
        {
            string statusStr = nodeType.GetProperty("Status")?.GetValue(node)?.ToString() ?? "Pass";
            if (statusStr is not ("Error" or "Warning")) return;

            string desc = nodeType.GetProperty("Description")?.GetValue(node)?.ToString() ?? "";
            string msg  = nodeType.GetProperty("Message")?.GetValue(node)?.ToString() ?? "";

            if (!isAttributable(desc))
                return;

            // allChildren are all EventMessages here, since resultChildren is empty.
            var eventMessages = allChildren
                .Select(c => c.GetType().GetProperty("Message")?.GetValue(c)?.ToString() ?? "")
                .ToList();

            // DescriptionFromJson mangles many method entries to "<DeclaringType>. }",
            // losing the method name. The Method event still carries both, so prefer it.
            var (eventType, eventMethod) = eventMessages
                .Select(ParseMethodEvent)
                .FirstOrDefault(p => p.DeclaringType is not null && p.MethodName is not null);
            string label = eventType is not null && eventMethod is not null && desc.EndsWith(". }", StringComparison.Ordinal)
                ? $"{eventType}.{eventMethod}"
                : desc;

            // The Method event's "Name" is assembly-qualified. It is the only handle on which
            // assembly should declare the type, so it decides who gets asked when the type
            // cannot be resolved.
            string? declaringAssembly = eventMessages
                .Select(ParseMethodEventAssembly)
                .FirstOrDefault(a => a is not null);

            string? cause = ClassifyUnresolvableCause(eventMessages);
            var path = ClassificationPath.UnresolvableFromEvents;
            IReadOnlyList<string> candidates = Array.Empty<string>();

            // No type-level cause recorded means the blocker may be in the signature
            // rather than the payload, which only reflection over the method can tell.
            if (cause is null)
            {
                if (eventType is null || eventMethod is null)
                    path = ClassificationPath.NoMethodEvent;
                else if (probeSignature is not null)
                    (cause, path, candidates) = probeSignature(eventType, eventMethod, declaringAssembly);
                else
                    path = ClassificationPath.ProbeNotSupplied;
            }

            // Reclassify a finding whose recorded declaring assembly is not in
            // this closure.
            //
            // candidates.Count > 0 is load-bearing and is the difference from v1. It means
            // some OTHER loaded assembly answered for the type, so the probe verdict above
            // describes code we were never asked about. When nothing answered, the path is
            // DeclaringTypeNotLoaded, which is the signal that the type is genuinely gone;
            // reclassifying that would convert a real removal into a silent pass.
            if (cause is null && closure is not null && declaringAssembly is not null
                && candidates.Count > 0
                && !closure.LoadedNames.Contains(declaringAssembly))
            {
                string baseName = StripConfigSuffix(declaringAssembly);
                if (!closure.SubjectBaseNames.Contains(baseName))
                {
                    // Nothing this repository builds under any configuration, and something
                    // else answered for the type, so the entry is another repository's.
                    cause = $"{declaringAssembly} (declaring assembly is not part of this repository)";
                    path = ClassificationPath.ForeignDeclaringAssembly;
                }
                else if (closure.LoadedBaseNames.Contains(baseName))
                {
                    // Ours, and a sibling configuration of the same family is loaded, so the
                    // family exists and only this configuration was not compiled.
                    cause = $"{declaringAssembly} (build configuration not compiled in this run)";
                    path = ClassificationPath.ConfigurationNotBuilt;
                }
                // Otherwise the family is ours but no configuration of it is loaded at all.
                // "Not compiled" and "removed outright" are indistinguishable there, so the
                // finding is left real. Ordering matters: folding this into the condition
                // above makes the branch unreachable, which is how v1 lost it.
            }

            if (cause is not null)
                unresolvableSkips?.Add(new UnverifiedFailure(label, cause));
            else
                failures.Add(new FailureInfo(label, msg));

            diagnostics?.Add(new FailureDiagnostic(
                label, cause is null, path, cause, eventType, declaringAssembly, eventMessages.Count,
                // Only recorded when more than one assembly yielded the type. One candidate is
                // the ordinary case and carrying it would add noise to every row.
                DeclaringTypeCandidates: candidates.Count > 1 ? candidates : null,
                Configuration: s_configuration,
                VersionConditional: ClassifyVersionConditional(eventType, eventMethod)));
        }
        else
        {
            foreach (var child in resultChildren)
            {
                string childStatus = child.GetType().GetProperty("Status")?.GetValue(child)?.ToString() ?? "Pass";
                if (childStatus is "Error" or "Warning")
                    CollectLeafFailures(child, isAttributable, failures, unresolvableSkips, probeSignature, diagnostics, closure, depth + 1);
            }
        }
    }

    public static HashSet<string> BuildNamespacePrefixes(IEnumerable<Assembly> assemblies)
    {
        var prefixes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var asm in assemblies)
        {
            // ReflectionTypeLoadException is the common case for Revit-versioned
            // assemblies whose RevitAPI.dll dependency is missing at versioning time:
            // some types load, others don't. ex.Types gives us the partial list
            // (nulls for failures). Swallowing the whole exception here previously
            // dropped the entire assembly, leaving its namespace prefix unregistered
            // — which caused IsFromLoadedNamespace to filter out genuine failures.
            Type?[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types; }
            catch { continue; }

            foreach (var type in types)
            {
                if (type?.FullName is null) continue;
                var parts = type.FullName.Split('.');
                if (parts.Length >= 3)
                    prefixes.Add(parts[0] + "." + parts[1] + "." + parts[2]);
            }
        }
        return prefixes;
    }

    // The exact namespaces declared by the subject repo's own assemblies, or null when
    // no build directory was supplied (callers that have not been updated keep the
    // whole-closure behaviour). Names come off the build output but are resolved against
    // the loaded set, which is authoritative, so a stale Build\ entry cannot introduce a
    // namespace for an assembly that is not actually present.

    // The subject set is the assemblies this repository's own build staged, handed over as a
    // list the caller computed rather than a directory this reads.
    //
    // It used to be a directory, `<workspace>\Build`, which the caller assumed every project
    // wrote to. Nothing guaranteed that: the convention was enforced by a linter that only
    // rewrote OutputPath values already present and never inspected which configuration they
    // applied to, and no check read the directory until this one. Measured across 138 projects,
    // 30% did not write there under the configuration CI builds, and a different 35% would not
    // under the other one. So there was no configuration that made it reliable.
    //
    // A list has a property the directory did not: it describes what happened rather than what
    // was declared. It also cannot be partially right. A directory holding some of the
    // repository's assemblies produced a subject set that looked plausible and silently covered
    // a fraction of the repo, with nothing to indicate it.
    //
    // Null means no list was supplied and attribution falls back to the whole closure, which
    // over-reports. Empty means the list was supplied and the build staged nothing, which is a
    // different state and is warned about by the caller before this runs.
    // Split from the file read so the name handling can be tested without a temp file.
    internal static HashSet<string> ReadSubjectAssemblyListFrom(IEnumerable<string> entries) =>
        new(entries.Select(l => l.Trim())
                   .Where(l => l.Length > 0)
                   .Select(Path.GetFileName)
                   .Where(n => !string.IsNullOrEmpty(n))!,
            StringComparer.OrdinalIgnoreCase);

    internal static HashSet<string>? ReadSubjectAssemblyList(string? listPath)
    {
        if (string.IsNullOrWhiteSpace(listPath))
            return null;

        if (!File.Exists(listPath))
        {
            Console.Error.WriteLine(
                $"::warning title=Versioning::Subject assembly list '{listPath}' not found. " +
                "Falling back to attributing failures across the whole dependency closure, " +
                "which over-reports: failures owned by dependencies will be attributed to this repo.");
            return null;
        }

        // Names, not paths. Callers may write either; the comparison downstream is by file
        // name, because that is what identifies an assembly in the staged set.
        return ReadSubjectAssemblyListFrom(File.ReadAllLines(listPath));
    }

    internal static HashSet<string>? BuildSubjectNamespaces(List<Assembly> loaded, HashSet<string>? subjectFiles)
    {
        if (subjectFiles is null)
            return null;

        var namespaces = new HashSet<string>(StringComparer.Ordinal);
        int unreadable = 0;
        int subjectTypeCount = 0;
        string? firstUnreadable = null;
        foreach (var asm in loaded)
        {
            string name;
            try { name = Path.GetFileName(asm.Location); }
            catch { continue; }

            if (string.IsNullOrEmpty(name) || !subjectFiles.Contains(name))
                continue;

            Type?[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types; }
            catch { continue; }

            foreach (var type in types)
            {
                if (type is null)
                    continue;

                subjectTypeCount++;

                // Reading Namespace walks to the enclosing type, so a nested type whose
                // declaring type needs an unresolvable assembly throws here even though
                // ex.Types handed it to us non-null. One such type must not lose the
                // whole assembly, and it must not be silent either.
                //
                // The WPF cause of this is gone: Revit_*_UI_20NN nested types used to
                // throw FileNotFoundException for PresentationFramework, which is why
                // the runner now targets net8.0-windows (see VersioningRunner.csproj).
                // The guard stays because that was not the only cause. When Revit mock
                // provisioning soft-skips, the same shape recurs against RevitAPIUI:
                // measured on BHoM/Revit_Toolkit, 120 unreadable types led by
                // Revit_Core_Adapter_2022 failing to resolve RevitAPIUI. That one is
                // tracked as issue #143 and is not fixed here.
                try
                {
                    if (type.Namespace is { Length: > 0 } ns)
                        namespaces.Add(ns);
                }
                catch (Exception ex)
                {
                    unreadable++;
                    firstUnreadable ??= $"{name}: {ex.GetType().Name}: {ex.Message}";
                }
            }
        }

        if (unreadable > 0)
        {
            Console.Error.WriteLine(
                $"::warning title=Versioning::{unreadable} subject type(s) had an unreadable namespace and were skipped, " +
                $"so failures in them cannot be attributed. First: {firstUnreadable}");
        }

        s_subjectAssemblyCount = subjectFiles.Count;
        s_subjectTypeCount = subjectTypeCount;

        Console.WriteLine(
            $"Subject assemblies: {subjectFiles.Count} name(s) staged by the subject build, " +
            $"{subjectFiles.Count(f => loaded.Any(a => PathName(a) == f))} present in the loaded set");
        return namespaces;

        static string PathName(Assembly a)
        {
            try { return Path.GetFileName(a.Location); }
            catch { return string.Empty; }
        }
    }

    public static bool IsFromLoadedNamespace(string description, HashSet<string> nsPrefixes)
    {
        if (string.IsNullOrWhiteSpace(description)) return false;

        // Strip method parameters if present ("BH.Engine.Foo.Bar(BH.oM.X)" → "BH.Engine.Foo.Bar")
        int parenIdx = description.IndexOf('(');
        string typePart = parenIdx > 0 ? description[..parenIdx] : description;

        var parts = typePart.Split('.');
        if (parts.Length < 3) return false;

        return nsPrefixes.Contains(parts[0] + "." + parts[1] + "." + parts[2]);
    }

    // Attribution against exact namespaces. A failure belongs to the subject when its
    // declaring type sits in one of the subject's namespaces or below it. Matching on a
    // segment boundary keeps BH.oM.Adapters.ETABS.Pier out when the subject only declares
    // BH.oM.Adapters.File. Descriptions arrive either as a type full name or as
    // "DeclaringType.MethodName" (Versioning_Toolkit's DescriptionFromJson), and both are
    // covered: each is a strict prefix extension of the declaring namespace.
    public static bool IsFromSubjectNamespace(string description, HashSet<string> subjectNamespaces)
    {
        if (string.IsNullOrWhiteSpace(description) || subjectNamespaces.Count == 0)
            return false;

        int parenIdx = description.IndexOf('(');
        string typePart = parenIdx > 0 ? description[..parenIdx] : description;

        foreach (string ns in subjectNamespaces)
        {
            if (typePart.Length > ns.Length
                && typePart[ns.Length] == '.'
                && typePart.StartsWith(ns, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    // "Method DuctLogicalOrFilter from { "_t" : "System.Type", "Name" : "BH.Revit.Engine
    //  .MechanicalPlumbing.Create, Revit_MechanicalPlumbing_Engine_2022, ..." ... } failed
    //  to deserialise." — the only event some failures carry. It names the method and its
    // declaring type where Description has been mangled to "...Create. }" by
    // Versioning_Toolkit's DescriptionFromJson, so it is the better label as well as the
    // handle needed to probe the signature.
    private static readonly Regex _methodEventPattern = new(
        @"^Method\s+(?<method>\S+)\s+from\s+.*?""Name""\s*:\s*""(?<type>[^"",]+)",
        RegexOptions.Compiled);

    public static (string? DeclaringType, string? MethodName) ParseMethodEvent(string message)
    {
        if (string.IsNullOrEmpty(message))
            return (null, null);

        var match = _methodEventPattern.Match(message);
        if (!match.Success)
            return (null, null);

        string method = match.Groups["method"].Value.Trim();
        string type = match.Groups["type"].Value.Trim();
        return (type.Length > 0 ? type : null, method.Length > 0 ? method : null);
    }

    // The Method event's "Name" field is assembly-qualified. ParseMethodEvent stops at the
    // comma because it only needs the type, so the assembly is discarded. It is the handle
    // for asking whether a declaring type is missing because its assembly failed to load,
    // so it is captured here for the diagnostic record.
    private static readonly Regex _methodEventAssemblyPattern = new(
        @"^Method\s+\S+\s+from\s+.*?""Name""\s*:\s*""[^"",]+,\s*(?<assembly>[^"",]+)",
        RegexOptions.Compiled);

    public static string? ParseMethodEventAssembly(string message)
    {
        if (string.IsNullOrEmpty(message))
            return null;

        var match = _methodEventAssemblyPattern.Match(message);
        if (!match.Success)
            return null;

        string assembly = match.Groups["assembly"].Value.Trim();
        return assembly.Length > 0 ? assembly : null;
    }

    // A failure attributed to the subject that could not actually be verified, and the
    // non-BHoM type identity that made it unverifiable.
    public readonly record struct UnverifiedFailure(string Description, string Cause);

    // Some failures record no type-level cause at all, because the type that cannot be
    // resolved is in the method's signature rather than in the deserialised payload.
    // Measured: BH.Revit.Engine.MechanicalPlumbing.Create.DuctLogicalOrFilter(bool,bool,bool)
    // exists with exactly the recorded signature, so it is not a regression, but its return
    // type is Autodesk.Revit.DB.LogicalOrFilter and the MethodInfo cannot be materialised
    // without RevitAPI. Probe the signature and report the blocker only when it is
    // positively observed and outside BHoM; anything else stays a real failure.
    // Resolves the declaring type, then either probes its signature or explains why it could
    // not be resolved.
    //
    // The second half exists because GetType(throwOnError: false) returns null and discards
    // the reason, which made "the type could not be loaded" indistinguishable from "the type
    // was deleted", and the classifier defaulted both to a real failure. Measured on a live
    // repo: 443 of 443 attributed failures took that route, none of them genuine.
    //
    // Asking the assembly the event names, with throwOnError: true, separates the two.
    // Validated read-only against mirrored assembly sets, with and without the Revit mocks:
    //   mocks absent, real type    FileNotFoundException 'RevitAPI, Version=22.0.0.0'  unverified
    //   type genuinely deleted     TypeLoadException naming the BH. type itself        real
    //   mocks present, real type   resolves, so the signature probe runs as before
    // Run-scoped context, set once by Execute. Held statically because the failure walk is
    // reached through two helper layers that take no context parameter, and threading one
    // through both for two constant values would be a wider change than the values justify.
    private static string? s_configuration;
    // Collapses a build-configuration suffix to the family name. Anchored and
    // restricted to 20xx because that is the only config-variant convention in the fleet
    // today: measured 295 of 640 assemblies match, all Revit, all with a sibling variant.
    // It is a naming heuristic over an unenforced convention, not a declared relationship.
    internal static string StripConfigSuffix(string assemblyName)
        => Regex.Replace(assemblyName, @"_20\d{2}$", string.Empty);

    private static HashSet<string>? s_versionConditional;
    private static int s_subjectAssemblyCount;
    private static int s_subjectTypeCount;

    // Three states, never two. A null set means no list was supplied, so the source was
    // never inspected and we do not know. That is not the same as "not version-conditional",
    // and the register treats them differently: Unknown leaves a divergence unscored,
    // No lets it count. Collapsing them would mark findings explained on no evidence.
    internal static VersionConditionalState ClassifyVersionConditional(string? typeFullName, string? methodName)
    {
        if (s_versionConditional is null) return VersionConditionalState.Unknown;
        if (typeFullName is null || methodName is null) return VersionConditionalState.Unknown;
        return s_versionConditional.Contains($"{typeFullName}.{methodName}")
            || s_versionConditional.Contains(methodName)
                ? VersionConditionalState.Yes
                : VersionConditionalState.No;
    }

    // Returns every assembly that yielded the declaring type, not just the first.
    //
    // The loop used to return on the first match, which made the classification depend on
    // assembly enumeration order wherever two repos declare into the same namespace
    // (CI_Toolkit#161, measured on BH.Revit.Engine.Core). The probe result still comes from
    // the first match, so behaviour is unchanged; the candidate list is recorded so a
    // classification that could have gone either way is visible rather than silent.
    internal static (string? Cause, ClassificationPath Path, IReadOnlyList<string> Candidates) ProbeDeclaringType(
        List<Assembly> loaded, string typeFullName, string methodName, string? declaringAssembly)
    {
        var candidates = new List<string>();
        string? firstBlocker = null;
        ClassificationPath? firstPath = null;

        foreach (var asm in loaded)
        {
            Type? type;
            try { type = asm.GetType(typeFullName, throwOnError: false); }
            catch { continue; }

            if (type is not null)
            {
                try { candidates.Add(asm.GetName().Name ?? "(unnamed)"); }
                catch { candidates.Add("(unnamed)"); }

                if (firstPath is null)
                {
                    firstBlocker = ProbeSignatureBlocker(type, methodName, out var probePath);
                    firstPath = probePath;
                }
            }
        }

        if (firstPath is not null)
            return (firstBlocker, firstPath.Value, candidates);

        // Nothing yielded the type. Ask its own assembly for the reason, when the event named
        // one and it is in the closure. Anything else stays real, which fails safe.
        var owner = declaringAssembly is null
            ? null
            : loaded.FirstOrDefault(a =>
                string.Equals(a.GetName().Name, declaringAssembly, StringComparison.Ordinal));

        if (owner is null)
            return (null, ClassificationPath.DeclaringTypeNotLoaded, candidates);

        try
        {
            var type = owner.GetType(typeFullName, throwOnError: true);
            if (type is not null)
            {
                string? blocker = ProbeSignatureBlocker(type, methodName, out var probePath);
                return (blocker, probePath, candidates);
            }
            return (null, ClassificationPath.DeclaringTypeNotLoaded, candidates);
        }
        catch (Exception ex)
        {
            var (cause, path) = ClassifyDeclaringTypeFailure(ex, typeFullName);
            return (cause, path, candidates);
        }
    }

    // Splits "the type is gone" from "the type cannot be built here", given whatever the
    // declaring assembly threw when asked for it.
    //
    // Unverified requires a named blocker that is something OTHER than the type we asked for.
    // A blocker naming the requested type means its own assembly cannot produce it, which is a
    // genuine removal and exactly what versioning exists to catch, so it stays real. So does
    // an unnamed failure. Deliberately not keyed on the exception class: TypeLoadException
    // carries an empty TypeName even when its message names the type, measured on both CoreLib
    // and a real BHoM assembly, so it arrives here with no blocker and is already handled.
    //
    // Measured shapes: a missing Revit mock gives FileNotFoundException
    // 'RevitAPI, Version=22.0.0.0', trimmed to 'RevitAPI', which is unverified. A deleted type
    // gives TypeLoadException with nothing extractable, which stays real.
    internal static (string? Cause, ClassificationPath Path) ClassifyDeclaringTypeFailure(
        Exception ex, string typeFullName)
    {
        string? blocker = NonBHoMTypeFrom(ex);

        if (blocker is null || string.Equals(blocker, typeFullName, StringComparison.Ordinal))
            return (null, ClassificationPath.DeclaringTypeAbsent);

        return (blocker, ClassificationPath.DeclaringTypeUnloadable);
    }

    public static string? ProbeSignatureBlocker(Type declaringType, string methodName)
        => ProbeSignatureBlocker(declaringType, methodName, out _);

    // Same decision as the two-argument overload, which delegates here, plus which branch
    // reached it. The extra output is diagnostic only and no behaviour depends on it.
    public static string? ProbeSignatureBlocker(Type declaringType, string methodName, out ClassificationPath path)
    {
        MethodInfo[] candidates;
        try
        {
            candidates = declaringType
                .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
                .Where(m => m.Name == methodName)
                .ToArray();
        }
        catch (Exception ex)
        {
            string? blocker = NonBHoMTypeFrom(ex);
            path = blocker is not null
                ? ClassificationPath.SignatureBlockerOutsideBHoM
                : ClassificationPath.SignatureBlockerInsideBHoM;
            return blocker;
        }

        // No overload of that name at all is a genuine finding, not an infrastructure one.
        if (candidates.Length == 0)
        {
            path = ClassificationPath.NoOverloadFound;
            return null;
        }

        bool sawBHoMBlocker = false;
        foreach (var method in candidates)
        {
            try
            {
                _ = method.ReturnType.FullName;
                foreach (var parameter in method.GetParameters())
                    _ = parameter.ParameterType.FullName;
            }
            catch (Exception ex)
            {
                string? blocker = NonBHoMTypeFrom(ex);
                if (blocker is not null)
                {
                    path = ClassificationPath.SignatureBlockerOutsideBHoM;
                    return blocker;
                }
                sawBHoMBlocker = true;
            }
        }

        path = sawBHoMBlocker
            ? ClassificationPath.SignatureBlockerInsideBHoM
            : ClassificationPath.SignatureResolved;
        return null;
    }

    // The identity an exception names, with the assembly-qualification tail removed.
    // Type identities arrive as "Foo.Bar, Assembly, Version=...", so strip at the first
    // comma to leave the bare type or assembly name for matching. Null means the
    // exception named nothing usable; an empty string means it named only separators,
    // which callers treat differently from null, so the two are kept distinct.
    //
    // Shared by NonBHoMTypeFrom and ClassifyInvocationFailure, which extract the same
    // identity and then decide differently on it. Only the extraction is common; keeping
    // it in one place stops a newly handled exception type reaching one caller and not
    // the other.
    private static string? IdentityFrom(Exception ex)
    {
        string? raw = ex switch
        {
            TypeLoadException tle => tle.TypeName,
            FileNotFoundException fnf => fnf.FileName,
            FileLoadException fle => fle.FileName,
            _ => null
        };

        if (string.IsNullOrEmpty(raw))
            return null;

        int commaIdx = raw.IndexOf(',');
        return (commaIdx > 0 ? raw[..commaIdx] : raw).Trim();
    }

    private static string? NonBHoMTypeFrom(Exception ex)
    {
        string? bare = IdentityFrom(ex);
        if (bare is null)
            return null;

        return bare.StartsWith("BH.", StringComparison.Ordinal) || bare.Length == 0 ? null : bare;
    }

    // A leaf failure carries its cause in the EventMessages FromJsonItem attached to it,
    // one per type that could not be deserialised, e.g.
    //   "Type Autodesk.Revit.DB.Document, RevitAPI, Version=9.0.0.0, ... failed to deserialise."
    // followed by "Method <name> from { ...json... } failed to deserialise."
    // When every named type is outside BHoM the failure says nothing about this repo's
    // code: RevitAPI is mixed-mode native and cannot be loaded on .NET 5+ at all, so those
    // methods can never resolve in CI regardless of the PR. Returns the first such type,
    // or null when the failure is real. Anything naming a BH. type is real by default, so
    // a genuine regression is never classified away.
    public static string? ClassifyUnresolvableCause(IEnumerable<string> eventMessages)
    {
        var namedTypes = new List<string>();

        foreach (string message in eventMessages)
        {
            if (string.IsNullOrEmpty(message)
                || !message.StartsWith("Type ", StringComparison.Ordinal)
                || !message.Contains("failed to deserialise", StringComparison.Ordinal))
                continue;

            string identity = message["Type ".Length..];
            int commaIdx = identity.IndexOf(',');
            string bare = (commaIdx > 0 ? identity[..commaIdx] : identity).Trim();

            if (bare.Length > 0)
                namedTypes.Add(bare);
        }

        if (namedTypes.Count == 0)
            return null;

        if (namedTypes.Any(t => t.StartsWith("BH.", StringComparison.Ordinal)))
            return null;

        return namedTypes[0];
    }

    // Classifies an exception thrown from invoking a versioning test method.
    // Distinguishes:
    //   * BHoM-side failures (the throwing type's namespace is in the loaded BHoM
    //     prefix set) — these should fail the check.
    //   * Infrastructure failures (the throwing type is a .NET / Microsoft type
    //     the runner cannot satisfy from its own deps, e.g. System.Drawing.Bitmap
    //     across a netfx / net8 boundary) — these should be skipped with a
    //     warning so a runtime-stack mismatch does not mask all results.
    //
    // Returns a TypeName extracted from the exception (empty if none can be
    // determined) and an IsInfrastructure flag. Anything we cannot positively
    // identify as infrastructure defaults to false (real failure) so a third-party
    // dep issue that turns out to be a genuine bug is not silently swallowed.
    public static (string TypeName, bool IsInfrastructure) ClassifyInvocationFailure(
        Exception ex, HashSet<string> nsPrefixes)
    {
        string? bare = IdentityFrom(ex);

        if (bare is null)
            return (string.Empty, false);

        if (IsFromLoadedNamespace(bare, nsPrefixes))
            return (bare, false);

        if (bare.StartsWith("System.", StringComparison.Ordinal)
            || bare.StartsWith("Microsoft.", StringComparison.Ordinal)
            || bare.Equals("System", StringComparison.Ordinal)
            || bare.Equals("mscorlib", StringComparison.Ordinal))
        {
            return (bare, true);
        }

        return (bare, false);
    }

}
