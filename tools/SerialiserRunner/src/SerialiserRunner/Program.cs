using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using SerialiserRunner.Commands;
using SerialiserRunner.Models;

if (args.Length == 0 || args[0] is "--help" or "-h")
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  SerialiserRunner run --assemblies <path> --output <file>");
    Console.WriteLine("  SerialiserRunner compare --baseline <file> --branch <file> [--implausible-baseline-ratio <0..1>]");
    return 1;
}

return args[0] switch
{
    "run"     => HandleRun(args[1..]),
    "compare" => HandleCompare(args[1..]),
    _         => Fail($"Unknown command: {args[0]}")
};

static int HandleRun(string[] args)
{
    string assemblies = GetArg(args, "--assemblies") ?? @"C:\ProgramData\BHoM\Assemblies";
    string output     = GetArg(args, "--output") ?? throw new ArgumentException("--output is required");
    return RunCommand.Execute(assemblies, output);
}

static int HandleCompare(string[] args)
{
    string baselinePath = GetArg(args, "--baseline") ?? throw new ArgumentException("--baseline is required");
    string branchPath   = GetArg(args, "--branch")   ?? throw new ArgumentException("--branch is required");

    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
    SerialiserResult baseline = JsonSerializer.Deserialize<SerialiserResult>(File.ReadAllText(baselinePath), options)
                                ?? throw new InvalidOperationException("Failed to deserialize baseline result.");
    SerialiserResult branch   = JsonSerializer.Deserialize<SerialiserResult>(File.ReadAllText(branchPath), options)
                                ?? throw new InvalidOperationException("Failed to deserialize branch result.");

    string? ratioArg = GetArg(args, "--implausible-baseline-ratio");
    double ratio = CompareCommand.DefaultImplausibleBaselineRatio;
    if (!string.IsNullOrWhiteSpace(ratioArg)
        && !double.TryParse(ratioArg, NumberStyles.Float, CultureInfo.InvariantCulture, out ratio))
    {
        Console.Error.WriteLine($"--implausible-baseline-ratio '{ratioArg}' is not a number.");
        return 1;
    }

    CompareResult result = CompareCommand.Compare(baseline, branch, ratio);
    Console.WriteLine(result.Summary);

    if (result.IsBaselineUnusable)
    {
        Console.WriteLine("::error title=Serialisation::Serialisation baseline unusable. The runner could not " +
                          "serialise the base branch either, so no comparison is possible. This is a CI runner " +
                          "failure and is not caused by this pull request.");
        return 2;
    }

    if (result.IsRegression)
    {
        Console.WriteLine("::error title=Serialisation::This PR introduces serialisation regressions. " +
                          "Affected types are listed above. Saved scripts using these types may fail to load.");
        return 1;
    }

    Console.WriteLine("::notice title=Serialisation::No serialisation regressions detected.");
    return 0;
}

static string? GetArg(string[] args, string flag)
{
    int i = Array.IndexOf(args, flag);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}
