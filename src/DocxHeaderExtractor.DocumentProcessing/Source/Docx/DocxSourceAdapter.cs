using DocxHeaderExtractor.Core.Semantics.Binding;
using DocxHeaderExtractor.DocumentProcessing.Provenance;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Source.Common;

namespace DocxHeaderExtractor.DocumentProcessing.Source.Docx;

/// <summary>
/// DOCX source adapter. It projects only facts OOXML actually owns into the common occurrence
/// universe; it never fabricates PDF pages, lines, geometry, or layout blocks.
/// </summary>
internal static class DocxSourceAdapter
{
    internal static DocxSourceBuildResult BuildForAudit(SourceDocument source) => Build(source);

    internal static DocxSourceBuildResult Build(SourceDocument sourceDocument)
    {
        var paragraphs = sourceDocument.Paragraphs
            .Where(source => !string.IsNullOrWhiteSpace(source.Text))
            .OrderBy(source => source.SourceOrdinal)
            .ToArray();
        var result = new Dictionary<string, DocxSourceContext>(StringComparer.Ordinal);
        var headingContexts = new Dictionary<string, OccurrenceContext>(StringComparer.Ordinal);
        for (var index = 0; index < paragraphs.Length; index++)
        {
            var sourceParagraph = paragraphs[index];
            var id = sourceParagraph.SourceId;
            const string scope = "document_body";
            var marker = SourceMarkerFactsParser.Parse(sourceParagraph.Text);
            var evidence = new List<string>
            {
                sourceParagraph.Text.Length <= 180 ? "short_source_paragraph" : "long_source_paragraph",
                marker is null ? "no_marker" : $"marker:{marker.Value.Family}",
            };
            if (sourceParagraph.Style.OutlineLevel is >= 0 and <= 8) evidence.Add($"outline_level:{sourceParagraph.Style.OutlineLevel.Value}");
            var previous = paragraphs.Take(index).TakeLast(3).Select(item => Excerpt(item.Text)).ToArray();
            var next = paragraphs.Skip(index + 1).Take(3).Select(item => Excerpt(item.Text)).ToArray();
            var origins = evidence.Select(item => item.StartsWith("marker:", StringComparison.Ordinal) ? "marker_parser" :
                item.StartsWith("outline_level:", StringComparison.Ordinal) ? "ooxml_parser" : "docx_parser").ToArray();
            var headingContext = new OccurrenceContext(id, sourceParagraph.Text, scope, origins, previous, next);
            var context = new DocxSourceContext(sourceParagraph, scope, headingContext, evidence);
            result.Add(id, context);
            headingContexts.Add(id, headingContext);
        }
        var catalog = DocumentSourceCatalogBuilder.FromSourceDocument(sourceDocument);
        var aliases = SemanticSourceAliasCatalog.FromCatalog(catalog).ToArray();
        var aliasesBySourceId = aliases.ToDictionary(item => item.SourceId, StringComparer.Ordinal);
        var universeEvidence = result.Values.OrderBy(item => item.Source.SourceOrdinal)
            .Select(item => EvidenceOf(item, aliasesBySourceId[item.Source.SourceId].Alias)).ToArray();
        var sourceHash = CanonicalSemanticSourceHash.Compute(sourceDocument.SourcePath);
        var universe = new DocumentSourceSnapshot(
            [],
            result.Values.OrderBy(item => item.Source.SourceOrdinal)
                .Select(item => new DocumentOccurrence(item.Source.SourceId, aliasesBySourceId[item.Source.SourceId].Alias,
                    item.Source.SourceOrdinal, item.Source.Text, sourceDocument.SourceKind, item.Source.Style.StyleId)).ToArray(),
            universeEvidence,
            sourceHash,
            sourceHash,
            sourceHash,
            headingContexts,
            catalog,
            aliases,
            result.Values.ToDictionary(item => item.Source.SourceId, item => item.Source.SourceOrdinal, StringComparer.Ordinal))
        {
            SourceKind = sourceDocument.SourceKind,
            DocumentId = sourceDocument.DocumentId,
        };
        return new DocxSourceBuildResult(result, headingContexts, universe);
    }

    private static string Excerpt(string text) => text.Length <= 180 ? text : text[..180];

    private static CanonicalSemanticSourceEvidence EvidenceOf(DocxSourceContext context, string alias)
    {
        var source = context.Source;
        return new CanonicalSemanticSourceEvidence(
            alias, source.SourceId, source.SourceOrdinal, source.Text, context.Scope,
            ["docx-parser-source"],
            new { source.Style.StyleId, source.Style.StyleName, source.Style.OutlineLevel, source.Style.Bold },
            new { source.Numbering.NumberingId, source.Numbering.NumberingLevel, source.Numbering.NumberLabel },
            source.TextSpans.Select(span => (object)new { span.Start, span.End, span.Bold, span.Italic, span.Underline }).ToArray(),
            context.ObservedEvidence, context.HeadingContext.PreviousOccurrences, context.HeadingContext.NextOccurrences)
        {
            LocationFacts = source.HyperlinkAnchors.Count == 0 ? null : new { hyperlinkAnchors = source.HyperlinkAnchors },
        };
    }
}
internal sealed record DocxSourceContext(
    SourceParagraph Source,
    string Scope,
    OccurrenceContext HeadingContext,
    IReadOnlyList<string> ObservedEvidence);

internal sealed record DocxSourceBuildResult(
    IReadOnlyDictionary<string, DocxSourceContext> Contexts,
    IReadOnlyDictionary<string, OccurrenceContext> OccurrenceContexts,
    DocumentSourceSnapshot Snapshot)
{
    public int Count => Contexts.Count;
}
