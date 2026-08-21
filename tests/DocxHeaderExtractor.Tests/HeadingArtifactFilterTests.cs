using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class HeadingArtifactFilterTests
{
    [Fact]
    public void RemovesMergedTableOfContentsBlobPromotedAsHeading()
    {
        var heading = Heading(
            "Section I – Instructions to Bidders (ITB) 4 Section I - Instructions to Bidders Contents 1. Scope .......... 5 2. Source of Funds .......... 6",
            basis: "evidence_not_calibrated");

        Assert.True(HeadingArtifactFilter.ShouldRemove(heading, Paragraph(heading.Text), out var reason));
        Assert.Equal("toc-blob", reason);
    }

    [Theory]
    [InlineData("The Project Manager is: _________________________________________")]
    [InlineData("Normal working hours are:___________________________________")]
    [InlineData("Kính gửi:..................................................................................")]
    [InlineData("…………………………………………………………………………………")]
    public void RemovesFormFillAndPureFillerHeadings(string text)
    {
        var heading = Heading(text, basis: "evidence_not_calibrated");

        Assert.True(HeadingArtifactFilter.ShouldRemove(heading, Paragraph(text), out var reason));
        Assert.Contains(reason, new[] { "form-fill-heading", "pure-filler" });
    }

    [Fact]
    public void KeepsCleanTitleFromPdfTocDictionaryEvenWhenOriginalTextContainsTocBlob()
    {
        var heading = Heading(
            "Financial Results",
            original: "TABLE OF CONTENTS Page Financial Results . . . . . . . . . . . . . . . . 11 Lending Activities . . . . . . . . 25",
            basis: PdfTocDictionaryOutline.Basis);

        Assert.False(HeadingArtifactFilter.ShouldRemove(heading, Paragraph(heading.OriginalText!), out _));
    }

    [Fact]
    public void KeepsGroundedTaggedPdfTitleEvenWhenItLooksLikeAFormFillArtifact()
    {
        var heading = Heading(
            "Schedule: _________________________________________",
            basis: PdfTaggedEvidenceOutline.Basis);

        Assert.False(HeadingArtifactFilter.ShouldRemove(heading, Paragraph(heading.Text), out _));
    }

    [Fact]
    public void ApplyRemovesOnlyArtifacts()
    {
        var keep = Heading("Section 2. Instructions to Consultants and Data Sheet", basis: "part_section_declared");
        var remove = Heading("Background _______________________________", basis: "evidence_not_calibrated");
        var doc = new SlimDocument
        {
            FileName = "x.docx",
            SourcePath = "x.docx",
            Paragraphs =
            [
                Paragraph(keep.Text, 1, "p1"),
                Paragraph(remove.Text, 2, "p2"),
            ],
        }.Build();
        var headings = new List<HeadingRecord>
        {
            Heading(keep.Text, keep.ConfidenceBasis, index: 1, stableId: "p1"),
            Heading(remove.Text, remove.ConfidenceBasis, index: 2, stableId: "p2"),
        };

        var result = HeadingArtifactFilter.Apply(headings, doc);

        Assert.Equal(1, result.Removed);
        Assert.Single(headings);
        Assert.Equal(keep.Text, headings[0].Text);
    }

    private static HeadingRecord Heading(
        string text,
        string basis,
        string? original = null,
        int index = 0,
        string stableId = "p0") => new()
        {
            Index = index,
            StableId = stableId,
            Level = 1,
            Text = text,
            OriginalText = original ?? text,
            Source = HeadingSource.Structure,
            Confidence = 0.5,
            ConfidenceBasis = basis,
        };

    private static SlimParagraph Paragraph(string text, int index = 0, string stableId = "p0") => new()
    {
        Index = index,
        StableId = stableId,
        Text = text,
        StyleId = "Normal",
    };
}
