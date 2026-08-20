using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfSemanticBlockGrouperTests
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

        var blocks = PdfSemanticBlockGrouper.Build(lines);

        Assert.Equal(3, blocks.Count);
        Assert.Equal(2, blocks[0].LineCount);
        Assert.Contains("Expected Funding", blocks[0].Text);
        Assert.Equal("Recipients should retain this statement.", blocks[1].Text);
        Assert.Equal("This next sentence must not merge.", blocks[2].Text);
    }

    [Fact]
    public void IgnoresLinesExcludedByDeterministicFilter()
    {
        var annotations = new[]
        {
            Ann(Line("Heading Topic", page: 1, y: 700)),
            new PdfLineBlockAnnotation(
                Line("TOTAL $42 80%", page: 1, y: 680),
                Repeated: false,
                HeaderFooterZone: false,
                TableLike: true,
                PageNumber: false,
                Reason: "table-like"),
        };

        var blocks = PdfSemanticBlockGrouper.Build(annotations);

        Assert.Single(blocks);
        Assert.Equal("Heading Topic", blocks[0].Text);
    }

    private static PdfLineBlockAnnotation Ann(PdfLine line) =>
        new(line, Repeated: false, HeaderFooterZone: false, TableLike: false, PageNumber: false, Reason: "semantic-candidate");

    private static PdfLine Line(string text, int page, double y) => new(
        Page: page,
        Y: y,
        FontSize: 14,
        Text: text,
        BoldRatio: 0.8,
        LeadingBoldPrefix: "",
        ItalicRatio: 0,
        Left: 72,
        Right: 420,
        FontName: "serif",
        FillColorKey: "0.00,0.20,0.40");
}
