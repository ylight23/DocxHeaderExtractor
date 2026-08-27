using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// M8.1d-2 locks: a numeric path the parser accepted keeps every component it observed.
/// <para>
/// These are representation locks only. The companion assertions here verify the negative half of
/// the same contract: recovering components must not, by itself, create a hierarchy relation that
/// did not exist before. Promoting recovered components to ancestry authority is a later step.
/// </para>
/// </summary>
public sealed class MarkerComponentRepresentationTests
{
    [Theory]
    [InlineData("4.3.2. Handling a Received Validation Request", new[] { 4, 3, 2 })]
    [InlineData("4.3 Validation", new[] { 4, 3 })]
    [InlineData("4 Constructing Responses from Caches", new[] { 4 })]
    [InlineData("4 3 2 Handling a Received Validation Request", new[] { 4, 3, 2 })]
    [InlineData("4 3 Validation", new[] { 4, 3 })]
    public void NumericPathKeepsEveryObservedComponent(string raw, int[] expected)
    {
        var marker = PdfMarkerFactsParser.Parse(raw);

        Assert.NotNull(marker);
        Assert.Equal(expected, marker!.Value.Components);
    }

    /// <summary>Depth is the component count by construction, not a separately parsed number.</summary>
    [Theory]
    [InlineData("4.3.2. Handling a Received Validation Request")]
    [InlineData("4 3 2 Handling a Received Validation Request")]
    [InlineData("4 3 Validation")]
    [InlineData("09 30 Opening remarks")]
    [InlineData("13 00 14 00 Lunch break")]
    public void DepthAgreesWithComponentCountWhenComponentsExist(string raw)
    {
        var marker = PdfMarkerFactsParser.Parse(raw);

        Assert.NotNull(marker);
        Assert.False(marker!.Value.Components.IsDefaultOrEmpty);
        Assert.Equal(marker.Value.Components.Length, marker.Value.Depth);
    }

    /// <summary>
    /// A non-arabic marker has no path. It must stay component-free rather than be flattened into a
    /// one-element path it never carried.
    /// </summary>
    [Theory]
    [InlineData("Chapter II Overview")]
    [InlineData("Article 5 Obligations")]
    public void NonArabicMarkersCarryNoComponents(string raw)
    {
        var marker = PdfMarkerFactsParser.Parse(raw);

        if (marker is null) return;
        Assert.False(marker.Value.IsPath);
        Assert.True(marker.Value.Components.IsDefaultOrEmpty);
    }

    /// <summary>
    /// The representation repair must not become an ancestry grant. A dot-stripped child whose
    /// ancestors are present in source order still resolves no parent at this stage, because
    /// MarkerPath and the ancestor pool deliberately still run on the strict grammar.
    /// </summary>
    [Fact]
    public void RecoveredComponentsDoNotCreateAncestryOnTheirOwn()
    {
        var texts = new[] { "4 Constructing Responses", "4 3 Validation", "4 3 2 Sending a Validation Request" };
        var contexts = new Dictionary<string, PdfCandidateContext>(StringComparer.Ordinal);
        var headings = new List<PdfValidatedHeading>();
        for (var index = 0; index < texts.Length; index++)
        {
            var id = $"b{index}";
            contexts[id] = Context(id, 1, 700 - index * 20, texts[index]);
            headings.Add(new PdfValidatedHeading(id, new TextOffsetSpan(0, texts[index].Length),
                PdfBlockRole.HeadingTopic, "document_body", "test"));
        }

        var facts = PdfHierarchyFactsInventory.Inspect(headings, contexts);

        Assert.All(facts, fact => Assert.Null(fact.MarkerPrefixParentCandidate));
        Assert.All(facts, fact => Assert.Equal("relationship_unresolved", fact.ParentResolution));
    }

    /// <summary>Components reach the audit intact even where the strict path is truncated.</summary>
    [Fact]
    public void AuditExposesCompleteComponentsAlongsideStrictPath()
    {
        const string text = "4 3 2 Sending a Validation Request";
        var contexts = new Dictionary<string, PdfCandidateContext>(StringComparer.Ordinal)
        {
            ["only"] = Context("only", 1, 700, text),
        };

        var fact = Assert.Single(PdfHierarchyFactsInventory.Inspect(
            [new PdfValidatedHeading("only", new TextOffsetSpan(0, text.Length), PdfBlockRole.HeadingTopic,
                "document_body", "test")],
            contexts));

        Assert.Equal([4, 3, 2], fact.MarkerComponents);
        Assert.Equal(3, fact.MarkerDepth);
        Assert.Equal(text, fact.SourceBlockText);
    }

    private static PdfCandidateContext Context(string id, int page, double topY, string text)
    {
        var source = new PdfSourceFacts(id, text, page, 1, 72, topY, 400, topY - 12, "document_body", []);
        return new PdfCandidateContext(source, [], [], [], "document_body", []);
    }
}
