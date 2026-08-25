using System.Collections.Generic;

/// <summary>
/// Counts what a compliance runner did with the files it was handed, and renders that as a
/// coverage denominator.
///
/// Both runners walk the same four-exit loop: a file is dropped by the filter, dropped because
/// it is not on disk, dropped because the engine returned no result, or examined. Without a
/// count of those, a run that examined every file and a run that examined none produce the same
/// output, so a green check carries no evidence that anything was checked.
///
/// Deliberately free of BHoM types and of any dependency on either runner, so it can be
/// compiled directly into the hermetic test project and its arithmetic and wording tested
/// without a BHoM install. It holds counters and formats strings; it decides nothing.
///
/// The drop reasons are counted but not sub-classified. A file dropped by the filter may have
/// been excluded on purpose — project compliance deliberately skips test projects and anything
/// under .ci/ — or may be a disagreement between the pathspec that selected it and the filter
/// that rejected it. Telling those apart would require the filter to explain itself, which is a
/// change to a class both runners share. The denominator's job is to make the ratio visible;
/// diagnosing it is a separate question.
/// </summary>
public sealed class FileAccounting
{
    /// <summary>Files handed to the runner on the command line.</summary>
    public int HandedIn { get; private set; }

    /// <summary>Dropped because the check's own filter did not consider them relevant.</summary>
    public int NotRelevant { get; private set; }

    /// <summary>Dropped because the path did not exist on disk.</summary>
    public int NotOnDisk { get; private set; }

    /// <summary>Dropped because the compliance engine returned no result for them.</summary>
    public int NoResult { get; private set; }

    /// <summary>Files the runner actually examined and merged into its verdict.</summary>
    public int Examined { get; private set; }

    public FileAccounting(int handedIn) => HandedIn = handedIn;

    public void CountNotRelevant() => NotRelevant++;
    public void CountNotOnDisk()   => NotOnDisk++;
    public void CountNoResult()    => NoResult++;
    public void CountExamined()    => Examined++;

    /// <summary>
    /// True when files were supplied and none of them was examined. The calling workflow exits
    /// before invoking a runner when it has no files at all, so this is the anomalous case
    /// rather than the empty one: something selected these files and the runner used none.
    /// </summary>
    public bool ExaminedNothing => HandedIn > 0 && Examined == 0;

    /// <summary>
    /// The coverage line. Leads with what was examined rather than with what was handed in:
    /// the whole point is to make a green interpretable, and a count of inputs does not.
    /// </summary>
    public string CoverageLine() =>
        $"Coverage: {Examined} of {HandedIn} file(s) examined; {DropSummary()}.";

    /// <summary>
    /// The warning text for a run that examined nothing. Carries the breakdown rather than the
    /// bare fact, because the breakdown is what distinguishes a pull request that changed
    /// nothing relevant from a selection layer handing over files this check will never accept.
    /// </summary>
    public string ExaminedNothingWarning() =>
        $"{HandedIn} file(s) were handed to this check and none was examined: {DropSummary()}. "
      + "The check reports success without having inspected anything. If the files were dropped "
      + "as not relevant, the pattern that selected them and the filter that rejected them "
      + "disagree.";

    private string DropSummary()
    {
        var parts = new List<string>();
        if (NotRelevant > 0) parts.Add($"{NotRelevant} not relevant to this check");
        if (NotOnDisk   > 0) parts.Add($"{NotOnDisk} not found on disk");
        if (NoResult    > 0) parts.Add($"{NoResult} returned no result");
        return parts.Count == 0 ? "none dropped" : string.Join(", ", parts);
    }

    /// <summary>Shape for machine-readable output, so consumers get counts structurally.</summary>
    public Dictionary<string, object> ToPayload() => new()
    {
        ["handedIn"]    = HandedIn,
        ["examined"]    = Examined,
        ["notRelevant"] = NotRelevant,
        ["notOnDisk"]   = NotOnDisk,
        ["noResult"]    = NoResult,
    };
}
