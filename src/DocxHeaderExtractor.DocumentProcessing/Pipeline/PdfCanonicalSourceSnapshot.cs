using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// Compact canonical authority for the P6S qualification lane. It freezes only the atom/evidence
/// projection consumed by P05 and C#/R# issuing; PdfLine glyph maps, blocks and contexts remain
/// parser diagnostics rather than a second canonical source graph.
/// </summary>
internal sealed record PdfCanonicalSourceSnapshotV1(
    string SchemaVersion, string SourceSha256, string SourceAliasUniverseSha256,
    string ModelVisibleEvidenceSha256, IReadOnlyList<SemanticSourceAtom> Atoms,
    IReadOnlyList<PdfCanonicalEvidenceSnapshotV1> Evidence,
    IReadOnlyDictionary<string, string> LayoutBlockByAtom)
{
    public const string Version = "a99-pdf-canonical-source-snapshot-v2";

    public static PdfCanonicalSourceSnapshotV1 From(PdfStructuredSourceAuthority authority) => new(
        Version, authority.SourceSha256, authority.SourceAliasUniverseHash, authority.ModelVisibleEvidenceHash,
        authority.Atoms, authority.Evidence.Select(PdfCanonicalEvidenceSnapshotV1.From).ToArray(), authority.LayoutBlockByAtom);

    public PdfCandidateSourceAuthorityV1 Rehydrate()
    {
        if (SchemaVersion != Version || Atoms.Count == 0 || Evidence.Count != Atoms.Count ||
            Atoms.Select(a => a.Alias).Distinct(StringComparer.Ordinal).Count() != Atoms.Count)
            throw new InvalidOperationException("pdf-canonical-source-snapshot-invalid");
        var result = new PdfCandidateSourceAuthorityV1(SourceSha256, Atoms, Evidence.Select(item => item.Rehydrate()).ToArray(),
            new Dictionary<string, string>(LayoutBlockByAtom, StringComparer.Ordinal));
        if (result.SourceAliasUniverseSha256 != SourceAliasUniverseSha256 || result.ModelVisibleEvidenceSha256 != ModelVisibleEvidenceSha256)
            throw new InvalidOperationException("pdf-canonical-source-snapshot-hash-mismatch");
        return result;
    }
}

/// <summary>All materialized P05 evidence, but none of the parser's glyph/block object graph.</summary>
internal sealed record PdfCanonicalEvidenceSnapshotV1(
    string SourceAlias, string SourceId, int SourceOrdinal, string ExactSourceText, string StructuralScope,
    IReadOnlyList<string> ContainerFacts, JsonElement StyleFacts, JsonElement NumberingFacts,
    IReadOnlyList<string> ObservedEvidence, IReadOnlyList<string> LocalBefore, IReadOnlyList<string> LocalAfter,
    JsonElement? LocationFacts)
{
    public static PdfCanonicalEvidenceSnapshotV1 From(CanonicalSemanticSourceEvidence item) => new(
        item.SourceAlias, item.SourceId, item.SourceOrdinal, item.ExactSourceText, item.StructuralScope,
        item.ContainerFacts, Element(item.StyleFacts), Element(item.NumberingFacts), item.ObservedEvidence,
        item.LocalBefore, item.LocalAfter, item.LocationFacts is null ? null : Element(item.LocationFacts));

    public CanonicalSemanticSourceEvidence Rehydrate() => new(SourceAlias, SourceId, SourceOrdinal, ExactSourceText,
        StructuralScope, ContainerFacts, StyleFacts.Clone(), NumberingFacts.Clone(), [], ObservedEvidence, LocalBefore, LocalAfter)
    { LocationFacts = LocationFacts?.Clone() };

    private static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value);
}

/// <summary>Minimal rehydrated authority; deliberately not a replacement for live runtime authority.</summary>
internal sealed record PdfCandidateSourceAuthorityV1(string SourceSha256, IReadOnlyList<SemanticSourceAtom> Atoms,
    IReadOnlyList<CanonicalSemanticSourceEvidence> Evidence, IReadOnlyDictionary<string, string> LayoutBlockByAtom)
{
    public string SourceAliasUniverseSha256 => Hash(new { schemaVersion = "a99-pdf-segment-atom-universe-v1", rows = Atoms.Select(a => new { sourceAlias = a.Alias, sourceId = a.SourceId, ordinal = a.Ordinal, page = a.Page, row = a.Row, segment = a.Segment, text = a.Text }).ToArray() });
    public string ModelVisibleEvidenceSha256 => Hash(new { schemaVersion = "a99-pdf-model-visible-evidence-v2", rows = Evidence.Select(e => new { alias = e.SourceAlias, block = LayoutBlockByAtom.GetValueOrDefault(e.SourceId), text = e.ExactSourceText, owned = true, location = e.LocationFacts, style = e.StyleFacts, numbering = e.NumberingFacts }).ToArray() });
    private static string Hash(object value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))));
}
