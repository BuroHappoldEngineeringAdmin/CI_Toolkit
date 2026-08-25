using System.Reflection;
using System.Runtime.Loader;
using VersioningRunner.Commands;

// BHoM types reference System.Drawing.Common with the 4.0.0.1 identity (the
// .NET Framework / netstandard ref). On .NET 8 the System.Drawing.Common
// PackageReference in this project supplies 8.x at runtime; the resolver below
// remaps any 4.0.0.1 request to the already-loaded modern version so reflection
// over BHoM types whose object graph touches Bitmap (or related) succeeds.
AssemblyLoadContext.Default.Resolving += static (context, name) =>
{
    if (!string.Equals(name.Name, "System.Drawing.Common", StringComparison.Ordinal))
        return null;

    var loaded = AssemblyLoadContext.Default.Assemblies
        .FirstOrDefault(a => string.Equals(a.GetName().Name, "System.Drawing.Common", StringComparison.Ordinal));
    if (loaded is not null)
        return loaded;

    try
    {
        return context.LoadFromAssemblyName(new AssemblyName("System.Drawing.Common"));
    }
    catch
    {
        return null;
    }
};

if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("Usage: VersioningRunner [--assemblies <path>] [--output <file>] [--test-all] [--subject-assembly-list <file>] [--configuration <name>] [--version-conditional <file>]");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --assemblies <path>          BHoM assemblies folder (default: C:\\ProgramData\\BHoM\\Assemblies)");
    Console.WriteLine("  --output <file>              Write JSON results to this file");
    Console.WriteLine("  --test-all                   Test all historical dataset versions (default: previous version only)");
    Console.WriteLine("  --configuration <name>       Build configuration this run compiled, recorded per finding.");
    Console.WriteLine("  --version-conditional <file> File of fully-qualified method names whose signature is version-");
    Console.WriteLine("                               conditional in the subject's source, one per line. A present file");
    Console.WriteLine("                               means the scan ran, so unlisted methods record No. Omit the flag");
    Console.WriteLine("                               only when no scan was performed: findings then record Unknown.");
    Console.WriteLine("  --subject-assembly-list <file>  File listing the assemblies this repo's own build staged,");
    Console.WriteLine("                               one name per line. Failures are attributed only to namespaces");
    Console.WriteLine("                               those assemblies declare (default: the whole closure).");
    return 1;
}

string assemblies = GetArg(args, "--assemblies") ?? @"C:\ProgramData\BHoM\Assemblies";
string? output = GetArg(args, "--output");
bool testAll = args.Contains("--test-all");
string? subject = GetArg(args, "--subject-assembly-list");

string? configuration = GetArg(args, "--configuration");
string? vcFile = GetArg(args, "--version-conditional");

// A present file means the scan ran, so No is assertable even when it lists nothing.
// An absent file means the scan did not run, so everything stays Unknown.
//
// The distinction matters for most of the fleet, not an edge case: 75 of the 92 alpha and
// beta repos are non-Revit and will legitimately produce an empty list. Treating empty as
// "no evidence" would leave every versioning divergence on those repos unscored, and the
// gate in CI_Toolkit#173 could never be satisfied for them.
IReadOnlyCollection<string>? versionConditional = null;
if (vcFile is not null && File.Exists(vcFile))
{
    versionConditional = File.ReadAllLines(vcFile)
        .Select(l => l.Trim())
        .Where(l => l.Length > 0 && !l.StartsWith('#'))
        .ToArray();
}

return RunCommand.Execute(assemblies, output, testAll, subject, configuration, versionConditional);

static string? GetArg(string[] args, string flag)
{
    int i = Array.IndexOf(args, flag);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
