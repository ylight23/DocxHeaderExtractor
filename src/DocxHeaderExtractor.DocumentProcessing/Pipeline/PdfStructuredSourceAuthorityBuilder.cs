using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>One deterministic request the provider would receive, built but never sent.</summary>
internal sealed record PdfStructuredContextPack(
    int Index,
    IReadOnlyList<string> OwnedAliases,
    IReadOnlyList<string> VisibleAliases,
    string RequestPayload,
    string RequestSha256);

/// <summary>
/// What the model would actually be shown, and four hashes that pin it.
/// <para>
/// The alias universe and the evidence are separate authorities on purpose. A migration can leave
/// every alias, its text and its order untouched while changing the facts attached to each one, the
/// context around it, or how it is grouped into requests - and a preflight that only checked the
/// universe would pass while the model read something else entirely. That gap is the reason this
/// type exists: <see cref="SourceAliasUniverseHash"/> covers what a coordinate is,
/// <see cref="ModelVisibleEvidenceHash"/> covers what is said about it, and the two plan hashes
/// cover how it is cut into calls.
/// </para>
/// </summary>
internal sealed record PdfStructuredSourceAuthority(
    IReadOnlyList<SemanticSourceAtom> Atoms,
    IReadOnlyList<CanonicalSemanticSourceEvidence> Evidence,
    IReadOnlyDictionary<string, string> LayoutBlockByAtom,
    IReadOnlyList<PdfStructuredContextPack> Packs,
    string SourceAliasUniverseHash,
    string ModelVisibleEvidenceHash,
    string CallPlanHash,
    string RequestPlanHash);

/// <summary>
/// Builds the coordinate atoms of a PDF, the evidence attached to each, and the requests they
/// would be packed into - all without contacting anything.
/// <para>
/// Coordinates come from visual-line segments. Layout blocks travel alongside as a label, which is
/// the whole point of the rearrangement: a block tells the model which lines were set together, and
/// it can no longer be mistaken for the address of a heading. Every atom the model sees keeps its
/// own alias, so a claim naming two of them is naming two things the model actually read.
/// </para>
/// <para>
/// Nothing here is wired into extraction. The active lane still builds its universe from blocks;
/// this is the path a migration would switch to, measured first.
/// </para>
/// </summary>
internal static class PdfStructuredSourceAuthorityBuilder
{
    private static readonly JsonSerializerOptions Canonical = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    public static PdfStructuredSourceAuthority Build(string pdfPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfPath);

        IReadOnlyList<PdfLine> segments;
        using (var document = UglyToad.PdfPig.PdfDocument.Open(pdfPath))
            segments = PdfLineExtraction.ExtractLines(document, PdfLineGrouping.VisualLineSegmentV3);

        return Build(segments);
    }

    /// <param name="layout">
    /// How the context labels are grouped. Varying it must leave the alias universe alone - that
    /// independence is the property the whole rearrangement rests on.
    /// </param>
    public static PdfStructuredSourceAuthority Build(
        IReadOnlyList<PdfLine> segments, PdfBlockGrouping layout = PdfBlockGrouping.ContinuationV2)
    {
        ArgumentNullException.ThrowIfNull(segments);

        var atoms = PdfSegmentAtomCatalog.FromSegments(segments);
        var annotations = PdfLineBlockFilter.Analyze(segments);

        // One block per segment, keyed by the line's own identity. The candidate context builder
        // then produces a context per atom rather than per layout block, which is what makes the
        // lookup below succeed for every atom instead of for none of them.
        var atomBlocks = segments.Select(SingleLineBlock).ToArray();
        var contexts = PdfCandidateContextBuilder.Build(atomBlocks, annotations);

        // Layout blocks, still built, still grouped the same way - attached as a label.
        var layoutBlocks = PdfSemanticBlockGrouper.Build(
            annotations, includeRiskLines: true, grouping: layout);
        var layoutBlockByAtom = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var block in layoutBlocks)
            foreach (var line in block.Lines)
                layoutBlockByAtom.TryAdd(PdfLineIdentity.Of(line), block.Id);

        var bodyFontSize = PdfCanonicalSourceUniverseBuilder.Median(
            contexts.Values.Select(context => context.Source.FontSize));

        var evidence = atoms
            .Select(atom => PdfCanonicalSourceUniverseBuilder.EvidenceOf(
                contexts[atom.SourceId], atom.Alias, atom.Ordinal, bodyFontSize))
            .ToArray();

        var packs = Pack(evidence, layoutBlockByAtom, atoms);

        return new PdfStructuredSourceAuthority(
            atoms,
            evidence,
            layoutBlockByAtom,
            packs,
            SourceAliasUniverseHash: Hash(new
            {
                schemaVersion = "a99-pdf-segment-atom-universe-v1",
                rows = atoms.Select(atom => new
                {
                    sourceAlias = atom.Alias,
                    sourceId = atom.SourceId,
                    ordinal = atom.Ordinal,
                    page = atom.Page,
                    row = atom.Row,
                    segment = atom.Segment,
                    text = atom.Text,
                }).ToArray(),
            }),
            ModelVisibleEvidenceHash: Hash(new
            {
                schemaVersion = "a99-pdf-model-visible-evidence-v1",
                rows = evidence.Select(item => Visible(item, layoutBlockByAtom)).ToArray(),
            }),
            CallPlanHash: Hash(new
            {
                schemaVersion = "a99-pdf-context-pack-plan-v1",
                packs = packs.Select(pack => new
                {
                    pack.Index,
                    owned = pack.OwnedAliases,
                    visible = pack.VisibleAliases,
                }).ToArray(),
            }),
            RequestPlanHash: Hash(new
            {
                schemaVersion = "a99-pdf-request-plan-v1",
                requests = packs.Select(pack => new { pack.Index, sha256 = pack.RequestSha256 }).ToArray(),
            }));
    }

    /// <summary>
    /// The same partition the production engine applies, over atoms instead of block occurrences.
    /// Owned entries may be claimed; the margin around them is readable context and never claimable.
    /// </summary>
    private static IReadOnlyList<PdfStructuredContextPack> Pack(
        IReadOnlyList<CanonicalSemanticSourceEvidence> evidence,
        IReadOnlyDictionary<string, string> layoutBlockByAtom,
        IReadOnlyList<SemanticSourceAtom> atoms)
    {
        const int owns = CanonicalSemanticEngine.HeaderClassifierCanonicalTextModel.OwnedPerSegment;
        const int margin = CanonicalSemanticEngine.HeaderClassifierCanonicalTextModel.VisibleMargin;

        var packs = new List<PdfStructuredContextPack>();
        for (var start = 0; start < evidence.Count; start += owns)
        {
            var owned = evidence.Skip(start).Take(owns).ToArray();
            if (owned.Length == 0) break;

            var from = Math.Max(0, start - margin);
            var to = Math.Min(evidence.Count, start + owned.Length + margin);
            var visible = evidence.Skip(from).Take(to - from).ToArray();
            var ownedAliases = owned.Select(item => item.SourceAlias).ToHashSet(StringComparer.Ordinal);

            var payload = JsonSerializer.Serialize(new
            {
                protocol = SemanticSourcePartsContract.ProtocolVersion,
                ownedSourceAliases = owned.Select(item => item.SourceAlias).ToArray(),
                openStructuralContext = owned[0].ActiveStructuralAncestors,
                sourceEvidence = visible.Select(item => ownedAliases.Contains(item.SourceAlias)
                    ? Visible(item, layoutBlockByAtom)
                    : new
                    {
                        alias = item.SourceAlias,
                        block = layoutBlockByAtom.GetValueOrDefault(item.SourceId),
                        text = item.ExactSourceText,
                        owned = false,
                    }).ToArray(),
                schema = SemanticSourcePartsContract.Schema(),
            }, Canonical);

            packs.Add(new PdfStructuredContextPack(
                packs.Count,
                owned.Select(item => item.SourceAlias).ToArray(),
                visible.Select(item => item.SourceAlias).ToArray(),
                payload,
                Sha256(payload)));
        }

        return packs;
    }

    /// <summary>
    /// One atom as the model sees it. The layout block is a label beside the alias, never instead
    /// of it, so a claim can only ever be addressed to the atom.
    /// </summary>
    private static object Visible(
        CanonicalSemanticSourceEvidence item, IReadOnlyDictionary<string, string> layoutBlockByAtom) => new
        {
            alias = item.SourceAlias,
            block = layoutBlockByAtom.GetValueOrDefault(item.SourceId),
            text = item.ExactSourceText,
            owned = true,
            scope = item.StructuralScope,
            inTableOfContents = item.InTableOfContents,
            style = item.StyleFacts,
            numbering = item.NumberingFacts,
            markers = item.MarkerFacts,
            attention = item.CandidateAttention.HeuristicMatch,
        };

    private static PdfSemanticBlock SingleLineBlock(PdfLine line) => new(
        PdfLineIdentity.Of(line),
        [line],
        PdfStyleClusterProfile.StyleOf(line),
        line.Page,
        line.Y,
        line.Y,
        line.Left,
        line.Right,
        PdfTextUtilities.Readable(line.Text));

    private static string Hash(object value) => Sha256(JsonSerializer.Serialize(value, Canonical));

    private static string Sha256(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
