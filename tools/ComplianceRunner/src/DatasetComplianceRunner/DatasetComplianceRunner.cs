using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BH.Engine.Test;                               // Modify.Merge
using BH.Engine.Test.CodeCompliance.DynamicChecks;  // Query.IsValidDataset
using BH.oM.Test;                                   // TestStatus
using BH.oM.Test.Results;                           // TestResult, ITestInformation

class DatasetComplianceRunner
{
    static int Main(string[] args)
    {
        // CLI: DatasetComplianceRunner [--output console|github|json|sarif] [--sarif-file PATH]
        //                              <file1.json> [file2.json ...]
        //
        // Only processes .json files whose path contains "datasets" (case-insensitive),
        // mirroring BHoMBot's DatasetCompliance filtering.
        var (outputFormat, sarifFilePath, files) = ArgParser.ParseDataset(args);
        if (files == null || files.Count == 0)
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  DatasetComplianceRunner [--output console|github|json|sarif] [--sarif-file PATH]");
            Console.WriteLine("                          <file1.json> [file2.json ...]");
            Console.WriteLine();
            Console.WriteLine("  --output github  = emit ::error/::warning for GitHub Actions (shows in PR).");
            Console.WriteLine("  --output json    = single JSON object to stdout.");
            Console.WriteLine("  --output sarif   = SARIF 2.1 to stdout (or --sarif-file for a file).");
            return 1;
        }

        if (outputFormat == "sarif" && !string.IsNullOrEmpty(sarifFilePath))
            outputFormat = "sarif-file";

        bool verbose = outputFormat == "console";
        if (verbose) Console.WriteLine("Running BHoM DATASET compliance...");

        var mergedResult   = new TestResult() { Status = TestStatus.Pass, Information = new List<ITestInformation>() };
        var allAnnotations = new List<Annotation>();

        foreach (var file in files)
        {
            // Only .json files under a datasets/ path are in scope.
            if (!FileFilter.IsDatasetFile(file)) continue;

            if (verbose) Console.WriteLine($"\n=== Checking: {file} ===");

            if (!File.Exists(file))
            {
                Console.WriteLine($"  [SKIP] File not found: {file}");
                continue;
            }

            var resultForThisFile = file.IsValidDataset();

            if (resultForThisFile == null)
            {
                Console.WriteLine($"  [SKIP] No result returned for: {file}");
                continue;
            }

            if (verbose) Console.WriteLine($"  Result Status: {resultForThisFile.Status}");

            mergedResult = mergedResult.Merge(resultForThisFile);

            var information        = resultForThisFile.Information ?? Enumerable.Empty<ITestInformation>();
            var perFileAnnotations = information
                .Select(i => i.ToAnnotationEquivalent())
                .ToList();
            var infoList = information.ToList();

            for (int i = 0; i < perFileAnnotations.Count; i++)
            {
                var a           = perFileAnnotations[i];
                var displayPath = string.IsNullOrEmpty(a.FilePath) ? file : a.FilePath;
                if (verbose)
                {
                    Console.WriteLine($"  - [{a.Level}] {displayPath}:{a.LineStart}:{a.ColumnStart}" +
                                      $"-{a.LineEnd}:{a.ColumnEnd} [{a.RuleName}]");
                    Console.WriteLine($"    {a.Message}");
                    if (i < infoList.Count)
                        AnnotationConvert.LogDetailedFinding(infoList[i]);
                }
                // Dataset checks don't populate Location.FilePath — anchor to the JSON file so
                // GitHub can produce an inline PR annotation instead of falling back to "unknown".
                if (string.IsNullOrEmpty(a.FilePath))
                    a.FilePath = file;
                if (a.LineStart <= 0)
                    a.LineStart = 1;

                allAnnotations.Add(a);
            }
        }

        const string checkType = "dataset";
        OutputEmitter.Write(outputFormat, checkType, mergedResult.Status, allAnnotations, sarifFilePath, verbose,
            BH.Engine.Base.Query.DocumentationURL("DevOps/Code%20Compliance%20and%20CI/Compliance%20Checks/"));

        // Exit code mirrors BHoMBot: failure only on Error; Warning and Pass are both success.
        return mergedResult.Status == TestStatus.Error ? 1 : 0;
    }

}
