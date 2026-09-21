using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using UglyToad.PdfPig;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// Immutable, parser-only input to the PDF semantic stages. It owns the complete occurrence
/// universe and the evidence derived from it; it never contains a model decision or Gold data.
/// </summary>
internal sealed record PdfCanonicalSourceUniverse(
    IReadOnlyList<PdfSemanticBlock> Blocks,
    IReadOnlyDictionary<string, PdfCandidateContext> Contexts,
    DocumentSourceCatalog Catalog,
    IReadOnlyList<SemanticSourceAlias> Aliases,
    IReadOnlyDictionary<string, SemanticSourceAlias> AliasesBySourceId,
    IReadOnlyDictionary<string, int> OrdinalBySourceId,
    IReadOnlyList<CanonicalSemanticSourceEvidence> Evidence,
    string SourceSha256,
    int ParserLineCount)
{
    /// <summary>
    /// Stable runtime identity of the parser universe. This is derived only from source rows and
    /// is intentionally independent of proposals, Gold, prompts, and provider transport.
    /// </summary>
    public string SourceUniverseSha256 => PdfCanonicalSourceUniverseHash.Compute(this);

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
            OwnedAliases = null,
            SourceUniverseSha256 = SourceUniverseSha256,
        };
}

internal static class PdfCanonicalSourceUniverseBuilder
{
    public static PdfCanonicalSourceUniverse Build(string pdfPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfPath);

        IReadOnlyList<PdfLine> lines;
        using (var document = PdfDocument.Open(pdfPath))
        {
            lines = PdfLineExtraction.ExtractLines(document);
        }

        return Build(pdfPath, lines);
    }

    internal static PdfCanonicalSourceUniverse Build(
        string pdfPath,
        IReadOnlyList<PdfLine> lines)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfPath);
        ArgumentNullException.ThrowIfNull(lines);

        var annotations = PdfLineBlockFilter.Analyze(lines);
        // Risk lines remain in the universe. Parser observations travel as evidence and never
        // become a candidate gate, because the universe is the recall ceiling for review/eval.
        var blocks = PdfSemanticBlockGrouper.Build(annotations, includeRiskLines: true);
        var contexts = PdfCandidateContextBuilder.Build(blocks, annotations);
        var catalog = DocumentSourceCatalogBuilder.FromPdfParserBlocks(blocks, lines);
        var aliases = SemanticSourceAliasCatalog.FromCatalog(catalog);
        var aliasesBySourceId = aliases.ToDictionary(item => item.SourceId, StringComparer.Ordinal);
        var ordinalBySourceId = catalog.Units.ToDictionary(
            unit => unit.SourceId, unit => unit.SourceOrdinal, StringComparer.Ordinal);
        var sourceHash = CanonicalSemanticSourceHash.Compute(pdfPath);
        var bodyFontSize = Median(contexts.Values.Select(context => context.Source.FontSize));
        var evidence = contexts.Values
            .Where(context => aliasesBySourceId.ContainsKey(context.Source.SourceId))
            .OrderBy(context => ordinalBySourceId.GetValueOrDefault(context.Source.SourceId, int.MaxValue))
            .Select(context => EvidenceOf(
                context,
                aliasesBySourceId[context.Source.SourceId].Alias,
                ordinalBySourceId.GetValueOrDefault(context.Source.SourceId),
                bodyFontSize))
            .ToArray();

        return new PdfCanonicalSourceUniverse(
            blocks,
            contexts,
            catalog,
            aliases,
            aliasesBySourceId,
            ordinalBySourceId,
            evidence,
            sourceHash,
            lines.Count);
    }

    private static CanonicalSemanticSourceEvidence EvidenceOf(
        PdfCandidateContext context,
        string alias,
        int ordinal,
        double bodyFontSize)
    {
        var source = context.Source;
        var bold = source.BoldRatio >= 0.5;
        var italic = source.ItalicRatio >= 0.5;
        var relativeFontSize = RelativeSize(source.FontSize, bodyFontSize);
        var attention = !source.ObservedEvidence.Contains("page_number") &&
            source.StructuralScope is not ("running_page_artifact" or "table_of_contents");
        return new CanonicalSemanticSourceEvidence(
            alias,
            source.SourceId,
            ordinal,
            source.RawText,
            source.StructuralScope,
            TableDepth: null,
            SectionIndex: source.Page,
            InContentControl: false,
            InTableOfContents: source.StructuralScope == "table_of_contents",
            ["pdf-parser-source", $"scope:{source.StructuralScope}", $"page:{source.Page}"],
            new { Bold = bold, Italic = italic, RelativeFontSize = relativeFontSize, LineCount = source.LineCount },
            new { },
            [],
            CanonicalSemanticEngine.MarkerFactsOf(source),
            source.ObservedEvidence,
            context.PreviousBlocks,
            context.NextBlocks,
            new SemanticCandidateAttentionHint(
                alias, attention, attention ? "pdf-layout-candidate" : "pdf-layout-non-candidate"))
        {
            ActiveStructuralAncestors = context.ActiveHeadingStack,
        };
    }

    private static string RelativeSize(double size, double body)
    {
        if (size <= 0 || body <= 0) return "unknown";
        var ratio = size / body;
        return ratio >= 1.25 ? "much-larger-than-body"
            : ratio >= 1.08 ? "larger-than-body"
            : ratio <= 0.85 ? "smaller-than-body"
            : "body";
    }

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.Where(value => value > 0).OrderBy(value => value).ToArray();
        return ordered.Length == 0 ? 0 : ordered[ordered.Length / 2];
    }
}

internal static class PdfCanonicalSourceUniverseHash
{
    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    public static string Compute(PdfCanonicalSourceUniverse universe)
    {
        ArgumentNullException.ThrowIfNull(universe);
        var rows = universe.Aliases
            .OrderBy(alias => alias.SourceOrdinal)
            .ThenBy(alias => alias.Alias, StringComparer.Ordinal)
            .Select(alias => new
            {
                sourceAlias = alias.Alias,
                sourceId = alias.SourceId,
                ordinal = alias.SourceOrdinal,
                text = alias.Text,
                sourceStart = alias.SourceSpan.Start,
                sourceEnd = alias.SourceSpan.End,
            })
            .ToArray();
        var json = JsonSerializer.Serialize(new
        {
            schemaVersion = "a99-pdf-runtime-source-universe-v1",
            sourceSha256 = universe.SourceSha256,
            rows,
        }, Json);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }
}
