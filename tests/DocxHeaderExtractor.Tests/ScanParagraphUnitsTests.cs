using DocxHeaderExtractor.Contracts.Scan;
using DocxHeaderExtractor.Sources.Scan;
using DocxHeaderExtractor.HeadingAgent.Scan;

namespace DocxHeaderExtractor.Tests;

/// <summary>The paragraph experiment's units: a unit is cut only where a line's space above is large for its page, and consecutive paragraphs
/// are packed across pages while a unit stays within the line limit.</summary>
public sealed class ScanParagraphUnitsTests
{
    static ScanLine Line(int page, int index, int top) =>
        new($"P{page:000}-L{index + 1:000}", page, new OcrLine($"P{page:000}-ocr-original-L{index + 1:000}", "text",
            new PixelBox(100, top, 800, 20), 95, 95, false, []), 1600, 2200,
            new Dictionary<string, (OcrLine Line, IReadOnlyList<TextDifference> Differences)>());

    [Fact]
    public void A_large_gap_cuts_a_paragraph_and_the_next_page_joins_the_same_unit()
    {
        // Page 1: lines 1-3 close together, then a large space before line 4. Page 2: two lines, close together.
        var lines = new[] { Line(1, 0, 0), Line(1, 1, 30), Line(1, 2, 60), Line(1, 3, 200), Line(2, 0, 0), Line(2, 1, 30) };

        var cores = ScanAgentSource.ParagraphCores(lines);

        Assert.Equal([(0, 6)], cores);
    }

    [Fact]
    public void A_page_of_one_paragraph_gives_one_unit_and_a_page_of_many_gives_units_at_paragraph_boundaries_only()
    {
        // Sixty lines in paragraphs of eight: a large space before every eighth line. Six paragraphs fill 48 lines; the rest follow.
        var lines = Enumerable.Range(0, 60).Select(i => Line(1, i, i * 30 + 130 * (i / 8))).ToArray();

        var cores = ScanAgentSource.ParagraphCores(lines);

        Assert.Equal([(0, 48), (48, 60)], cores);
    }
}
