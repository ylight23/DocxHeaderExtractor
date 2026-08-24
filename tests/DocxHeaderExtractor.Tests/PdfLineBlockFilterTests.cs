using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfLineBlockFilterTests
{
    [Fact]
    public void ExcludesRepeatedHeaderFooterPageNumbersAndNumericTableLinesButKeepsTopicPhrase()
    {
        var lines = new List<PdfLine>();
        for (var page = 1; page <= 6; page++)
        {
            lines.Add(Line("Annual Financial Report", page, y: 790));
            lines.Add(Line(page.ToString(), page, y: 20));
            lines.Add(Line("TOTAL $1,234 56% 2025", page, y: 430));
        }
        lines.Add(Line("AVAILABILITY OF INFORMATION", 2, y: 700));

        var annotations = PdfLineBlockFilter.Analyze(lines);
        var summary = PdfLineBlockFilter.Summarize(annotations);

        Assert.Equal(lines.Count, summary.TotalLines);
        Assert.True(summary.RepeatedLines >= 6);
        Assert.Equal(6, summary.PageNumberLines);
        Assert.True(summary.TableLikeLines >= 6);

        var topic = annotations.Single(a => a.Line.Text == "AVAILABILITY OF INFORMATION");
        Assert.False(topic.ExcludeFromSemanticSamples);
        Assert.Equal("semantic-candidate", topic.Reason);

        var repeatedHeader = annotations.First(a => a.Line.Text == "Annual Financial Report");
        var numericTable = annotations.First(a => a.Line.Text == "TOTAL $1,234 56% 2025");
        Assert.True(repeatedHeader.ExcludeFromCandidateGrouping);
        Assert.True(numericTable.ExcludeFromSemanticSamples);
        Assert.True(numericTable.ExcludeFromCandidateGrouping);
    }

    [Fact]
    public void RetainsNonRepeatedStructuralMarkerEvenWhenItsTextLooksTableLike()
    {
        var annotations = PdfLineBlockFilter.Analyze([
            Line("DAY 1: TUESDAY", 1, y: 740),
            Line("An nex 1 : Meeting Agenda", 1, y: 700),
            Line("ordinary body text", 1, y: 400),
        ]);

        var day = annotations.Single(annotation => annotation.Line.Text == "DAY 1: TUESDAY");
        var annex = annotations.Single(annotation => annotation.Line.Text == "An nex 1 : Meeting Agenda");

        Assert.True(day.TableLike);
        Assert.False(day.Repeated);
        Assert.False(day.ExcludeFromCandidateGrouping);
        Assert.True(annex.TableLike);
        Assert.False(annex.ExcludeFromCandidateGrouping);
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
