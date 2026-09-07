using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

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

    /// <summary>
    /// Builds the PDF catalog from parser-owned semantic blocks, never from structure text. The
    /// optional line inventory keeps source ordinals tied to physical parser order even when a
    /// supplemental/window representation is the selected source unit.
    /// </summary>
    internal static DocumentSourceCatalog FromPdfParserBlocks(
        IReadOnlyList<PdfSemanticBlock> blocks,
        IReadOnlyList<PdfLine>? sourceLines = null)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        var uniqueBlocks = blocks
            .GroupBy(block => block.Id, StringComparer.Ordinal)
            .Select(group =>
            {
                var first = group.First();
                var firstLineIds = first.Lines.Select(PdfCandidateProvenance.LineId).ToArray();
                if (group.Skip(1).Any(other =>
                    !firstLineIds.SequenceEqual(other.Lines.Select(PdfCandidateProvenance.LineId)) ||
                    !string.Equals(first.Text, other.Text, StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException(
                        $"ambiguous_pdf_source_representation: '{first.Id}' has multiple parser representations.");
                }

                return first;
            })
            .ToArray();
        var lineIndexById = sourceLines is null
            ? null
            : sourceLines
                .Select((line, index) => (Id: PdfCandidateProvenance.LineId(line), Index: index))
                .GroupBy(item => item.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().Index, StringComparer.Ordinal);

        return FromSourceFacts(uniqueBlocks.Select((block, index) =>
        {
            var fact = SourceFactsBuilder.FromPdfBlock(block);
            var sourceOrdinal = block.Lines
                .Select(PdfCandidateProvenance.LineId)
                .Where(lineId => lineIndexById?.ContainsKey(lineId) ?? false)
                .Select(lineId => lineIndexById![lineId])
                .DefaultIfEmpty(index)
                .Min();
            return fact with
            {
                Source = fact.Source with
                {
                    ParagraphIndex = sourceOrdinal,
                    RenderLineIds = block.Lines.Select(PdfCandidateProvenance.LineId).ToArray(),
                },
            };
        }));
    }
}
