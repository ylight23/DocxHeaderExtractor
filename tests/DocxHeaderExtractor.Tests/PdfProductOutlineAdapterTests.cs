using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// M9.5b compatibility shell locks. The adapter is structural copy only - it may not re-derive
/// RequiresReview, Level, or Text, and must leave fields the M9 lane has no authority for at their
/// honest default rather than guessing.
/// </summary>
public sealed class PdfProductOutlineAdapterTests
{
    [Fact]
    public void Compatibility_shell_preserves_canonical_paragraph_for_nonzero_span()
    {
        var product = new PdfProductHeading(
            "id", 4, "stable", new DocxTextSpan(7, 14), "HEADING", "Heading", 1, null, true, [],
            "prefix HEADING body");

        var heading = PdfProductOutlineAdapter.ToHeadingRecord(product);

        Assert.Equal(new TextOffsetSpan(7, 14), heading.HeadingSpan);
        Assert.Equal("prefix HEADING body", heading.OriginalText);
        Assert.Equal("HEADING", heading.OriginalText![heading.HeadingSpan!.Start..heading.HeadingSpan.End]);
    }
    [Fact]
    public void CopiesCanonicalAnchorFieldsVerbatim()
    {
        var heading = new PdfProductHeading(
            "@body[1]/p[10]#0-14", 10, "@body[1]/p[10]", new DocxTextSpan(0, 14),
            "1. Introduction", "Heading", 1, null, true, []);

        var record = PdfProductOutlineAdapter.ToHeadingRecord(heading);

        Assert.Equal(10, record.Index);
        Assert.Equal("@body[1]/p[10]", record.StableId);
        Assert.Equal("@body[1]/p[10]#0-14", record.SourceId);
        Assert.Equal("1. Introduction", record.Text);
        Assert.Equal(0, record.HeadingSpan!.Start);
        Assert.Equal(14, record.HeadingSpan.End);
        Assert.Equal(1, record.Level);
    }

    /// <summary>An unresolved level is copied as null, never guessed at the compatibility boundary.</summary>
    [Fact]
    public void UnresolvedLevelStaysNull()
    {
        var heading = new PdfProductHeading(
            "id", 0, null, new DocxTextSpan(0, 5), "Title", "Heading", null, null, true, []);

        var record = PdfProductOutlineAdapter.ToHeadingRecord(heading);

        Assert.Null(record.Level);
    }

    [Fact]
    public void RequiresReviewIsCopiedNotRecomputed()
    {
        var reviewNeeded = new PdfProductHeading(
            "id1", 0, null, new DocxTextSpan(0, 5), "Title", "Heading", 1, null, true, ["hierarchy_unresolved"]);
        var noReviewNeeded = new PdfProductHeading(
            "id2", 1, null, new DocxTextSpan(0, 5), "Other", "Heading", 1, null, false, []);

        Assert.Equal(HeadingDecisionStatus.RequiresReview, PdfProductOutlineAdapter.ToHeadingRecord(reviewNeeded).DecisionStatus);
        Assert.Equal(HeadingDecisionStatus.AutoAcceptedEvidence, PdfProductOutlineAdapter.ToHeadingRecord(noReviewNeeded).DecisionStatus);
    }

    [Fact]
    public void ReasonsBecomeTheAcceptanceSignature()
    {
        var heading = new PdfProductHeading(
            "id", 0, null, new DocxTextSpan(0, 5), "Title", "Heading", null, null, true,
            ["hierarchy_unresolved", "level_unresolved"]);

        var record = PdfProductOutlineAdapter.ToHeadingRecord(heading);

        Assert.Equal("hierarchy_unresolved,level_unresolved", record.AcceptanceSignature);
    }

    /// <summary>
    /// Fields the M9 lane carries no authority for stay at an honest default - never silently filled
    /// from legacy data, since the adapter reads only PdfProductHeading.
    /// </summary>
    [Fact]
    public void FieldsWithNoM9AuthorityStayAtHonestDefaults()
    {
        var heading = new PdfProductHeading(
            "id", 0, null, new DocxTextSpan(0, 5), "Title", "Heading", 1, null, true, []);

        var record = PdfProductOutlineAdapter.ToHeadingRecord(heading);

        Assert.Null(record.OriginalText);
        Assert.Null(record.InlineBody);
        Assert.Null(record.InlineBodySpan);
        Assert.Null(record.StyleId);
        Assert.Null(record.Evidence);
        Assert.False(record.ModelConfirmed);
        Assert.False(record.CriticConfirmed);
        Assert.False(record.Disputed);
        Assert.Equal(0, record.CalibrationSamples);
    }

    [Fact]
    public void ToHeadingRecordsPreservesProductOutputOrder()
    {
        var output = new PdfProductOutput("sha",
        [
            new PdfProductHeading("id2", 5, null, new DocxTextSpan(0, 1), "B", "Heading", 1, null, true, []),
            new PdfProductHeading("id1", 2, null, new DocxTextSpan(0, 1), "A", "Heading", 1, null, true, []),
        ]);

        var records = PdfProductOutlineAdapter.ToHeadingRecords(output);

        Assert.Equal(["B", "A"], records.Select(r => r.Text));
    }
}
