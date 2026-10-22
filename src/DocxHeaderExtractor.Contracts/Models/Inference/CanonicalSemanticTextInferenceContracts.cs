namespace DocxHeaderExtractor.Contracts.Models.Inference;

/// <summary>Compact parser-owned evidence attached to one canonical source occurrence.</summary>
public sealed record CanonicalSemanticSourceEvidence(
    string SourceAlias,
    string SourceId,
    int SourceOrdinal,
    string ExactSourceText,
    string StructuralScope,
    IReadOnlyList<string> ContainerFacts,
    object StyleFacts,
    object NumberingFacts,
    IReadOnlyList<object> RunFormattingFacts,
    IReadOnlyList<string> ObservedEvidence,
    IReadOnlyList<string> LocalBefore,
    IReadOnlyList<string> LocalAfter)
{
    /// <summary>Observable physical placement facts; never a semantic interpretation.</summary>
    public object? LocationFacts { get; init; }
}

