using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// The coordinate atoms of a PDF and the evidence attached to each - two hashes that pin exactly
/// that and nothing past it. How they partition into requests is the packing policy's concern.
/// <para>Request composition is deliberately outside this source authority. The promoted PDF
/// route consumes these parser-owned facts directly through F1 → G2A → H2-C V2.</para>
/// </summary>
internal sealed record PdfStructuredSourceAuthority(
    IReadOnlyList<SemanticSourceAtom> Atoms,
    IReadOnlyList<CanonicalSemanticSourceEvidence> Evidence,
    IReadOnlyDictionary<string, string> LayoutBlockByAtom,
    string SourceAliasUniverseHash,
    string ModelVisibleEvidenceHash,
    string SourceSha256,
    IReadOnlyList<PdfSemanticBlock> Blocks,
    IReadOnlyDictionary<string, PdfSemanticSourceContext> Contexts,
    DocumentSourceCatalog Catalog,
    IReadOnlyList<SemanticSourceAlias> Aliases,
    IReadOnlyDictionary<string, int> OrdinalBySourceId,
    int ParserLineCount)
{
    /// <summary>
    /// The coordinate universe identity a live route checks before it will transport.
    /// <see cref="SourceAliasUniverseHash"/> already is that identity for the atom universe:
    /// it depends on the PDF's text and geometry alone, nothing a proposal or a request could move.
    /// </summary>
    public string SourceUniverseSha256 => SourceAliasUniverseHash;

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
            segments = PdfLineExtraction.ExtractLines(document);

        return Build(segments, sourceSha256: CanonicalSemanticSourceHash.Compute(pdfPath));
    }

    /// <param name="sourceSha256">
    /// Retained for qualification/source-authority construction. Every other measurement here -
    /// the three hashes - depends on the PDF's text and geometry alone.
    /// </param>
    public static PdfStructuredSourceAuthority Build(
        IReadOnlyList<PdfLine> segments,
        string sourceSha256 = "")
    {
        ArgumentNullException.ThrowIfNull(segments);

        var atoms = PdfSegmentAtomCatalog.FromSegments(segments);
        var annotations = PdfLineObservationAnalyzer.Analyze(segments);

        // One block per segment, keyed by the line's own identity. The semantic source context builder
        // then produces a context per atom rather than per layout block, which is what makes the
        // lookup below succeed for every atom instead of for none of them.
        var atomBlocks = segments.Select(SingleLineBlock).ToArray();
        var contexts = PdfSemanticSourceContextBuilder.Build(atomBlocks, annotations);

        // Layout blocks, still built, still grouped the same way - attached as a label.
        var layoutBlocks = PdfSemanticBlockGrouper.Build(annotations);
        var layoutBlockByAtom = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var block in layoutBlocks)
            foreach (var line in block.Lines)
                layoutBlockByAtom.TryAdd(PdfLineIdentity.Of(line), block.Id);

        var bodyFontSize = PdfSourceEvidence.Median(
            contexts.Values.Select(context => context.Source.FontSize));

        var evidence = atoms
            .Select(atom => PdfSourceEvidence.EvidenceOf(
                contexts[atom.SourceId], atom.Alias, atom.Ordinal, bodyFontSize))
            .ToArray();

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
                schemaVersion = "a99-pdf-model-visible-evidence-v2",
                rows = evidence.Select(item => VisibleV2(item, layoutBlockByAtom)).ToArray(),
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
    /// One atom as the model sees it: no harness judgement attached - no salience flag, and
    /// physical location facts in place of the scope label and the contents flag.
    /// </summary>
    private static object VisibleV2(
        CanonicalSemanticSourceEvidence item, IReadOnlyDictionary<string, string> layoutBlockByAtom) => new
        {
            alias = item.SourceAlias,
            block = layoutBlockByAtom.GetValueOrDefault(item.SourceId),
            text = item.ExactSourceText,
            owned = true,
            location = item.LocationFacts,
            style = item.StyleFacts,
            numbering = item.NumberingFacts,
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
