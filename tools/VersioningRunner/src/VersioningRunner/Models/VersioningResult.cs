namespace VersioningRunner.Models;

public enum VersioningStatus { Pass, Warning, Error }

public record FailureInfo(string Description, string Message);

// Which decision counted a failure as real or as unverified. Diagnostic only: no
// behaviour depends on it. It exists because "real" is reachable by four different
// routes that imply different fixes, and a log that only prints the total cannot
// tell them apart. See the classification block in RunCommand.CollectLeafFailures.
public enum ClassificationPath
{
    // An event named a type outside BHoM that failed to deserialise, so the failure
    // could not be verified. Unverified.
    UnresolvableFromEvents,
    // No parsable "Method <name> from { ... }" event, so the signature probe could
    // not be addressed and never ran. Real by default.
    NoMethodEvent,
    // The probe ran, no loaded assembly returned the declaring type, and there was no
    // loaded assembly to ask for the reason: either the event named none, or the one it
    // named is not in the closure. Real by default, which fails safe.
    DeclaringTypeNotLoaded,
    // The declaring assembly was asked directly and reported a missing dependency outside
    // BHoM, so the type exists but cannot be materialised here. Unverified.
    DeclaringTypeUnloadable,
    // The declaring assembly was asked directly and reported the type itself as absent, so
    // it really is gone. Real, and this is the case versioning exists to catch.
    DeclaringTypeAbsent,
    // The declaring type resolved but carries no method of that name. Real, and
    // documented as a genuine finding, which is only sound if the type is complete.
    NoOverloadFound,
    // The declaring type and every signature type resolved. Genuinely real.
    SignatureResolved,
    // A signature type failed to resolve and the blocker is outside BHoM. Unverified.
    SignatureBlockerOutsideBHoM,
    // A signature type failed to resolve but the blocker is a BH. type, so it is not
    // attributed to infrastructure. Real.
    SignatureBlockerInsideBHoM,
    // No probe was supplied by the caller, which happens only in unit tests.
    ProbeNotSupplied,
}

// Whether the failing method's signature is version-conditional in the subject's source.
//
// Three states, deliberately. An empty grep result is not the same as "not
// version-conditional": the first means we do not know, the second is a measurement.
// Conflating them would let the register mark a finding explained on no evidence.
public enum VersionConditionalState
{
    // No list was supplied, so the source was never inspected. Not evidence either way.
    Unknown,
    // A list was supplied and this method is in it.
    Yes,
    // A list was supplied and this method is not in it.
    No,
}

// One row per attributed failure, recording how it was classified rather than only
// what it was. Assembly comes from the Method event, which the label parser discards.
public record FailureDiagnostic(
    string Label,
    bool CountedAsReal,
    ClassificationPath Path,
    string? Cause,
    string? DeclaringType,
    string? DeclaringAssembly,
    int EventCount,
    // Every assembly in the loaded set that yielded the declaring type. More than one
    // means the classification depended on enumeration order (see CI_Toolkit#161).
    IReadOnlyList<string>? DeclaringTypeCandidates = null,
    // Build configuration this run compiled, carried per row so a finding read on its
    // own is interpretable. Human-legible; it does not by itself explain a divergence.
    string? Configuration = null,
    VersionConditionalState VersionConditional = VersionConditionalState.Unknown);

// Coverage denominator. A verdict without one cannot be interpreted: a pass over zero
// methods reads identically to a pass over seven thousand. BHoMBot reported object
// types, methods, datasets and adapters on failure only; this records them either way.
public record CoverageCounts(
    int LoadedAssemblies,
    // Number of FromJsonDatasets entry points invoked, normally one per repository. This is
    // not a measure of how much was verified and must not be read as one: the surface is
    // SubjectTypes. Named for what it counts so a reader does not have to know.
    int VerifyEntryPoints,
    int SubjectAssemblies,
    int SubjectTypes,
    int DatasetVersions);

public class VersioningResult
{
    public VersioningStatus Status { get; init; } = VersioningStatus.Pass;
    public int FailureCount { get; init; }
    public CoverageCounts? Coverage { get; init; }
    public string? Configuration { get; init; }
    public List<FailureInfo> Failures { get; init; } = [];
    public List<FailureDiagnostic> Diagnostics { get; init; } = [];
}
