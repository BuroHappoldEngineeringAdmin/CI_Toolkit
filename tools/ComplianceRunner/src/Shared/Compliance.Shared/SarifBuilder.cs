using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

public static class SarifBuilder
{
    // toolUri is intentionally optional so unit tests can call Build() without a BHoM runtime
    // dependency. Production callers supply the real documentation URL from BH.Engine.Base.
    public static string Build(string checkType, string title, List<Annotation> annotations, string toolUri = "")
    {
        // Build a per-rule entry from distinct rule names so each check method appears
        // as its own rule in Code Scanning, with a helpUri linking to its BHoM docs page.
        var ruleMap = annotations
            .Where(a => !string.IsNullOrEmpty(a.RuleName))
            .GroupBy(a => a.RuleName)
            .ToDictionary(g => g.Key, g => g.First().DocumentationUrl);

        if (ruleMap.Count == 0)
            ruleMap[$"BHoM.{checkType}"] = "";

        var rulesArray = ruleMap.Select(kv =>
        {
            var rule = new Dictionary<string, object>
            {
                ["id"]               = kv.Key,
                ["shortDescription"] = new Dictionary<string, object> { ["text"] = title },
                ["fullDescription"]  = new Dictionary<string, object> { ["text"] = title }
            };
            if (!string.IsNullOrEmpty(kv.Value))
            {
                rule["helpUri"] = kv.Value;
                rule["help"]    = new Dictionary<string, object>
                {
                    ["text"]     = $"For more information see {kv.Value}",
                    ["markdown"] = $"[BHoM documentation]({kv.Value})"
                };
            }
            return (object)rule;
        }).ToArray();

        var results = new List<object>();
        foreach (var a in annotations)
        {
            var ruleId = string.IsNullOrEmpty(a.RuleName) ? $"BHoM.{checkType}" : a.RuleName;

            var region = new Dictionary<string, object>
            {
                ["startLine"] = a.LineStart > 0 ? a.LineStart : 1,
                ["endLine"]   = a.LineEnd   > 0 ? a.LineEnd   : 1
            };
            if (a.ColumnStart > 0) region["startColumn"] = a.ColumnStart;
            if (a.ColumnEnd   > 0) region["endColumn"]   = a.ColumnEnd;

            var props = new Dictionary<string, object>();
            if (!string.IsNullOrEmpty(a.BHoMGuid)) props["bhomGuid"] = a.BHoMGuid;
            if (a.UTCTime != default)               props["utcTime"]  = a.UTCTime.ToString("o");

            var result = new Dictionary<string, object>
            {
                ["ruleId"]    = ruleId,
                // SARIF severity vocabulary uses "note" rather than "notice" (per
                // SARIF 2.1.0 spec, section 3.27.10). Map accordingly.
                ["level"]     = a.Level switch
                {
                    "failure" => "error",
                    "notice"  => "note",
                    _         => "warning",
                },
                ["message"]   = new Dictionary<string, object> { ["text"] = a.Message },
                ["locations"] = new[]
                {
                    new Dictionary<string, object>
                    {
                        ["physicalLocation"] = new Dictionary<string, object>
                        {
                            ["artifactLocation"] = new Dictionary<string, object>
                            {
                                ["uri"]       = (a.FilePath ?? "").Replace("\\", "/"),
                                // %SRCROOT% is the SARIF-standard uriBaseId for the repository root;
                                // GitHub Code Scanning resolves relative artifact paths against it.
                                ["uriBaseId"] = "%SRCROOT%"
                            },
                            ["region"] = region
                        }
                    }
                }
            };

            if (props.Count > 0)
                result["properties"] = props;

            results.Add(result);
        }

        var sarif = new Dictionary<string, object>
        {
            ["$schema"] = "https://raw.githubusercontent.com/oasis-tcs/sarif-spec/master/Schemata/sarif-schema-2.1.0.json",
            ["version"] = "2.1.0",
            ["runs"]    = new[]
            {
                new Dictionary<string, object>
                {
                    ["tool"] = new Dictionary<string, object>
                    {
                        ["driver"] = BuildDriver(toolUri, rulesArray)
                    },
                    ["results"] = results
                }
            }
        };

        return JsonSerializer.Serialize(sarif, new JsonSerializerOptions { WriteIndented = true });
    }

    private static Dictionary<string, object> BuildDriver(string toolUri, object[] rulesArray)
    {
        var driver = new Dictionary<string, object>
        {
            ["name"]  = "BHoM Compliance Runner",
            ["rules"] = rulesArray
        };
        // Only emit informationUri when a real URL is supplied — an empty string is not valid SARIF.
        if (!string.IsNullOrEmpty(toolUri))
            driver["informationUri"] = toolUri;
        return driver;
    }
}
