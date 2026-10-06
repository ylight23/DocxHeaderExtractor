using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Projection;

namespace DocxHeaderExtractor.Tests;

public class OutlineFormatterTests
{
    [Fact]
    public void Every_format_keeps_every_heading_verbatim_without_lexical_collapse()
    {
        // Repeats are the model's call (same-node relations), not a formatter's text match.
        var outline = new DocumentOutline
        {
            File = "sample.docx",
            ParagraphCount = 20,
            SourceCount = 3,
            Headings =
            [
                Heading(1, 1, "New Administration Agreements"),
                Heading(3, 1, "New Administration Agreements (Cont'd)"),
            ],
        };

        var text = OutlineFormatter.Format(outline, OutlineFormat.Text);
        var markdown = OutlineFormatter.Format(outline, OutlineFormat.Markdown);
        var json = OutlineFormatter.Format(outline, OutlineFormat.Json);

        Assert.Equal(2, text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Contains("New Administration Agreements (Cont'd)", text);
        Assert.Contains("New Administration Agreements (Cont'd)", markdown);
        Assert.Contains("New Administration Agreements (Cont'd)", json);
        Assert.DoesNotContain("navigationCollapsed", json);
    }

    /// <summary>M9.5a: an unresolved level (null) must format without crashing in every output shape.</summary>
    [Fact]
    public void UnresolvedLevelFormatsWithoutCrashingInEveryShape()
    {
        var outline = new DocumentOutline
        {
            File = "sample.docx",
            ParagraphCount = 5,
            SourceCount = 1,
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
        Source = HeadingSource.Model,
        Confidence = 1.0,
    };
}
