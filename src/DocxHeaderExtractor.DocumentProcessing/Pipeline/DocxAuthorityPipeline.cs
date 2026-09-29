using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// DOCX source adapter for the same authority pipeline used by PDF-first extraction. DOCX text,
/// stable paragraph identity, and pointer spans remain source facts; the 9B model can only make
/// closed role/span/parent proposals through the shared analyst contracts.
/// </summary>
internal static class DocxAuthorityPipeline
{
    internal static DocxAuthoritySource BuildForAudit(SourceDocument source) =>
        Build(source, (id, text) => PdfMarkerFactsParser.Parse(text));

    public static async Task<StructuralAuthorityResult> RunAsync(
        SourceDocument source,
        IHeaderClassifier? analyst,
        CancellationToken ct = default) =>
        await CanonicalSemanticDocxAuthorityAdapter.RunAsync(source, analyst, ct);

    private static DocxAuthoritySource Build(
        SourceDocument sourceDocument,
        Func<string, string, PdfMarkerFact?> markerFor)
    {
        var paragraphs = sourceDocument.Paragraphs
            .Where(source => !string.IsNullOrWhiteSpace(source.Text))
            .OrderBy(source => source.SourceOrdinal)
            .ToArray();
        const string regime = "document_body";
        var result = new Dictionary<string, DocxAuthorityContext>(StringComparer.Ordinal);
        var modelContexts = new Dictionary<string, PdfSemanticSourceContext>(StringComparer.Ordinal);
        var blocks = new List<PdfSemanticBlock>(paragraphs.Length);
        for (var index = 0; index < paragraphs.Length; index++)
        {
            var sourceParagraph = paragraphs[index];
            var id = sourceParagraph.SourceId;
            const string scope = "document_body";
            var marker = markerFor(id, sourceParagraph.Text);
            var evidence = new List<string>
            {
                sourceParagraph.Text.Length <= 180 ? "short_source_paragraph" : "long_source_paragraph",
                marker is null ? "no_marker" : $"marker:{marker.Value.Family}",
            };
            if (sourceParagraph.Style.OutlineLevel is >= 0 and <= 8) evidence.Add($"outline_level:{sourceParagraph.Style.OutlineLevel.Value}");
            var facts = new PdfSourceFacts(id, sourceParagraph.Text, 0, 1, 0, -sourceParagraph.SourceOrdinal, 0, -sourceParagraph.SourceOrdinal,
                scope, evidence)
            {
                Marker = marker,
                LineIds = [sourceParagraph.SourceId],
                EvidenceDetails = evidence.Select(item => new PdfObservedEvidence(item, "true",
                    item.StartsWith("marker:", StringComparison.Ordinal) ? "marker_parser" :
                    item.StartsWith("outline_level:", StringComparison.Ordinal) ? "ooxml_parser" : "docx_parser")).ToArray(),
            };
            var previous = paragraphs.Take(index).TakeLast(3).Select(item => Excerpt(item.Text)).ToArray();
            var next = paragraphs.Skip(index + 1).Take(3).Select(item => Excerpt(item.Text)).ToArray();
            var parents = paragraphs.Take(index).TakeLast(8).Select(item => item.SourceId).ToArray();
            var modelContext = new PdfSemanticSourceContext(facts, previous, next, parents, regime);
            var context = new DocxAuthorityContext(sourceParagraph, scope, modelContext);
            result.Add(id, context);
            modelContexts.Add(id, modelContext);
            var line = new PdfLine(0, -sourceParagraph.SourceOrdinal, sourceParagraph.Style.FontSizePt ?? 11, sourceParagraph.Text,
                sourceParagraph.Style.Bold ? 1 : 0, "", sourceParagraph.Style.Italic ? 1 : 0, 0, 1, sourceParagraph.Style.StyleName ?? "docx", "docx");
            blocks.Add(new PdfSemanticBlock(id, [line], PdfStyleClusterProfile.StyleOf(line), 0,
                -sourceParagraph.SourceOrdinal, -sourceParagraph.SourceOrdinal, 0, 1, sourceParagraph.Text));
        }
        return new DocxAuthoritySource(blocks, result, modelContexts);
    }

    private static string Excerpt(string text) => text.Length <= 180 ? text : text[..180];
}
internal sealed record DocxAuthorityContext(
    SourceParagraph Source,
    string Scope,
    PdfSemanticSourceContext ModelContext);

internal sealed record DocxAuthoritySource(
    IReadOnlyList<PdfSemanticBlock> Blocks,
    IReadOnlyDictionary<string, DocxAuthorityContext> Contexts,
    IReadOnlyDictionary<string, PdfSemanticSourceContext> ModelContexts);
