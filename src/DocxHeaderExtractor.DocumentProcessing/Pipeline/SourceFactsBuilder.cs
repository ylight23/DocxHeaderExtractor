using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>Builds immutable parser facts before any LLM/VLM call.</summary>
public static class SourceFactsBuilder
{
    internal static SourceFacts FromPdfBlock(PdfSemanticBlock block)
    {
        return new SourceFacts
        {
            SourceId = block.Id,
            // The canonical projection, not the raw concatenation. A PDF has no text, only ordered
            // glyphs; the raw string is one reconstruction and a poor one, so it stays available on
            // the block for audit while the declared projection is what anything downstream binds.
            RawText = block.VerbatimText,
            RawSpan = new SourceTextSpan(0, block.VerbatimText.Length),
            Source = new SourceAnchor
            {
                SourceType = "pdf",
                Page = block.Page,
                RenderBlockId = block.Id,
                RenderLineIds = block.Lines.Select((_, index) => $"{block.Id}:l{index + 1}").ToArray(),
                BoundingBox = new PdfBoundingBox(block.Left, block.BottomY, block.Right, block.TopY),
            },
            ParserBoundaries = SourceTextBoundaryMap.For(block.Text),
        };
    }
}
