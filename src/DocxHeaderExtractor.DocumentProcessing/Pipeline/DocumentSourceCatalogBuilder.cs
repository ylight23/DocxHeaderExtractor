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
    /// <summary>
    /// A catalog whose units are visual line segments rather than layout blocks.
    /// <para>
    /// The seam for a coordinate system in which the atom is one coherent region of one row, and
    /// blocks are context rather than authority. It is built and measured; nothing routes through
    /// it yet, and <see cref="FromPdfParserBlocks"/> remains what production reads. A heading that
    /// wraps occupies more than one atom here, so this catalog only becomes bindable once a
    /// contract exists for naming several of them - which is a separate decision.
    /// </para>
    /// </summary>
    internal static DocumentSourceCatalog FromPdfVisualLineSegments(IReadOnlyList<PdfLine> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);

        var blocks = segments.Select((line, index) => new PdfSemanticBlock(
            $"l{index + 1}",
            [line],
            PdfStyleClusterProfile.StyleOf(line),
            line.Page,
            line.Y,
            line.Y,
            line.Left,
            line.Right,
            PdfTextUtilities.Readable(line.Text))).ToArray();

        return FromPdfParserBlocks(blocks, segments);
    }

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
                var firstLineIds = first.Lines.Select(PdfLineIdentity.Of).ToArray();
                if (group.Skip(1).Any(other =>
                    !firstLineIds.SequenceEqual(other.Lines.Select(PdfLineIdentity.Of)) ||
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
                .Select((line, index) => (Id: PdfLineIdentity.Of(line), Index: index))
                .GroupBy(item => item.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().Index, StringComparer.Ordinal);

        return FromSourceFacts(uniqueBlocks.Select((block, index) =>
        {
            var fact = SourceFactsBuilder.FromPdfBlock(block);
            var sourceOrdinal = block.Lines
                .Select(PdfLineIdentity.Of)
                .Where(lineId => lineIndexById?.ContainsKey(lineId) ?? false)
                .Select(lineId => lineIndexById![lineId])
                .DefaultIfEmpty(index)
                .Min();
            return fact with
            {
                Source = fact.Source with
                {
                    ParagraphIndex = sourceOrdinal,
                    RenderLineIds = block.Lines.Select(PdfLineIdentity.Of).ToArray(),
                },
            };
        }));
    }
}
