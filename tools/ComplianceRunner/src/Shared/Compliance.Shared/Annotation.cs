using System;

/// <summary>Lightweight record carrying everything needed for any output format.</summary>
public class Annotation
{
    public string   FilePath         { get; set; } = "";
    public int      LineStart        { get; set; }
    public int      LineEnd          { get; set; }
    public int      ColumnStart      { get; set; }
    public int      ColumnEnd        { get; set; }
    /// <summary>"failure" (Error) or "warning" — matches GitHub's expected values.</summary>
    public string   Level            { get; set; } = "warning";
    /// <summary>
    /// Full message including the " - For more information see &lt;url&gt;" suffix,
    /// exactly matching BHoMBot's FullMessage() output.
    /// </summary>
    public string   Message          { get; set; } = "";
    /// <summary>Check method name (e.g. "HasValidCopyright") — used as the SARIF ruleId.</summary>
    public string   RuleName         { get; set; } = "";
    /// <summary>Fully-qualified documentation URL built via BH.Engine.Base.Query.DocumentationURL.</summary>
    public string   DocumentationUrl { get; set; } = "";
    /// <summary>BHoM_Guid from the engine's Error — unique identifier per finding for cross-run tracing.</summary>
    public string   BHoMGuid         { get; set; } = "";
    /// <summary>UTC timestamp at which the engine produced this finding.</summary>
    public DateTime UTCTime          { get; set; }
}
