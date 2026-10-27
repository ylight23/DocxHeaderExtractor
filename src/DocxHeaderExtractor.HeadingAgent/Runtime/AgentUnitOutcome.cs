using static DocxHeaderExtractor.HeadingAgent.Protocol.AgentProtocol;

namespace DocxHeaderExtractor.HeadingAgent.Runtime;

/// <summary>What one unit (one segment) ended with.</summary>
/// <param name="Extents">The headingExtent of each SELECT that gave one (DOCX), checked against the target's text.</param>
/// <param name="TargetsWithoutFacts">Core facts coverage at the end of the unit: targets whose own core facts no successful facts call returned.</param>
/// <param name="FactsResultsCut">Successful facts calls whose result was cut.</param>
/// <param name="DetailEvidenceAliases">Detail evidence coverage: the aliases the model chose to read through the other tools. Not required of any target.</param>
internal sealed record AgentUnitOutcome(string UnitId, string Status, string? Failure, IReadOnlyDictionary<string, string> Decisions, int Posts,
    int ToolCallsEmitted, int ToolCallsSucceeded, bool FenceUnwrapped, string? Analysis, IReadOnlyDictionary<string, string>? Reasons,
    string? RepairedFrom, int RepairsUsed, int TransportRetries, IReadOnlyList<string> RequestSha256s,
    IReadOnlyDictionary<string, HeadingExtent>? Extents = null, IReadOnlyList<string>? TargetsWithoutFacts = null, int FactsResultsCut = 0,
    IReadOnlyList<string>? DetailEvidenceAliases = null, IReadOnlyDictionary<string, string>? ReadingChoices = null,
    int ToolRounds = 0, bool DecidedBeforeTools = false)
{
    /// <summary>The outcome was read back from the selection cache: no call was made for it in this run.</summary>
    public bool Reused { get; init; }
    public IReadOnlyList<IReadOnlyList<string>>? SelectionGroups { get; init; }
    public string? GroupingFailure { get; init; }
}
