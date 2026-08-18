using System;

/// <summary>
/// Path utilities for producing GitHub Actions annotation paths.
/// GitHub requires paths relative to the workspace root for annotations to attach
/// to PR diff lines. Roslyn and BHoM engines return absolute paths, so we strip
/// the GITHUB_WORKSPACE prefix before emitting ::error/::warning commands.
/// </summary>
public static class PathHelper
{
    public static string NormaliseAnnotationPath(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return "unknown";

        var path = filePath.Replace("\\", "/");

        var workspace = (Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") ?? "")
                            .Replace("\\", "/").TrimEnd('/');

        if (!string.IsNullOrEmpty(workspace) &&
            path.StartsWith(workspace, StringComparison.OrdinalIgnoreCase))
            path = path.Substring(workspace.Length).TrimStart('/');

        return path;
    }
}
