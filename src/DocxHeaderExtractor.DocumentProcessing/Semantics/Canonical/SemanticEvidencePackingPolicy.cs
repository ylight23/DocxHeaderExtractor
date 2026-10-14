using System.Text.Json;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.DocumentProcessing.Semantics.Canonical;

/// <summary>
/// One deterministic request partition produced before semantic inference. Packing is an
/// execution concern: it does not change the coordinate profile, the response contract, or the
/// meaning assigned to any source atom.
/// </summary>
internal sealed record SemanticEvidencePack(
    string PackId,
    int Ordinal,
    IReadOnlyList<CanonicalSemanticSourceEvidence> Owned,
    IReadOnlyList<CanonicalSemanticSourceEvidence> Visible);

