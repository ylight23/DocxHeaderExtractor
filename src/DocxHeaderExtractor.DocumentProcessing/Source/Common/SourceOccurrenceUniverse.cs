using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.DocumentProcessing.Source.Common;

/// <summary>
/// Immutable source IR shared by format adapters and heading authority. Parser objects stay in
/// format-owned adapter results; this record carries only source-derived identities and evidence.
/// </summary>
internal sealed record SourceOccurrenceUniverse(
    IReadOnlyList<SemanticSourceAtom> Atoms,
    IReadOnlyList<SourceOccurrence> Occurrences,
    IReadOnlyList<CanonicalSemanticSourceEvidence> Evidence,
    string SourceAliasUniverseHash,
    string ModelVisibleEvidenceHash,
    string SourceSha256,
    IReadOnlyDictionary<string, HeadingSourceContext> HeadingContexts,
    DocumentSourceCatalog Catalog,
    IReadOnlyList<SemanticSourceAlias> Aliases,
    IReadOnlyDictionary<string, int> OrdinalBySourceId)
{
    public string SourceKind { get; init; } = "unknown";

    /// <summary>Caller-visible identity of the source document, used to label requests.</summary>
    public string DocumentId { get; init; } = string.Empty;

    /// <summary>The live route identity checked before any qualified request is sent.</summary>
    public string SourceUniverseSha256 => SourceAliasUniverseHash;
}
