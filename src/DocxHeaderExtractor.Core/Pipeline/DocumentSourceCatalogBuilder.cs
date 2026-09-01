using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>Builds generic source units from parser-owned source representations.</summary>
public static class DocumentSourceCatalogBuilder
{
    /// <summary>Builds the DOCX catalog directly from parser-owned source paragraphs.</summary>
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
    /// Builds a catalog from parser-owned facts. The catalog span covers the complete raw fact;
    /// a structural proposal may still retain a narrower span in its own SourceReference.
    /// </summary>
    public static DocumentSourceCatalog FromSourceFacts(IEnumerable<SourceFacts> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return new DocumentSourceCatalog(facts
            .Where(fact => !string.IsNullOrWhiteSpace(fact.RawText))
            .Select((fact, index) => new DocumentSourceUnit(
                fact.SourceId,
                fact.Source.ParagraphIndex ?? index,
                fact.RawText,
                fact.Source,
                new StructuralSpan(0, fact.RawText.Length))));
    }

    /// <summary>Builds the PDF catalog from parser-owned semantic blocks, never from structure text.</summary>
    internal static DocumentSourceCatalog FromPdfParserBlocks(IReadOnlyList<PdfSemanticBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        return FromSourceFacts(blocks.Select((block, index) =>
        {
            var fact = SourceFactsBuilder.FromPdfBlock(block);
            return fact with { Source = fact.Source with { ParagraphIndex = index } };
        }));
    }
}
