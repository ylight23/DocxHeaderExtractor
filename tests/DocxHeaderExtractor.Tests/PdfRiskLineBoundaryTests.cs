using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// What a risk classification is allowed to do to a source occurrence.
/// <para>
/// It may steer attention, routing, context and evidence. It may not delete source text, fuse a
/// risk occurrence into an unrelated clean one, or change semantic truth.
/// </para>
/// <para>
/// Retention alone is not the invariant. Keeping a repeated header in the universe and then letting
/// it merge into the heading below produces a source occurrence whose text no heading actually has
/// - a partial-span problem manufactured by the harness rather than found in the document. These
/// assert boundary fidelity, not just survival.
/// </para>
/// </summary>
public sealed class PdfRiskLineBoundaryTests
{
    [Fact]
    public void A_page_number_survives_the_source_universe_as_its_own_occurrence()
    {
        var heading = Line("2. Regional updates", y: 700);
        var pageNumber = Line("7", y: 682);
        var blocks = Build(
            Annotate(heading),
            Annotate(pageNumber, pageNumber: true));

        Assert.Equal(2, blocks.Count);
        Assert.Contains(blocks, block => block.Text.Trim() == "7");
        Assert.Contains(blocks, block => block.Text.Contains("Regional updates", StringComparison.Ordinal));
        Assert.DoesNotContain(blocks, block =>
            block.Text.Contains("Regional updates", StringComparison.Ordinal) &&
            block.Text.Contains('7', StringComparison.Ordinal));
    }

    [Fact]
    public void A_repeated_header_immediately_before_a_heading_does_not_merge_with_it()
    {
        // Same font, same left edge, 18pt apart: every geometric rule in CanMerge says join them.
        // Only the risk classification stops it, which is why the classification has to reach here.
        var header = Line("World Bank ICP Meeting", y: 700);
        var heading = Line("2. Regional updates", y: 682);
        var blocks = Build(
            Annotate(header, repeated: true, headerFooterZone: true),
            Annotate(heading));

        Assert.Equal(2, blocks.Count);
        var headingBlock = Assert.Single(blocks, block =>
            block.Text.Contains("Regional updates", StringComparison.Ordinal));
        Assert.DoesNotContain("World Bank", headingBlock.Text, StringComparison.Ordinal);
        Assert.Equal("2. Regional updates", headingBlock.Text);
    }

    [Fact]
    public void A_table_like_line_next_to_a_heading_leaves_the_heading_text_unchanged()
    {
        var heading = Line("DAY 2: WEDNESDAY, NOVEMBER 1, 2023", y: 700);
        var tableRow = Line("09:00 - 10:30    Opening    Room A", y: 682);
        var blocks = Build(
            Annotate(heading),
            Annotate(tableRow, tableLike: true));

        var headingBlock = Assert.Single(blocks, block =>
            block.Text.StartsWith("DAY 2", StringComparison.Ordinal));
        Assert.Equal("DAY 2: WEDNESDAY, NOVEMBER 1, 2023", headingBlock.Text);
        Assert.Contains(blocks, block => block.Text.Contains("Opening", StringComparison.Ordinal));
    }

    [Fact]
    public void Two_clean_lines_still_merge_so_the_guard_is_not_simply_disabling_grouping()
    {
        // The control. If risk handling had turned every line into its own block, the three tests
        // above would pass for the wrong reason.
        var first = Line("A very long heading that continues", y: 700);
        var second = Line("onto the following line", y: 682);

        var blocks = Build(Annotate(first), Annotate(second));

        var block = Assert.Single(blocks);
        Assert.Equal(2, block.LineCount);
    }

    [Fact]
    public void Excluding_risk_lines_still_removes_them_so_the_two_modes_stay_distinct()
    {
        var heading = Line("2. Regional updates", y: 700);
        var pageNumber = Line("7", y: 682);

        var kept = Build(Annotate(heading), Annotate(pageNumber, pageNumber: true));
        var dropped = PdfSemanticBlockGrouper.Build(
            [Annotate(heading), Annotate(pageNumber, pageNumber: true)], includeRiskLines: false);

        Assert.Equal(2, kept.Count);
        Assert.Single(dropped);
    }

    [Fact]
    public void No_parser_line_is_lost_from_the_source_universe()
    {
        // The retention half of the invariant, stated over every line rather than sampled.
        var lines = new[]
        {
            Annotate(Line("Running header", y: 760), repeated: true, headerFooterZone: true),
            Annotate(Line("1. Introduction", y: 700)),
            Annotate(Line("Some body text that follows the heading", y: 682)),
            Annotate(Line("Col A    Col B    Col C", y: 640), tableLike: true),
            Annotate(Line("12", y: 40), pageNumber: true),
        };

        var blocks = Build(lines);

        var emitted = blocks.SelectMany(block => block.Lines).Select(line => line.Text).ToArray();
        Assert.Equal(lines.Length, emitted.Length);
        Assert.All(lines, annotation => Assert.Contains(annotation.Line.Text, emitted));
    }

    private static IReadOnlyList<PdfSemanticBlock> Build(params PdfLineBlockAnnotation[] annotations) =>
        PdfSemanticBlockGrouper.Build(annotations, includeRiskLines: true);

    private static PdfLineBlockAnnotation Annotate(
        PdfLine line,
        bool repeated = false,
        bool headerFooterZone = false,
        bool tableLike = false,
        bool pageNumber = false) =>
        new(line, repeated, headerFooterZone, tableLike, pageNumber,
            repeated || headerFooterZone || tableLike || pageNumber ? "risk" : "semantic-candidate");

    private static PdfLine Line(string text, double y) =>
        new(Page: 1, Y: y, FontSize: 11, Text: text, BoldRatio: 0, LeadingBoldPrefix: "",
            ItalicRatio: 0, Left: 72, Right: 400, FontName: "Arial", FillColorKey: "k");
}
