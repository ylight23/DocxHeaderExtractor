using System.Text.Json.Serialization;
using UglyToad.PdfPig;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Eval;

/// <summary>
/// Builds a complete, source-only PDF review population. It annotates no heading semantics and
/// deliberately bypasses the sampler/candidate limits used by the diagnostic PDF cluster probe.
/// </summary>
public static class IsolatedPdfSourceBuilder
{
    public static IsolatedPdfSourceReview Build(string pdfPath)
    {
        if (!File.Exists(pdfPath)) throw new FileNotFoundException("PDF source not found.", pdfPath);

        using var document = PdfDocument.Open(pdfPath);
        var pages = document.GetPages().ToList();
        var lines = PdfLineExtraction.ExtractLines(document);
        var annotations = PdfLineBlockFilter.Analyze(lines);
        // includeRiskLines=true is intentional: every extracted source line belongs in the review
        // population, including lines that production candidate logic would exclude.
        var blocks = PdfSemanticBlockGrouper.Build(annotations, includeRiskLines: true);
        var lineOrdinals = new Dictionary<PdfLine, int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < lines.Count; i++) lineOrdinals[lines[i]] = i;

        var blockRows = blocks.Select((block, blockIndex) => new IsolatedPdfBlock(
            $"B{blockIndex + 1:D6}",
            blockIndex,
            block.Page,
            block.Id,
            block.Text,
            block.LineCount,
            new IsolatedPdfLayout(block.TopY, block.BottomY, block.Left, block.Right,
                block.PrimaryStyle.FontSizeBucket, block.PrimaryStyle.FontName, block.PrimaryStyle.FillColorKey),
            block.Lines.Select(line => new IsolatedPdfLine(
                $"L{lineOrdinals[line] + 1:D6}",
                lineOrdinals[line],
                line.Page,
                line.Text,
                line.FontSize,
                line.BoldRatio > 0,
                line.ItalicRatio > 0,
                line.BoldRatio,
                line.ItalicRatio,
                line.Left,
                line.Right,
                line.Y,
                line.FontName,
                line.FillColorKey)).ToArray())).ToArray();

        return new IsolatedPdfSourceReview(
            Path.GetFullPath(pdfPath),
            pages.Count,
            pages.Count,
            lines.Count,
            blockRows.Length,
            blockRows,
            "page ascending, line Y descending, line left ascending; blocks preserve that order",
            false);
    }
}

public sealed record IsolatedPdfSourceReview(
    [property: JsonPropertyName("sourcePath")] string SourcePath,
    [property: JsonPropertyName("pageCount")] int PageCount,
    [property: JsonPropertyName("pagesTraversed")] int PagesTraversed,
    [property: JsonPropertyName("lineCount")] int LineCount,
    [property: JsonPropertyName("blockCount")] int BlockCount,
    [property: JsonPropertyName("blocks")] IReadOnlyList<IsolatedPdfBlock> Blocks,
    [property: JsonPropertyName("ordering")] string Ordering,
    [property: JsonPropertyName("truncated")] bool Truncated);

public sealed record IsolatedPdfBlock(
    [property: JsonPropertyName("sourceOccurrenceId")] string SourceOccurrenceId,
    [property: JsonPropertyName("sourceOrdinal")] int SourceOrdinal,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("blockId")] string BlockId,
    [property: JsonPropertyName("literalSourceText")] string LiteralSourceText,
    [property: JsonPropertyName("lineCount")] int LineCount,
    [property: JsonPropertyName("layout")] IsolatedPdfLayout Layout,
    [property: JsonPropertyName("lines")] IReadOnlyList<IsolatedPdfLine> Lines);

public sealed record IsolatedPdfLine(
    [property: JsonPropertyName("sourceLineId")] string SourceLineId,
    [property: JsonPropertyName("sourceOrdinal")] int SourceOrdinal,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("literalSourceText")] string LiteralSourceText,
    [property: JsonPropertyName("fontSize")] double FontSize,
    [property: JsonPropertyName("bold")] bool Bold,
    [property: JsonPropertyName("italic")] bool Italic,
    [property: JsonPropertyName("boldRatio")] double BoldRatio,
    [property: JsonPropertyName("italicRatio")] double ItalicRatio,
    [property: JsonPropertyName("left")] double Left,
    [property: JsonPropertyName("right")] double Right,
    [property: JsonPropertyName("y")] double Y,
    [property: JsonPropertyName("font")] string Font,
    [property: JsonPropertyName("fillColor")] string FillColor);

public sealed record IsolatedPdfLayout(
    [property: JsonPropertyName("topY")] double TopY,
    [property: JsonPropertyName("bottomY")] double BottomY,
    [property: JsonPropertyName("left")] double Left,
    [property: JsonPropertyName("right")] double Right,
    [property: JsonPropertyName("fontSize")] double FontSize,
    [property: JsonPropertyName("font")] string Font,
    [property: JsonPropertyName("fillColor")] string FillColor);
