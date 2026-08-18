/// <summary>Command-line argument parsing for compliance runners.</summary>
public static class ArgParser
{
    private static readonly HashSet<string> ValidOutputFormats =
        new(StringComparer.OrdinalIgnoreCase) { "console", "github", "json", "sarif" };

    private static readonly HashSet<string> ValidCheckTypes =
        new(StringComparer.OrdinalIgnoreCase) { "code", "copyright", "documentation", "project" };

    public sealed record ComplianceArgs(
        string        OutputFormat,
        string?       SarifFilePath,
        string?       CheckType,
        List<string>? Files,
        string        OrgUrl);

    public sealed record DatasetArgs(
        string        OutputFormat,
        string?       SarifFilePath,
        List<string>? Files);

    public static ComplianceArgs ParseCompliance(string[] args)
    {
        string  outputFormat  = "console";
        string? sarifFilePath = null;
        string  orgUrl        = "";
        var     rest          = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--output" && i + 1 < args.Length)
            {
                var fmt = args[++i].ToLowerInvariant();
                outputFormat = ValidOutputFormats.Contains(fmt) ? fmt : "console";
            }
            else if ((args[i] == "--sarif-file" || args[i] == "--sarif") && i + 1 < args.Length)
                sarifFilePath = args[++i];
            else if (args[i] == "--org-url" && i + 1 < args.Length)
                orgUrl = args[++i];
            else
                rest.Add(args[i]);
        }

        if (rest.Count < 2)
            return new ComplianceArgs(outputFormat, sarifFilePath, null, null, orgUrl);

        var checkType = rest[0].Trim().ToLowerInvariant();
        if (!ValidCheckTypes.Contains(checkType))
            return new ComplianceArgs(outputFormat, sarifFilePath, null, null, orgUrl);

        return new ComplianceArgs(outputFormat, sarifFilePath, checkType, rest.Skip(1).ToList(), orgUrl);
    }

    public static DatasetArgs ParseDataset(string[] args)
    {
        string  outputFormat  = "console";
        string? sarifFilePath = null;
        var     rest          = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--output" && i + 1 < args.Length)
            {
                var fmt = args[++i].ToLowerInvariant();
                outputFormat = ValidOutputFormats.Contains(fmt) ? fmt : "console";
            }
            else if ((args[i] == "--sarif-file" || args[i] == "--sarif") && i + 1 < args.Length)
                sarifFilePath = args[++i];
            else
                rest.Add(args[i]);
        }

        return rest.Count == 0
            ? new DatasetArgs(outputFormat, sarifFilePath, null)
            : new DatasetArgs(outputFormat, sarifFilePath, rest);
    }
}
