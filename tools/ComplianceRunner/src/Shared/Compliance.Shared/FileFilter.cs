/// <summary>File-extension and path filters for compliance runners.</summary>
public static class FileFilter
{
    /// <summary>Returns true when a file should be processed by the given compliance check type.</summary>
    public static bool IsRelevantFile(string file, string checkType)
    {
        if (checkType == "project")
        {
            var normalized = file.Replace("\\", "/");

            if (file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                // Exclude test projects and anything under a .ci/ directory.
                // These are internal tooling and are not subject to BHoM shipping conventions
                // (target framework, PostBuildEvent, AssemblyVersion, etc.).
                if (normalized.Contains("/.ci/", StringComparison.OrdinalIgnoreCase) ||
                    normalized.StartsWith(".ci/", StringComparison.OrdinalIgnoreCase))
                    return false;
                if (Path.GetFileName(file).EndsWith(".Tests.csproj", StringComparison.OrdinalIgnoreCase))
                    return false;
                return true;
            }

            return Path.GetFileName(file).Equals("AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase);
        }

        // code, copyright, documentation all operate on .cs files.
        return file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns true for .json files whose path contains "datasets" (case-insensitive),
    /// matching BHoMBot's DatasetCompliance filter exactly:
    ///   x.ToLower().Contains("datasets") &amp;&amp; x.EndsWith(".json")
    /// </summary>
    public static bool IsDatasetFile(string file)
    {
        if (!file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return false;

        return file.Contains("datasets", StringComparison.OrdinalIgnoreCase);
    }
}
