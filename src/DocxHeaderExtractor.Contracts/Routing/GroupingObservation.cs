namespace DocxHeaderExtractor.Contracts.Routing;

/// <summary>Actual stage observations, not inferred from failure-code text. Counts accumulate across repair/review passes.</summary>
public sealed record GroupingObservation(int ModelDividedRuns = 0, int FallbackRuns = 0, int DisputedRuns = 0,
    int VerificationChecks = 0, int VerifiedRuns = 0, int MembershipDisputes = 0, int BoundaryDisputes = 0,
    int UnresolvedAliases = 0, int IgnoredReviewChanges = 0, IReadOnlyList<GroupingBatchTiming>? Batches = null,
    IReadOnlyList<GroupingFieldDiagnostic>? FieldDiagnostics = null,
    IReadOnlyList<GroupingCoverageDiagnostic>? CoverageDiagnostics = null,
    IReadOnlyList<GroupingPartitionObservation>? Partitions = null,
    IReadOnlyList<GroupingVerifierEvidenceObservation>? VerifierEvidence = null,
    IReadOnlyList<BoundaryRepairObservation>? BoundaryRepairs = null,
    IReadOnlyList<GroupingBoundaryVerdictObservation>? BoundaryVerdicts = null)
{
    public GroupingObservation Add(GroupingObservation other) => new(ModelDividedRuns + other.ModelDividedRuns,
        FallbackRuns + other.FallbackRuns, DisputedRuns + other.DisputedRuns, VerificationChecks + other.VerificationChecks,
        VerifiedRuns + other.VerifiedRuns, MembershipDisputes + other.MembershipDisputes,
        BoundaryDisputes + other.BoundaryDisputes, UnresolvedAliases + other.UnresolvedAliases,
        IgnoredReviewChanges + other.IgnoredReviewChanges, (Batches ?? []).Concat(other.Batches ?? []).ToArray(),
        (FieldDiagnostics ?? []).Concat(other.FieldDiagnostics ?? []).ToArray(),
        (CoverageDiagnostics ?? []).Concat(other.CoverageDiagnostics ?? []).ToArray(),
        (Partitions ?? []).Concat(other.Partitions ?? []).ToArray(),
        (VerifierEvidence ?? []).Concat(other.VerifierEvidence ?? []).ToArray(),
        (BoundaryRepairs ?? []).Concat(other.BoundaryRepairs ?? []).ToArray(),
        (BoundaryVerdicts ?? []).Concat(other.BoundaryVerdicts ?? []).ToArray());
}

/// <summary>Alias-only boundary proposals. Applied is always false: suggestions have no repair authority.</summary>
public sealed record GroupingBoundaryVerdictObservation(string UnitId, int Attempt, int Round, string Run, int BoundaryRepairCycle,
    IReadOnlyList<string> CandidateAliases, string? Decision, IReadOnlyList<IReadOnlyList<string>>? CurrentGroups,
    IReadOnlyList<IReadOnlyList<string>>? SuggestedGroups, string? ProtocolError, bool Applied);

/// <summary>Actual verifier-input partition. Null comparison means no prior partition or a changed alias scope.</summary>
public sealed record GroupingPartitionObservation(string Phase, string UnitId, string CandidateId, string CandidateKey,
    int BoundaryRepairCycle, IReadOnlyList<string> CandidateAliases,
    IReadOnlyList<IReadOnlyList<string>>? InputGroups, IReadOnlyList<IReadOnlyList<string>> OutputGroups, bool? ChangedFromInput);

/// <summary>Repair invocation result before recursive verification; unchanged valid divisions remain informational.</summary>
public sealed record BoundaryRepairObservation(string Phase, string ParentUnitId, string CandidateId, string CandidateKey,
    IReadOnlyList<string> CandidateAliases, int FromCycle, int ToCycle,
    IReadOnlyList<IReadOnlyList<string>> InputGroups, IReadOnlyList<IReadOnlyList<string>> OutputGroups,
    bool? Changed, string Result, string? FailureClass);

/// <summary>Shared conversation scope, not evidence attribution to individual candidates. Aliases and registered tool names only.</summary>
public sealed record GroupingVerifierEvidenceObservation(string UnitId, int BoundaryRepairCycle,
    IReadOnlyList<IReadOnlyList<string>> CandidateAliases, IReadOnlyList<string> InitialObservedAliases,
    IReadOnlyList<GroupingEvidenceToolObservation> Requests, IReadOnlyList<string> ObservedAliases)
{
    public bool UsedScanCrop => Requests.Any(r => r.ScanCropReturned && r.SuppliedToVerifier);
}

public sealed record GroupingEvidenceToolObservation(int Round, string ToolName, string Status,
    IReadOnlyList<string> ReturnedAliases, bool ScanCropReturned, bool SuppliedToVerifier = false);

/// <summary>Wall-clock timing for one initial or review grouping request batch; offsets are relative to that Group invocation.</summary>
public sealed record GroupingBatchTiming(string Phase, string UnitId, int Runs, int Posts, int Repairs,
    double StartedAtSeconds, double CompletedAtSeconds, double ElapsedSeconds);

/// <summary>Protocol field names and types only; contains no provider completion or metadata values.</summary>
public sealed record GroupingFieldTypeIssue(string Field, string Expected, string Actual);

public sealed record GroupingFieldDiagnostic(string UnitId, int Attempt, int Round, string? Run, string Path,
    IReadOnlyList<string> Missing, IReadOnlyList<string> Unexpected, IReadOnlyList<GroupingFieldTypeIssue> InvalidTypes, bool ProtocolAccepted);

/// <summary>Observed membership identity/order, retained even when the strict coverage gate refuses a verdict.</summary>
public sealed record GroupingCoverageDiagnostic(string UnitId, int Attempt, int Round, string Run, int BoundaryRepairCycle,
    IReadOnlyList<string> ExpectedAliases, IReadOnlyList<string> ObservedAliases, IReadOnlyList<string> MissingAliases,
    IReadOnlyList<string> UnexpectedAliases, IReadOnlyList<string> DuplicateAliases, bool SequenceMatches);
