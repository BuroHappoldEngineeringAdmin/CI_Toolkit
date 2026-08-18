using System.Diagnostics;
using System.Reflection;

/// <summary>
/// Helpers for E2E integration tests that run the compiled runner executables.
///
/// Prerequisites:
///   - BHoM must be installed at $(ProgramData)\BHoM\Assemblies\
///   - The solution must be built before running these tests (dotnet build).
///     The solution is built automatically when running via the test project,
///     but "dotnet run --no-build" is used to avoid redundant rebuilds during test execution.
/// </summary>
internal static class RunnerFixture
{
    private static readonly string RepoRoot = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);

        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Platform.slnx")))
            dir = dir.Parent;

        return dir?.FullName
            ?? throw new InvalidOperationException(
                "Cannot locate repo root — Platform.slnx not found in any ancestor directory.");
    }

    /// <summary>
    /// Runs a compliance runner via "dotnet run --no-build" and returns its exit code and stdout.
    /// </summary>
    /// <param name="runner">Project folder name, e.g. "ComplianceRunner" or "DatasetComplianceRunner".</param>
    /// <param name="args">Arguments forwarded to the runner after "--".</param>
    public static (int ExitCode, string Stdout) Run(string runner, params string[] args)
    {
        var projectPath = Path.Combine(RepoRoot, "src", runner, $"{runner}.csproj");
        using var proc = new Process();

        proc.StartInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };

        // Build the argument list: dotnet run --no-build --project <path> -- <runner args>
        foreach (var a in new[] { "run", "--no-build", "--project", projectPath, "--" }.Concat(args))
            proc.StartInfo.ArgumentList.Add(a);

        proc.Start();
        var stdout = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();

        return (proc.ExitCode, stdout);
    }
}
