namespace DocxHeaderExtractor.Core.Models;

/// <summary>Explicit semantic disagreement retained for document-wide diagnostic census.</summary>
public sealed record CanonicalSemanticGlobalConflict(
    string ConflictId,
    IReadOnlyList<string> OccurrenceIds,
    IReadOnlyList<CanonicalSemanticProposal> Alternatives,
    IReadOnlyList<string> StructuralEvidence,
    IReadOnlyList<string> LocalContext)
{
    public string ConflictKind { get; init; } = "GLOBAL_SEMANTIC_CONFLICT";
    public IReadOnlyList<string> RelationEvidence { get; init; } = [];
}
