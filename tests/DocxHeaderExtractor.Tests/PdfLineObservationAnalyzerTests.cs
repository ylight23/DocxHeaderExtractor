using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The analyzer reports positions and recurrence counts only. It must not classify a line as a
/// running header, page number or table row: that reading belongs to the model.
/// </summary>
public sealed class PdfLineObservationAnalyzerTests
{
    [Fact]
    public void Reports_recurrence_counts_for_every_line_without_classifying_it()
    {
        var lines = new List<PdfLine>();
        for (var page = 1; page <= 6; page++)
        {
            lines.Add(Line("Annual Financial Report", page, y: 790));
            lines.Add(Line(page.ToString(), page, y: 20));
        }
        lines.Add(Line("AVAILABILITY OF INFORMATION", 2, y: 700));

        var annotations = PdfLineObservationAnalyzer.Analyze(lines);

        Assert.Equal(lines.Count, annotations.Count);
        var header = annotations.First(a => a.Line.Text == "Annual Financial Report");
        Assert.Equal(6, header.SameNormalizedTextPageCount);
        Assert.Equal(1, header.SameNormalizedTextFirstPage);
        Assert.Equal(6, header.SameNormalizedTextLastPage);
        var topic = annotations.Single(a => a.Line.Text == "AVAILABILITY OF INFORMATION");
        Assert.Equal(1, topic.SameNormalizedTextPageCount);
    }

    [Fact]
    public void Vertical_position_is_a_measurement_from_the_lowest_to_the_highest_text()
    {
        var annotations = PdfLineObservationAnalyzer.Analyze([
            Line("top", 1, y: 800),
            Line("middle", 1, y: 450),
            Line("bottom", 1, y: 100),
        ]);

        Assert.Equal(1.0, annotations.Single(a => a.Line.Text == "top").VerticalPosition);
        Assert.Equal(0.5, annotations.Single(a => a.Line.Text == "middle").VerticalPosition);
        Assert.Equal(0.0, annotations.Single(a => a.Line.Text == "bottom").VerticalPosition);
    }

    private static PdfLine Line(string text, int page, double y) => new(
        Page: page,
        Y: y,
        FontSize: 12,
        Text: text,
        BoldRatio: 0,
        LeadingBoldPrefix: "",
        ItalicRatio: 0,
        Left: 72,
        Right: 420,
        FontName: "times",
        FillColorKey: "");
}
