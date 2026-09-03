using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Projection;

namespace DocxHeaderExtractor.Tests;

public class OutlineFormatterTests
{
    [Fact]
    public void NavigationHeadings_CollapsesSiblingContinuationTitles()
    {
        var outline = new[]
        {
            Heading(10, 1, "Investments"),
            Heading(12, 2, "Cash and Investments"),
            Heading(14, 2, "Cash and Investments (cont'd)"),
            Heading(16, 2, "Cash and Investments (Cont'd)"),
            Heading(18, 2, "Commitment Authority"),
        };

        var nav = OutlineFormatter.NavigationHeadings(outline);

        Assert.Equal(
            ["Investments", "Cash and Investments", "Commitment Authority"],
            nav.Select(h => h.Text).ToArray());
    }

    [Fact]
    public void NavigationHeadings_KeepsSameTitleAtDifferentLevels()
    {
        var outline = new[]
        {
            Heading(50, 1, "Cost Recovery"),
            Heading(50, 2, "Cost Recovery"),
            Heading(52, 2, "Cost Recovery"),
        };

        var nav = OutlineFormatter.NavigationHeadings(outline);

        Assert.Equal(
            ["Cost Recovery", "Cost Recovery"],
            nav.Select(h => h.Text).ToArray());
        Assert.Equal([1, 2], nav.Select(h => h.Level).ToArray());
    }

    [Fact]
    public void TextFormat_UsesNavigationHeadingsButJsonKeepsSourceHeadings()
    {
        var outline = new DocumentOutline
        {
            File = "sample.docx",
            ParagraphCount = 20,
            CandidateCount = 3,
            Headings =
            [
                Heading(1, 1, "New Administration Agreements"),
                Heading(3, 1, "New Administration Agreements (Cont'd)"),
            ],
        };

        var text = OutlineFormatter.Format(outline, OutlineFormat.Text);
        var json = OutlineFormatter.Format(outline, OutlineFormat.Json);

        Assert.Single(text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("New Administration Agreements (Cont'd)", json);
        Assert.Contains("\"navigationCollapsedCount\": 1", json);
        Assert.Contains("\"navigationCollapsedFromIndexes\"", json);
        Assert.Contains("\"index\": 3", json);
    }

    /// <summary>M9.5a: an unresolved level (null) must format without crashing in every output shape.</summary>
    [Fact]
    public void UnresolvedLevelFormatsWithoutCrashingInEveryShape()
    {
        var outline = new DocumentOutline
        {
            File = "sample.docx",
            ParagraphCount = 5,
            CandidateCount = 1,
            Headings = [Heading(2, null, "Untitled section")],
        };

        var json = OutlineFormatter.Format(outline, OutlineFormat.Json);
        var markdown = OutlineFormatter.Format(outline, OutlineFormat.Markdown);
        var text = OutlineFormatter.Format(outline, OutlineFormat.Text);
        var xml = OutlineFormatter.Format(outline, OutlineFormat.Xml);
        var csv = OutlineFormatter.Format(outline, OutlineFormat.Csv);

        Assert.Contains("\"level\": null", json);
        Assert.Contains("Untitled section", markdown);
        Assert.Contains("Untitled section", text);
        Assert.Contains("Untitled section", xml);
        Assert.Contains("Untitled section", csv);
    }

    private static HeadingRecord Heading(int index, int? level, string text) => new()
    {
        Index = index,
        Level = level,
        Text = text,
        Source = HeadingSource.Heuristic,
        Confidence = 1.0,
    };
}
