using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfMarkerHierarchyResolverTests
{
    [Fact]
    public void LearnsLabelTiersFromDocumentOrderWithoutKnowingLabels()
    {
        var headings = new List<HeadingRecord>
        {
            Heading(1, "Abschnitt I. Overview"),
            Heading(2, "Article 1. Scope"),
            Heading(3, "Article 2. Definitions"),
        };

        PdfMarkerHierarchyResolver.Apply(headings);

        Assert.Equal(new int?[] { 1, 2, 2 }, headings.Select(h => h.Level));
    }

    [Fact]
    public void LearnsTiersFromLooseLabelAndNumberMarkers()
    {
        var headings = new List<HeadingRecord>
        {
            Heading(1, "Phan I Quy dinh chung"),
            Heading(2, "Muc 1 Pham vi"),
            Heading(3, "Muc 2 Doi tuong"),
        };

        PdfMarkerHierarchyResolver.Apply(headings);

        Assert.Equal(new int?[] { 1, 2, 2 }, headings.Select(h => h.Level));
    }

    [Fact]
    public void UsesExplicitArabicDepthWhenPresent()
    {
        var headings = new List<HeadingRecord>
        {
            Heading(1, "Chapter 1. General"),
            Heading(2, "1.2. Scope"),
        };

        PdfMarkerHierarchyResolver.Apply(headings);

        Assert.Equal(1, headings[0].Level);
        Assert.Equal(2, headings[1].Level);
    }

    [Fact]
    public void DoesNotInventAbsoluteLevelForSingleMarkerFamily()
    {
        var headings = new List<HeadingRecord> { Heading(1, "Article 1. Scope", level: 3) };

        PdfMarkerHierarchyResolver.Apply(headings);

        Assert.Equal(3, headings[0].Level);
    }

    private static HeadingRecord Heading(int index, string text, int level = 1) => new()
    {
        Index = index,
        Text = text,
        Level = level,
    };
}
