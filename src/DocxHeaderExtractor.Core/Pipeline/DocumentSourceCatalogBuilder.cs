using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>Builds generic source units from parser-owned source representations.</summary>
public static class DocumentSourceCatalogBuilder
{
    public static DocumentSourceCatalog FromSourceDocument(SourceDocument source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new DocumentSourceCatalog(source.Paragraphs.Where(paragraph => paragraph.Text.Length > 0).Select(paragraph =>
            new DocumentSourceUnit(
                paragraph.SourceId,
                paragraph.SourceOrdinal,
                paragraph.Text,
                new SourceAnchor
                {
                    SourceType = source.SourceKind,
                    ParagraphId = paragraph.SourceId,
                    ParagraphIndex = paragraph.SourceOrdinal,
                    SourceSegments = paragraph.SourceSegments,
                },
                new StructuralSpan(0, paragraph.Text.Length))));
    }

    /// <summary>
    /// Adds parser-owned units that are not present in the canonical source document, such as
    /// PDF layout blocks used by non-heading structural elements. Existing source identity wins.
    /// </summary>
    public static DocumentSourceCatalog MergeStructuralSources(
        DocumentSourceCatalog catalog,
        ValidatedStructure structure)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(structure);
        var known = catalog.Units.Select(unit => unit.SourceId).ToHashSet(StringComparer.Ordinal);
        var additions = structure.Elements
            .SelectMany(element => element.Sources.Select(source => (element, source)))
            .Where(item => !known.Contains(item.source.SourceId))
            .GroupBy(item => item.source.SourceId, StringComparer.Ordinal)
            .Select(group =>
            {
                var item = group.First();
                return new DocumentSourceUnit(
                    item.source.SourceId,
                    item.source.SourceOrdinal,
                    item.element.Text,
                    new SourceAnchor
                    {
                        SourceType = "parser-fact",
                        ParagraphId = item.source.SourceId,
                        ParagraphIndex = item.source.SourceOrdinal,
                    },
                    item.source.Span);
            });
        return new DocumentSourceCatalog(catalog.Units.Concat(additions));
    }
}
