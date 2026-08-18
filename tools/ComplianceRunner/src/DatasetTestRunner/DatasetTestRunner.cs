using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BH.Engine.Test;                   // Modify.Merge
using BH.Engine.UnitTest;               // CheckTest extension method
using BH.oM.Test;                       // TestStatus
using BH.oM.Test.Results;              // TestResult, ITestInformation

class DatasetTestRunner
{
    static int Main(string[] args)
    {
        // CLI: DatasetTestRunner [--output console|github|json|sarif] [--sarif-file PATH]
        //                        <file1.json> [file2.json ...]
        var (outputFormat, sarifFilePath, files) = ArgParser.ParseDataset(args);
        if (files == null || files.Count == 0)
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  DatasetTestRunner [--output console|github|json|sarif] [--sarif-file PATH]");
            Console.WriteLine("                    <file1.json> [file2.json ...]");
            return 1;
        }

        if (outputFormat == "sarif" && !string.IsNullOrEmpty(sarifFilePath))
            outputFormat = "sarif-file";

        bool verbose = outputFormat == "console";
        if (verbose) Console.WriteLine("Running BHoM DATASET UNIT TESTS...");

        // Load all BHoM assemblies including the caller's compiled DLLs.
        // Required because CheckTest() executes methods via reflection and needs
        // the caller's type system to be fully loaded in the AppDomain.
        BH.Engine.Base.Compute.LoadAllAssemblies();

        var mergedResult   = new TestResult() { Status = TestStatus.Pass, Information = new List<ITestInformation>() };
        var allAnnotations = new List<Annotation>();

        foreach (var file in files)
        {
            if (verbose) Console.WriteLine($"\n=== Running: {file} ===");

            if (!File.Exists(file))
            {
                Console.WriteLine($"  [SKIP] File not found: {file}");
                continue;
            }

            var result = file.CheckTest();

            if (result == null)
            {
                Console.WriteLine($"  [SKIP] No result returned for: {file}");
                continue;
            }

            if (verbose) Console.WriteLine($"  Result Status: {result.Status}");

            mergedResult = mergedResult.Merge(result);

            var information        = (result.Information ?? Enumerable.Empty<ITestInformation>())
                                     .Where(i => i.Status != TestStatus.Pass);
            var perFileAnnotations = information
                .Select(i => i.ToAnnotationEquivalent())
                .ToList();
            var infoList = information.ToList();

            for (int i = 0; i < perFileAnnotations.Count; i++)
            {
                var a = perFileAnnotations[i];
                if (string.IsNullOrEmpty(a.FilePath))
                    a.FilePath = file;
                if (a.LineStart <= 0)
                    a.LineStart = 1;

                if (verbose)
                {
                    Console.WriteLine($"  - [{a.Level}] {a.FilePath}:{a.LineStart} [{a.RuleName}]");
                    Console.WriteLine($"    {a.Message}");
                    if (i < infoList.Count)
                        AnnotationConvert.LogDetailedFinding(infoList[i]);
                }
                allAnnotations.Add(a);
            }
        }

        const string checkType = "dataset-tests";
        CheckMetadata.GetOutput(checkType, mergedResult.Status,
                                out string title, out string summary, out string text);

        if (verbose)
        {
            if (mergedResult.Status == TestStatus.Error || mergedResult.Status == TestStatus.Warning)
            {
                Console.WriteLine("\n--- Check output ---");
                Console.WriteLine($"Title:   {title}");
                Console.WriteLine($"Summary: {summary}");
                if (!string.IsNullOrEmpty(text)) Console.WriteLine($"Text:    {text}");
            }
            Console.WriteLine("\n===============================");
            Console.WriteLine($"FINAL RESULT: {mergedResult.Status} (Annotations: {allAnnotations.Count})");
            Console.WriteLine("===============================");
        }

        if (outputFormat == "github")
        {
            foreach (var a in allAnnotations)
            {
                var path  = PathHelper.NormaliseAnnotationPath(a.FilePath);
                var level = a.Level == "failure" ? "error" : "warning";
                var msg   = a.Message.Replace("\r", "").Replace("\n", " ");
                var col   = a.ColumnStart > 0 ? $",col={a.ColumnStart}" : "";
                Console.WriteLine($"::{level} file={path},line={a.LineStart}{col}::{msg}");
            }
        }
        else if (outputFormat == "json")
        {
            var payload = new Dictionary<string, object>
            {
                ["status"]          = mergedResult.Status.ToString(),
                ["checkType"]       = checkType,
                ["title"]           = title,
                ["summary"]         = summary,
                ["text"]            = text,
                ["annotationCount"] = allAnnotations.Count,
                ["annotations"]     = allAnnotations.Select(a => new Dictionary<string, object>
                {
                    ["path"]             = a.FilePath,
                    ["lineStart"]        = a.LineStart,
                    ["lineEnd"]          = a.LineEnd,
                    ["columnStart"]      = a.ColumnStart,
                    ["columnEnd"]        = a.ColumnEnd,
                    ["level"]            = a.Level,
                    ["message"]          = a.Message,
                    ["ruleName"]         = a.RuleName,
                    ["documentationUrl"] = a.DocumentationUrl,
                    ["bhomGuid"]         = a.BHoMGuid,
                    ["utcTime"]          = a.UTCTime.ToString("o")
                }).ToList()
            };
            Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = false }));
        }
        else if (outputFormat == "sarif" || outputFormat == "sarif-file")
        {
            var sarif = SarifBuilder.Build(checkType, title, allAnnotations,
                             BH.Engine.Base.Query.DocumentationURL("DevOps/Code%20Compliance%20and%20CI/Compliance%20Checks/"));
            if (outputFormat == "sarif-file" && !string.IsNullOrEmpty(sarifFilePath))
            {
                File.WriteAllText(sarifFilePath, sarif);
                if (verbose) Console.WriteLine($"SARIF written to {sarifFilePath}");
            }
            else
                Console.WriteLine(sarif);
        }

        return mergedResult.Status == TestStatus.Error ? 1 : 0;
    }
}
