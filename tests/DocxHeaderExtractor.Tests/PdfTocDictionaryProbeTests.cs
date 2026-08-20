using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfTocDictionaryProbeTests
{
    [Fact]
    public void ProbeAnchorsDotLeaderTocEntriesByCanonicalPageMatch()
    {
        var lines = new[]
        {
            Line(10, 700, "TABLE OF CONTENTS"),
            Line(10, 680, "Introduction ............................................................... 4"),
            Line(10, 660, "Financial Results ........................................................ 17"),
            Line(10, 640, "Risk Management .......................................................... 42"),
            Line(10, 620, "Appendix ................................................................ 67"),
            Line(10, 600, "Administration of IDA .................................................... 63"),
            Line(4, 700, "I ntrod u ction"),
            Line(17, 700, "Financial Results an d Portfol io Performan ce"),
            Line(42, 700, "Risk Management"),
            Line(63, 700, "Administration of IDA"),
            Line(67, 700, "Appendix"),
        };

        var result = PdfTocDictionaryProbe.Analyze(lines);

        Assert.Equal(10, result.TocPage);
        Assert.Equal(5, result.Entries);
        Assert.Equal(5, result.ExactPageAnchors);
        Assert.Equal(5, result.RelaxedPageAnchors);
        Assert.Equal(5, result.AtOrAfterPageAnchors);
        Assert.Contains(result.Items, i => i.Title == "Introduction" && i.ExactAnchorPage == 4);
    }

    [Fact]
    public void ProbeAcceptsLooseEntriesOnlyOnExplicitTocPage()
    {
        var lines = new[]
        {
            Line(99, 700, "TABLE OF CONTENTS"),
            Line(99, 680, "Overview 4"),
            Line(99, 660, "Executive Summary 7"),
            Line(99, 640, "IDA’s Financial Resources 1 0"),
            Line(99, 620, "Financial Results 1 7"),
            Line(99, 600, "Risk Management 4 2"),
            Line(4, 700, "SECTION I: OVERVIEW"),
            Line(7, 700, "SECTION II: EXECUTIVE SUMMARY"),
            Line(10, 300, "SECTION III: IDA’ S FINANCIAL RESOURCES"),
            Line(17, 700, "SECTION IV: FINANCIAL RESULTS"),
            Line(42, 700, "Risk Management"),
            Line(11, 700, "This ordinary body line ended in 2025"),
        };

        var result = PdfTocDictionaryProbe.Analyze(lines);

        Assert.Equal(5, result.Entries);
        Assert.Equal(5, result.ExactPageAnchors);
        Assert.Equal(5, result.RelaxedPageAnchors);
        Assert.DoesNotContain(result.Items, i => i.Title.Contains("ordinary", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ProbeMarksIndexToEntryAsRelaxedAnchorWhenTargetPageUsesShorterTitle()
    {
        var lines = new[]
        {
            Line(99, 700, "TABLE OF CONTENTS"),
            Line(99, 680, "Overview 4"),
            Line(99, 660, "Executive Summary 7"),
            Line(99, 640, "Financial Results 17"),
            Line(99, 620, "Risk Management 42"),
            Line(99, 600, "Index to Financial Statements and Internal Control Reports 70"),
            Line(4, 700, "Overview"),
            Line(7, 700, "Executive Summary"),
            Line(17, 700, "Financial Results"),
            Line(42, 700, "Risk Management"),
            Line(70, 700, "FINANCIAL STATEMENTS AND INTERNAL CONTROL REPORTS"),
        };

        var result = PdfTocDictionaryProbe.Analyze(lines);
        var index = Assert.Single(result.Items, i => i.Title.StartsWith("Index to", StringComparison.Ordinal));

        Assert.Null(index.ExactAnchorPage);
        Assert.Equal(70, index.RelaxedAnchorPage);
        Assert.Equal(4, result.ExactPageAnchors);
        Assert.Equal(5, result.RelaxedPageAnchors);
    }

    [Fact]
    public void ProbeRelaxedAnchorAllowsPrefixBeforeQualifierOnExactPage()
    {
        var lines = new[]
        {
            Line(99, 700, "TABLE OF CONTENTS"),
            Line(99, 680, "Overview 4"),
            Line(99, 660, "Executive Summary 7"),
            Line(99, 640, "Financial Results 17"),
            Line(99, 620, "Risk Management 42"),
            Line(99, 600, "Affiliated Organizations—IFC, IDA and MIGA 79"),
            Line(4, 700, "Overview"),
            Line(7, 700, "Executive Summary"),
            Line(17, 700, "Financial Results"),
            Line(42, 700, "Risk Management"),
            Line(79, 700, "SECTION XV: AFFILIATED ORGANIZATIONS—IDA, IFC AND MIGA"),
        };

        var result = PdfTocDictionaryProbe.Analyze(lines);
        var affiliated = Assert.Single(result.Items, i => i.Title.StartsWith("Affiliated", StringComparison.Ordinal));

        Assert.Null(affiliated.ExactAnchorPage);
        Assert.Equal(79, affiliated.RelaxedAnchorPage);
        Assert.Equal(4, result.ExactPageAnchors);
        Assert.Equal(5, result.RelaxedPageAnchors);
    }

    private static PdfLine Line(int page, double y, string text) => new(
        Page: page,
        Y: y,
        FontSize: 12,
        Text: text,
        BoldRatio: 0,
        LeadingBoldPrefix: "",
        ItalicRatio: 0,
        Left: 72,
        Right: 500,
        FontName: "serif",
        FillColorKey: "0.00,0.00,0.00");
}
