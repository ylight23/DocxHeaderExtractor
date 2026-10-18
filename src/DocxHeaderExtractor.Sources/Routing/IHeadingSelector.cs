using DocxHeaderExtractor.DocumentProcessing.Authority;

namespace DocxHeaderExtractor.DocumentProcessing.Routing;

/// <summary>
/// The PDF heading authority's model side: reads one PDF and answers which source lines are headings, by the source alias the PDF parser
/// gives each line (the alias of <c>DocumentSourceSnapshot</c> atoms). Hierarchy, binding and materialization stay in the shared stages.
/// <para>
/// The PDF lane's only authority since the V6 promotion (2026-10-14). The implementation lives outside this assembly (the V6 direct agent,
/// qualified as the D4.12 method), so the lane depends on this seam, not on the agent.
/// </para>
/// </summary>
public interface IHeadingSelector
{
    /// <summary>Identifies the method in the route audit, for example the agent's arm and instruction.</summary>
    string SelectorId { get; }

    /// <param name="pdfPath">The uploaded PDF; the selector parses it with the same parser as the lane, so the aliases agree.</param>
    /// <param name="apiKey">The provider key of the authorized PDF route.</param>
    Task<HeadingSelection> SelectAsync(string pdfPath, string apiKey, CancellationToken ct);
}

/// <summary>What a selector decided for one PDF.</summary>
/// <param name="SelectedAliases">Lines selected as headings. A heading that runs over several lines has each of its lines here.</param>
/// <param name="UnitsComplete">Units (segments) whose answer passed the contract; lines of other units have no decision.</param>
/// <param name="UnitsTotal">Units the document was cut into.</param>
/// <param name="UnitFailures">The failure code of each unit that did not complete.</param>
/// <param name="ModelAnswers">One JSON object per complete unit with its decisions and the model's reasons and analysis (the audit's analyst
/// responses). The provider's raw bytes are not kept.</param>
/// <param name="ProviderCalls">POSTs sent.</param>
/// <param name="ProviderCostUsd">Cost the provider reported.</param>
/// <param name="Placement">Optional measurements for the shared placement pass (parent relations); null sends the placement packet as it was.</param>
/// <param name="Groups">Optional: the selected lines divided into headings, in document order (each group is the lines of one heading). Null makes
/// the authority group by its own layout rule: consecutive lines of the same page and parser layout block.</param>
/// <param name="HeadingExtents">Optional: for a selected line of which only a part is heading text (a DOCX paragraph that opens with its heading
/// and continues with body text), that part as a UTF-16 span of the line's text, by alias. The selector checked that the span's text is the
/// line's text at those offsets; a selected line without an entry is heading text from end to end.</param>
public sealed record HeadingSelection(
    IReadOnlySet<string> SelectedAliases,
    int UnitsComplete,
    int UnitsTotal,
    IReadOnlyList<string> UnitFailures,
    IReadOnlyList<string> ModelAnswers,
    int ProviderCalls,
    double ProviderCostUsd,
    HeadingPlacementContext? Placement = null,
    IReadOnlyList<IReadOnlyList<string>>? Groups = null,
    IReadOnlyDictionary<string, TextOffsetSpan>? HeadingExtents = null)
{
    /// <summary>Optional, scanned sources only: the reading a selected line was taken in (a view id of the scan's evidence), by alias. Its extent,
    /// when there is one, is measured on that reading. A selected line without an entry is taken in its primary reading.</summary>
    public IReadOnlyDictionary<string, string>? HeadingReadings { get; init; }
    public GroupingObservation? Grouping { get; init; }
    /// <summary>Wall-clock seconds of consecutive heading phases; verification/review include their dependent repairs.</summary>
    public IReadOnlyDictionary<string, double>? StageSeconds { get; init; }
    public IReadOnlyDictionary<string, HeadingAliasState>? AliasStates { get; init; }
}

/// <summary>Per-alias state immediately before binding. Unknown membership is never rewritten as NOT_SELECT.</summary>
public sealed record HeadingAliasState(string InitialSelection, string FinalSelection, string? VerificationMembership,
    string BindingStatus)
{
    public IReadOnlyList<HeadingAliasProvenance> Provenance { get; init; } = [];
    public string ProvenanceAvailability => Provenance.Count == 0 ? "not-recorded" : "recorded";
}

/// <summary>Producer facts for one alias: no source text, model reason text or unvalidated semantic overrides.</summary>
public sealed record HeadingAliasProvenance(string Alias, string Stage, string UnitId, string? CandidateId,
    string? Decision, string? Membership, IReadOnlyList<string> FailureCodes, IReadOnlyList<string> EvidenceRefs)
{
    public int? Round { get; init; }
    public int? ReasonLength { get; init; }
    public string? UnitFailureContext { get; init; }
    public int BoundaryRepairCycle { get; init; }
}

/// <summary>Actual stage observations, not inferred from failure-code text. Counts accumulate across repair/review passes.</summary>
public sealed record GroupingObservation(int ModelDividedRuns = 0, int FallbackRuns = 0, int DisputedRuns = 0,
    int VerificationChecks = 0, int VerifiedRuns = 0, int MembershipDisputes = 0, int BoundaryDisputes = 0,
    int UnresolvedAliases = 0, int IgnoredReviewChanges = 0, IReadOnlyList<GroupingBatchTiming>? Batches = null,
    IReadOnlyList<GroupingFieldDiagnostic>? FieldDiagnostics = null,
    IReadOnlyList<GroupingCoverageDiagnostic>? CoverageDiagnostics = null,
    IReadOnlyList<GroupingPartitionObservation>? Partitions = null,
    IReadOnlyList<GroupingVerifierEvidenceObservation>? VerifierEvidence = null)
{
    public GroupingObservation Add(GroupingObservation other) => new(ModelDividedRuns + other.ModelDividedRuns,
        FallbackRuns + other.FallbackRuns, DisputedRuns + other.DisputedRuns, VerificationChecks + other.VerificationChecks,
        VerifiedRuns + other.VerifiedRuns, MembershipDisputes + other.MembershipDisputes,
        BoundaryDisputes + other.BoundaryDisputes, UnresolvedAliases + other.UnresolvedAliases,
        IgnoredReviewChanges + other.IgnoredReviewChanges, (Batches ?? []).Concat(other.Batches ?? []).ToArray(),
        (FieldDiagnostics ?? []).Concat(other.FieldDiagnostics ?? []).ToArray(),
        (CoverageDiagnostics ?? []).Concat(other.CoverageDiagnostics ?? []).ToArray(),
        (Partitions ?? []).Concat(other.Partitions ?? []).ToArray(),
        (VerifierEvidence ?? []).Concat(other.VerifierEvidence ?? []).ToArray());
}

/// <summary>Actual verifier-input partition. Null comparison means no prior partition or a changed alias scope.</summary>
public sealed record GroupingPartitionObservation(string Phase, string UnitId, string CandidateId, string CandidateKey,
    int BoundaryRepairCycle, IReadOnlyList<string> CandidateAliases,
    IReadOnlyList<IReadOnlyList<string>>? InputGroups, IReadOnlyList<IReadOnlyList<string>> OutputGroups, bool? ChangedFromInput);

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

/// <summary>
/// Measurements of the document handed to the placement pass: the style table of the whole document and the style of each line (JSON objects as
/// text, keyed by line alias). Facts only; the placement model decides every relation.
/// </summary>
/// <param name="MeasurementsNote">The sentence that tells the placement model what the style objects hold, when the document is not a PDF (the
/// default sentence describes a PDF's size, weight and rank). Null keeps the default.</param>
public sealed record HeadingPlacementContext(string DocumentStylesJson, IReadOnlyDictionary<string, string> StyleJsonByAlias, string? MeasurementsNote = null);
