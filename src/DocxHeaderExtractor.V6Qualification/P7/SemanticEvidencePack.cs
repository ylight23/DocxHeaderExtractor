using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.V6Qualification.P7;

/// <summary>
/// One deterministic request partition of the P7-F1Q packing experiment (moved here from DocumentProcessing: no production route uses it). Packing is an
/// execution concern: it does not change the coordinate profile, the response contract, or the
/// meaning assigned to any source atom.
/// </summary>
internal sealed record SemanticEvidencePack(
    string PackId,
    int Ordinal,
    IReadOnlyList<CanonicalSemanticSourceEvidence> Owned,
    IReadOnlyList<CanonicalSemanticSourceEvidence> Visible);

