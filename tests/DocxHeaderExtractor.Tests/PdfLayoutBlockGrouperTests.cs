using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Block grouping: which consecutive lines continue one another, and therefore become one source
/// occurrence.
/// <para>
/// The cases below are written as coordinates because the rule is a claim about typography, not
/// about a document: the gap between lines is measured against the document's own line pitch.
/// </para>
/// </summary>
public sealed class PdfLayoutBlockGrouperTests
{
    [Fact]
    public void MergesNearbySameStyleTitleLinesButStopsAfterSentence()
    {
        var lines = new[]
        {
            Ann(Line("Top 3 trust funds activated during the fiscal year", page: 1, y: 700)),
            Ann(Line("ended June 30, 2024, on the basis of Expected Funding", page: 1, y: 684)),
            Ann(Line("Recipients should retain this statement.", page: 1, y: 640)),
            Ann(Line("This next sentence must not merge.", page: 1, y: 624)),
        };

        var blocks = PdfLayoutBlockGrouper.Build(lines);

        Assert.Equal(3, blocks.Count);
        Assert.Equal(2, blocks[0].LineCount);
        Assert.Contains("Expected Funding", blocks[0].Text);
        Assert.Equal("Recipients should retain this statement.", blocks[1].Text);
        Assert.Equal("This next sentence must not merge.", blocks[2].Text);
    }

    // ---- what the document's own leading is ----------------------------------------------------

    [Fact]
    public void The_leading_is_the_smallest_gap_that_recurs_not_the_most_common_one()
    {
        // A document of short articles has more paragraph breaks than wrapped lines. Taking the
        // most common gap would hand back the break, and the grouper would then merge straight
        // through every one of them. The leading is the smallest gap the document uses repeatedly.
        var lines = Page(
            ("Article 1", 0),
            ("a paragraph that wraps once here", 14),
            ("Article 2", 20),
            ("another paragraph that wraps", 14),
            ("Article 3", 20),
            ("a third paragraph wrapping too", 14),
            ("Article 4", 20));

        Assert.Equal(1.4, PdfLinePitch.Estimate(lines), 3);
    }

    [Fact]
    public void An_unmeasurable_document_falls_back_to_a_plausible_leading()
    {
        Assert.Equal(PdfLinePitch.MinimumPitch, PdfLinePitch.Estimate([]));
        Assert.Equal(PdfLinePitch.MinimumPitch, PdfLinePitch.Estimate(Page(("One line only", 0))));

        // Nothing a document says makes six line-heights a leading.
        Assert.Equal(PdfLinePitch.MaximumPitch,
            PdfLinePitch.Estimate(Page(("Far", 0), ("Apart", 60), ("Again", 60))));
    }

    // ---- what may and may not be merged --------------------------------------------------------

    [Fact]
    public void A_wrapped_paragraph_stays_one_block()
    {
        var lines = Page(
            ("Professor Feenstra presented the empirical findings of an analysis", 0),
            ("aimed at simplifying the current methodology used by the tables", 14),
            ("for estimating imports and exports", 14));

        var blocks = Group(lines);

        Assert.Single(blocks);
        Assert.Equal(3, blocks[0].LineCount);
    }

    [Fact]
    public void A_wrapped_heading_stays_one_block()
    {
        // The case the line fix exposed: a heading long enough to wrap, with the paragraph beneath
        // it a full break away.
        var lines = Page(
            ("2. A Survey Based Approach to Adjustment for Quality in International Price", 0),
            ("Comparisons", 14),
            ("Professor Abe presented a research paper written in collaboration with", 20));

        var blocks = Group(lines);

        Assert.Equal(2, blocks.Count);
        Assert.Equal(2, blocks[0].LineCount);
        Assert.Contains("Comparisons", blocks[0].Text);
    }

    [Fact]
    public void Consecutive_headings_stay_separate()
    {
        // The two headings sit a break apart, with body text around them so the page says what its
        // leading is. Two lines alone could not: a lone gap is the only gap, so it is the leading
        // by definition, and no rule reading the document can say otherwise.
        var lines = Page(
            ("Session IV: TAG Functioning and Terms of Reference for Task Forces", 0),
            ("1. TAG Composition and Terms of Reference", 20),
            ("The chair introduced the draft terms of reference and invited the group", 20),
            ("to comment on the composition of the task force", 14));

        var blocks = Group(lines);

        Assert.Equal(3, blocks.Count);
        Assert.Equal([1, 1, 2], blocks.Select(block => block.LineCount).ToArray());
    }

    [Fact]
    public void A_hanging_indent_continuation_stays_with_its_line()
    {
        // The continuation is indented, which is what a hanging indent means. Indentation is not
        // evidence of a new block on its own, and the gap is still one leading.
        var lines = new[]
        {
            Line("(a) the price of a good expressed in the currency of the country", page: 1, y: 700),
            Line("in which it was collected, adjusted for quality", page: 1, y: 686, left: 96),
        };

        var blocks = Group(lines);

        Assert.Single(blocks);
        Assert.Equal(2, blocks[0].LineCount);
    }

    [Fact]
    public void A_small_gap_alone_is_not_enough_to_merge()
    {
        // One leading apart in every case, and none of them continues the line above.
        var differentFont = new[]
        {
            Line("Heading in one face", page: 1, y: 700),
            Line("Body in another", page: 1, y: 686, fontName: "sans"),
        };
        var differentSize = new[]
        {
            Line("Heading at one size", page: 1, y: 700),
            Line("Body at another", page: 1, y: 686, fontSize: 18),
        };
        var acrossAPage = new[]
        {
            Line("Last line of page one", page: 1, y: 700),
            Line("first line of page two", page: 2, y: 686),
        };
        var afterASentence = new[]
        {
            Line("This sentence ends here.", page: 1, y: 700),
            Line("This next one must not merge.", page: 1, y: 686),
        };

        Assert.Equal(2, Group(differentFont).Count);
        Assert.Equal(2, Group(differentSize).Count);
        Assert.Equal(2, Group(acrossAPage).Count);
        Assert.Equal(2, Group(afterASentence).Count);
    }

    [Fact]
    public void Grouping_is_a_pure_function_of_its_input()
    {
        var lines = Page(
            ("A heading", 0),
            ("a paragraph that wraps", 20),
            ("onto a second line", 14),
            ("Another heading", 20));

        var first = Group(lines);
        var second = Group(lines);

        Assert.Equal(
            first.Select(block => $"{block.Page}|{block.TopY:F4}|{block.Text}"),
            second.Select(block => $"{block.Page}|{block.TopY:F4}|{block.Text}"));
        Assert.Equal([1, 2, 1], first.Select(block => block.LineCount).ToArray());
    }

    // ---- what the candidate would change on a real document ------------------------------------

    // ---- helpers -------------------------------------------------------------------------------

    private static IReadOnlyList<PdfLayoutBlock> Group(IReadOnlyList<PdfLine> lines) =>
        PdfLayoutBlockGrouper.Build(lines.Select(Ann).ToArray());

    /// <summary>A page of lines, each placed a stated number of points below the one before.</summary>
    private static PdfLine[] Page(params (string Text, double Below)[] entries)
    {
        var lines = new List<PdfLine>();
        var y = 700.0;
        foreach (var entry in entries)
        {
            y -= entry.Below;
            lines.Add(Line(entry.Text, page: 1, y: y));
        }

        return [.. lines];
    }

    private static PdfLineBlockAnnotation Ann(PdfLine line) => new(line);

    private static PdfLine Line(
        string text, int page, double y,
        double fontSize = 10, double left = 72, string fontName = "serif") => new(
        Page: page,
        Y: y,
        FontSize: fontSize,
        Text: text,
        BoldRatio: 0.8,
        LeadingBoldPrefix: "",
        ItalicRatio: 0,
        Left: left,
        Right: 420,
        FontName: fontName,
        FillColorKey: "0.00,0.20,0.40",
        Bottom: y - 5,
        Top: y + 5);
}
