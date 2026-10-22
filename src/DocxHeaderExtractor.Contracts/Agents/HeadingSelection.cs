using DocxHeaderExtractor.Contracts.Models;

namespace DocxHeaderExtractor.Contracts.Agents;

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
    /// <summary>Experiment counters: units that called tools, the tool-calling replies, and units whose tool-calling reply already carried decisions.</summary>
    public ToolUseStats? ToolUse { get; init; }
}

public sealed record ToolUseStats(int UnitsWithToolRounds, int ToolRounds, int UnitsDecidedBeforeTools);

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

/// <summary>
/// Measurements of the document handed to the placement pass: the style table of the whole document and the style of each line (JSON objects as
/// text, keyed by line alias). Facts only; the placement model decides every relation.
/// </summary>
/// <param name="MeasurementsNote">The sentence that tells the placement model what the style objects hold, when the document is not a PDF (the
/// default sentence describes a PDF's size, weight and rank). Null keeps the default.</param>
public sealed record HeadingPlacementContext(string DocumentStylesJson, IReadOnlyDictionary<string, string> StyleJsonByAlias, string? MeasurementsNote = null);
