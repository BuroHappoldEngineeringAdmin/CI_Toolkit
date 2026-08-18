namespace SerialiserRunner.Models;

public enum TestStatus { Pass, Warning, Error }

public record FailureInfo
{
    public string Description { get; init; } = "";
    public string Message { get; init; } = "";
    public List<string> Events { get; init; } = [];
    public bool IsPotentialCascade { get; init; }
    public List<string> SuspectedRootCauses { get; init; } = [];
}

// One entry per Verify method invoked. Population and FailureCount on the result are single
// totals, which cannot say which leg moved: when a Revit subject's own assemblies were loaded
// and the population fell from 6368 to 5395, the drop had to be attributed to the Objects leg
// by argument rather than measured, because no breakdown existed.
//
// Status is the raw string the leg reported, not TestStatus, because a leg can also have thrown
// or returned null and neither is a TestStatus value. Inventing enum members for those would
// imply they are serialisation outcomes; they are not, they are the absence of one.
public record LegResult
{
    public string Name { get; init; } = "";
    public string Status { get; init; } = "";
    public int Population { get; init; }
    public int FailureCount { get; init; }
}

public record SerialiserResult
{
    public TestStatus Status { get; init; }
    public int FailureCount { get; init; }
    public int Population { get; init; }
    public List<FailureInfo> Failures { get; init; } = [];

    // Per-leg breakdown of Population and FailureCount. Each leg's figures come from the same
    // values that accumulate into the totals above, so the two cannot drift apart.
    public List<LegResult> Legs { get; init; } = [];

    // File names of everything LoadAssemblies actually loaded, sorted, so branch and
    // baseline sets can be diffed directly from the uploaded artifacts. Population is a
    // single number and cannot say which assemblies produced it, which left the observed
    // gap against BHoMBot's totals undiagnosable.
    public List<string> LoadedAssemblies { get; init; } = [];
}
