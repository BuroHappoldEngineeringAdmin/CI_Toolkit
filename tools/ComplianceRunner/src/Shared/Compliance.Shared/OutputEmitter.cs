using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BH.oM.Test;   // TestStatus

/// <summary>
/// Shared result output for the compliance runners. Computes the check
/// title/summary/text via CheckMetadata and emits the merged result in the
/// requested format: a verbose console summary, GitHub workflow commands,
/// a single JSON object, or SARIF 2.1. Extracted so ComplianceRunner and
/// DatasetComplianceRunner surface results identically; only their per-file
/// check logic differs.
///
/// toolUri is supplied by the caller (it comes from BH.Engine.Base) rather
/// than resolved here, so this type has no BHoM runtime dependency, matching
/// SarifBuilder.
/// </summary>
public static class OutputEmitter
{
    public static void Write(
        string outputFormat,
        string checkType,
        TestStatus status,
        List<Annotation> annotations,
        string? sarifFilePath,
        bool verbose,
        string toolUri,
        FileAccounting? accounting = null)
    {
        CheckMetadata.GetOutput(checkType, status, out string title, out string summary, out string text);

        // Coverage goes to stdout only for the two human-facing formats. json and sarif put a
        // payload on stdout and nothing else may precede it: an unconditional Console.WriteLine
        // here is exactly how the [SKIP] diagnostic already breaks a caller that parses stdout
        // directly. The counts still reach machine-readable consumers, structurally, below.
        if (accounting is not null && (outputFormat == "console" || outputFormat == "github"))
        {
            Console.WriteLine(accounting.CoverageLine());

            // Reported, never failed. Whether examining nothing should fail is a separate and
            // undecided question; this only makes the state visible, and it is emitted as a
            // workflow command so it survives the collapsed log group the caller wraps this in.
            if (accounting.ExaminedNothing)
            {
                string warning = accounting.ExaminedNothingWarning();
                if (outputFormat == "github")
                    Console.WriteLine($"::warning title=Compliance coverage::{warning}");
                else
                    Console.WriteLine($"WARNING: {warning}");
            }
        }

        if (verbose)
        {
            if (status == TestStatus.Error || status == TestStatus.Warning)
            {
                Console.WriteLine("\n--- Check output ---");
                Console.WriteLine($"Title:   {title}");
                Console.WriteLine($"Summary: {summary}");
                if (!string.IsNullOrEmpty(text)) Console.WriteLine($"Text:    {text}");
            }
            Console.WriteLine("\n===============================");
            Console.WriteLine($"FINAL RESULT: {status} (Annotations: {annotations.Count})");
            Console.WriteLine("===============================");
        }

        if (outputFormat == "github")
        {
            foreach (var a in annotations)
            {
                var path  = PathHelper.NormaliseAnnotationPath(a.FilePath);
                var level = a.Level switch
                {
                    "failure" => "error",
                    "notice"  => "notice",
                    _         => "warning",
                };
                // GitHub treats file/line/col purely as annotation metadata and strips them from
                // the rendered ##[error] log line, so the location has to be repeated in the
                // title property and prefixed to the message. Without it the job log shows only
                // rule text and the author cannot tell which file to fix. BHoMBot got this for
                // free: the Checks API auto-titles each annotation "<path>#L<line>", whereas
                // workflow commands have no such default.
                // LineStart is 0 when the engine reported no line (see the "?? 0" fallbacks in
                // AnnotationConvert); "#L0" would point at nothing, so drop the suffix.
                var location = a.LineStart > 0 ? $"{path}#L{a.LineStart}" : path;
                // Message already contains the " - For more information see <url>" suffix. %0A is
                // the workflow-command newline escape, so the findings GroupErrors concatenated
                // for this file stay on separate lines instead of running together. '%' is
                // deliberately not escaped: the runner's unescape pass only reverses %25, %0D and
                // %0A, so the percent-encoded documentation URLs survive as-is.
                var msg   = a.Message.Replace("\r", "").Replace("\n", "%0A");
                var col   = a.ColumnStart > 0 ? $",col={a.ColumnStart}" : "";
                Console.WriteLine($"::{level} file={path},line={a.LineStart}{col},title={location}::{location}: {msg}");
            }
        }
        else if (outputFormat == "json")
        {
            var payload = new Dictionary<string, object>
            {
                ["status"]          = status.ToString(),
                ["checkType"]       = checkType,
                ["title"]           = title,
                ["summary"]         = summary,
                ["text"]            = text,
                ["annotationCount"] = annotations.Count,
                ["coverage"]        = accounting?.ToPayload() ?? new Dictionary<string, object>(),
                ["annotations"]     = annotations.Select(a => new Dictionary<string, object>
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
                    ["utcTime"]          = a.UTCTime.ToString("o")  // ISO 8601
                }).ToList()
            };
            Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = false }));
        }
        else if (outputFormat == "sarif" || outputFormat == "sarif-file")
        {
            var sarif = SarifBuilder.Build(checkType, title, annotations, toolUri);
            if (outputFormat == "sarif-file" && !string.IsNullOrEmpty(sarifFilePath))
            {
                File.WriteAllText(sarifFilePath, sarif);
                if (verbose) Console.WriteLine($"SARIF written to {sarifFilePath}");
            }
            else
                Console.WriteLine(sarif);
        }
    }
}
