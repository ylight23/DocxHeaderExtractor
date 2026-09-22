using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// One request's worth of the deterministic partition: which atoms it owns, which it shows for
/// context. Nothing here is serialized to a request - that step belongs to exactly one place,
/// <see cref="CanonicalSemanticRequestComposer"/>, reached through
/// <see cref="CanonicalSemanticEngine.HeaderClassifierCanonicalTextModel.ComposeRequests"/>.
/// </summary>
internal sealed record PdfStructuredEvidencePack(
    int Index,
    IReadOnlyList<string> OwnedAliases,
    IReadOnlyList<string> VisibleAliases);

/// <summary>
/// The coordinate atoms of a PDF, the evidence attached to each, and how they partition into
/// requests - three hashes that pin exactly that and nothing past it.
/// <para>
/// A fourth hash used to live here, over a request format this type invented for measurement. It
/// disagreed with what production actually sends, because inventing a second format is exactly how
/// that happens. Request bytes are no longer this type's concern at all: build a
/// <see cref="CanonicalSemanticProductionInput"/> from <see cref="CreateProductionInput"/> and ask
/// the one real composer, the same way a live call would.
/// </para>
/// </summary>
internal sealed record PdfStructuredSourceAuthority(
    IReadOnlyList<SemanticSourceAtom> Atoms,
    IReadOnlyList<CanonicalSemanticSourceEvidence> Evidence,
    IReadOnlyDictionary<string, string> LayoutBlockByAtom,
    IReadOnlyList<PdfStructuredEvidencePack> Packs,
    string SourceAliasUniverseHash,
    string ModelVisibleEvidenceHash,
    string CallPlanHash,
    string SourceSha256,
    IReadOnlyList<PdfSemanticBlock> Blocks,
    IReadOnlyDictionary<string, PdfCandidateContext> Contexts,
    DocumentSourceCatalog Catalog,
    IReadOnlyList<SemanticSourceAlias> Aliases,
    IReadOnlyDictionary<string, int> OrdinalBySourceId,
    int ParserLineCount) : IPdfSemanticSourceAuthority
{
    /// <summary>
    /// The coordinate universe identity a live route checks before it will transport - the same
    /// role <see cref="PdfCanonicalSourceUniverse.SourceUniverseSha256"/> plays for the occurrence
    /// lane. <see cref="SourceAliasUniverseHash"/> already is that identity for the atom universe:
    /// it depends on the PDF's text and geometry alone, nothing a proposal or a request could move.
    /// </summary>
    public string SourceUniverseSha256 => SourceAliasUniverseHash;

    /// <summary>
    /// What a live call would actually build and send: the same input shape the production
    /// adapter would construct, with the atoms' layout labels attached as context rather than as
    /// coordinates.
    /// </summary>
    public CanonicalSemanticProductionInput CreateProductionInput(string documentId) =>
        new(
            Catalog,
            null,
            SourceSha256,
            [new CanonicalSemanticPageEvidence("PDF", true, 0, "pdf-source")],
            Evidence.Select(item => item.CandidateAttention).ToArray(),
            Evidence.Select(item => $"[{item.SourceAlias}] {item.ExactSourceText}").ToArray(),
            [],
            Evidence.SelectMany(item => item.LocalBefore.Concat(item.LocalAfter)).ToArray(),
            DocumentId: documentId,
            SourceEvidence: Evidence)
        {
            ExpectedSourceSha256 = SourceSha256,
            LayoutBlockBySourceId = LayoutBlockByAtom,
            SourceUniverseSha256 = SourceUniverseSha256,
            // The contract that issued the schema also validates, decodes and binds. Its aliases
            // are the atoms' own - deriving them from the catalog instead would renumber every
            // coordinate into a scheme the model was never shown.
            CoordinateContract = SemanticCoordinateContract.PdfStructuredSourceParts,
            SourceAliases = Aliases,
            SourceAtoms = Atoms,
        };
}

/// <summary>
/// Builds the coordinate atoms of a PDF and the evidence attached to each - all without contacting
/// anything, and without deciding what a request looks like.
/// <para>
/// Coordinates come from visual-line segments. Layout blocks travel alongside as a label, which is
/// the whole point of the rearrangement: a block tells the model which lines were set together, and
/// it can no longer be mistaken for the address of a heading. Every atom the model sees keeps its
/// own alias, so a claim naming two of them is naming two things the model actually read.
/// </para>
/// <para>
/// The structured PDF profile uses this source authority. Request partitioning remains a separate
/// execution capability on the semantic engine, so the same source universe can run under the
/// default fixed policy or an explicitly selected experiment policy.
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

        return Build(segments, layout: PdfBlockGrouping.ContinuationV2,
            sourceSha256: CanonicalSemanticSourceHash.Compute(pdfPath));
    }

    /// <param name="layout">
    /// How the context labels are grouped. Varying it must leave the alias universe alone - that
    /// independence is the property the whole rearrangement rests on.
    /// </param>
    /// <param name="sourceSha256">
    /// Only needed to build a <see cref="CanonicalSemanticProductionInput"/> afterwards. Every
    /// other measurement here - the three hashes - depends on the PDF's text and geometry alone.
    /// </param>
    public static PdfStructuredSourceAuthority Build(
        IReadOnlyList<PdfLine> segments,
        PdfBlockGrouping layout = PdfBlockGrouping.ContinuationV2,
        string sourceSha256 = "")
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

        var packs = Partition(evidence);

        var catalog = DocumentSourceCatalogBuilder.FromPdfParserBlocks(atomBlocks, segments);
        // Deliberately not SemanticSourceAliasCatalog.FromCatalog(catalog): that scheme renumbers
        // to S0001.. running numbers, the addressing this whole rearrangement retired. An atom's
        // own alias (L{row}:S{segment}) is its coordinate identity everywhere, including here.
        var aliases = atoms
            .Select(atom => new SemanticSourceAlias(
                atom.Alias, atom.SourceId, atom.Ordinal, atom.Text,
                new StructuralSpan(0, atom.Text.Length)))
            .ToArray();
        var ordinalByAtomSourceId = atoms.ToDictionary(
            atom => atom.SourceId, atom => atom.Ordinal, StringComparer.Ordinal);

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
            SourceSha256: sourceSha256,
            Blocks: atomBlocks,
            Contexts: contexts,
            Catalog: catalog,
            Aliases: aliases,
            OrdinalBySourceId: ordinalByAtomSourceId,
            ParserLineCount: segments.Count);
    }

    /// <summary>
    /// The same partition the production engine applies, over atoms instead of block occurrences -
    /// which owns which alias, and which margin surrounds it. Nothing here decides what those
    /// aliases are shown as; that is <see cref="CanonicalSemanticEngine.HeaderClassifierCanonicalTextModel"/>'s
    /// job; the two must partition identically, which is why both use the same constants.
    /// </summary>
    private static IReadOnlyList<PdfStructuredEvidencePack> Partition(
        IReadOnlyList<CanonicalSemanticSourceEvidence> evidence)
    {
        const int owns = CanonicalSemanticEngine.HeaderClassifierCanonicalTextModel.OwnedPerSegment;
        const int margin = CanonicalSemanticEngine.HeaderClassifierCanonicalTextModel.VisibleMargin;

        var packs = new List<PdfStructuredEvidencePack>();
        for (var start = 0; start < evidence.Count; start += owns)
        {
            var owned = evidence.Skip(start).Take(owns).ToArray();
            if (owned.Length == 0) break;

            var from = Math.Max(0, start - margin);
            var to = Math.Min(evidence.Count, start + owned.Length + margin);
            var visible = evidence.Skip(from).Take(to - from).ToArray();

            packs.Add(new PdfStructuredEvidencePack(
                packs.Count,
                owned.Select(item => item.SourceAlias).ToArray(),
                visible.Select(item => item.SourceAlias).ToArray()));
        }

        return packs;
    }

    /// <summary>
    /// One atom as the model sees it, for <see cref="PdfStructuredSourceAuthority.ModelVisibleEvidenceHash"/>
    /// only - a measurement of what is attached to each alias, not a request serialization. Its
    /// shape matches <c>HeaderClassifierCanonicalTextModel.OwnedEvidence</c>'s block-bearing branch
    /// exactly, so the hash means what its name says.
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

    private static string Hash(object value) =>
        Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Canonical))));
}
