using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// M8.1d-1 safety fixtures, written before any marker representation change.
/// <para>
/// They separate two independent concerns the trace exposed. Representation asks whether a marker
/// the parser already accepted keeps its components. Detection asks whether a numeric sequence
/// should have been accepted at all. Repairing representation must never turn a detection
/// false positive into a stronger, fully-formed hierarchy claim, so these locks are written first.
/// </para>
/// <para>
/// No corpus counts appear here. The fixtures are lexical shapes, not documents.
/// </para>
/// </summary>
public sealed class MarkerHierarchySafetyFixtureTests
{
    /// <summary>Dotted arabic is the only shape allowed to carry the strict family.</summary>
    [Theory]
    [InlineData("4.3.2. Handling a Received Validation Request", 3)]
    [InlineData("4.3 Validation", 2)]
    [InlineData("4 Constructing Responses from Caches", 1)]
    public void DottedArabicKeepsStrictFamilyAndDepth(string raw, int expectedDepth)
    {
        var marker = PdfMarkerFactsParser.Parse(raw);

        Assert.NotNull(marker);
        Assert.Equal("arabic", marker!.Value.Family);
        Assert.Equal(expectedDepth, marker.Value.Depth);
    }

    /// <summary>
    /// Space-separated numeric runs are recovered evidence, never the strict family. A later
    /// representation fix may give them components; it may not promote them to strict evidence.
    /// </summary>
    [Theory]
    [InlineData("4 3 2 Handling a Received Validation Request")]
    [InlineData("4 3 Validation")]
    [InlineData("09 30 Opening remarks")]
    [InlineData("13 00 14 00 Lunch break")]
    [InlineData("192 168 1 1 Gateway address")]
    [InlineData("3 14 Approximation of pi")]
    [InlineData("1 2 Some ordinary prose sentence continues here")]
    public void SpacedNumericRunNeverBecomesStrictArabic(string raw)
    {
        var marker = PdfMarkerFactsParser.Parse(raw);

        if (marker is null) return;
        Assert.NotEqual("arabic", marker.Value.Family);
    }

    /// <summary>
    /// Lexically these are indistinguishable from a real spaced heading marker, so the guarantee
    /// cannot be "the parser rejects them". The guarantee is that weak evidence alone does not
    /// create an ancestry relation when the claimed ancestor was never observed.
    /// </summary>
    [Theory]
    [InlineData("13 00 14 00 Lunch break")]
    [InlineData("192 168 1 1 Gateway address")]
    [InlineData("3 14 Approximation of pi")]
    [InlineData("1 2 Some ordinary prose sentence continues here")]
    public void WeakNumericEvidenceWithoutObservedAncestorResolvesNoParent(string raw)
    {
        var contexts = new Dictionary<string, PdfCandidateContext>(StringComparer.Ordinal)
        {
            ["only"] = Context("only", 1, 700, raw),
        };

        var facts = PdfHierarchyFactsInventory.Inspect([Heading("only", raw)], contexts);

        var fact = Assert.Single(facts);
        Assert.Null(fact.MarkerPrefixParentCandidate);
        Assert.Equal("relationship_unresolved", fact.ParentResolution);
    }

    /// <summary>A marker family alone must not manufacture a heading, a parent, or an extra row.</summary>
    [Fact]
    public void InventoryNeverAddsHeadingsAndNeverRewritesSource()
    {
        const string first = "4 3 Validation";
        const string second = "13 00 14 00 Lunch break";
        var contexts = new Dictionary<string, PdfCandidateContext>(StringComparer.Ordinal)
        {
            ["a"] = Context("a", 1, 700, first),
            ["b"] = Context("b", 1, 680, second, scope: "table"),
        };

        var facts = PdfHierarchyFactsInventory.Inspect([Heading("a", first), Heading("b", second)], contexts);

        Assert.Equal(2, facts.Count);
        Assert.Equal([first, second], facts.Select(fact => fact.SourceBlockText));
        Assert.Equal(["document_body", "table"], facts.Select(fact => fact.StructuralScope));
        Assert.All(facts, fact => Assert.Equal("document_body", fact.DocumentRegime));
        Assert.All(facts, fact => Assert.Null(fact.MarkerPrefixParentCandidate));
    }

    /// <summary>
    /// Scope is authority, not a hint: a numerically adjacent pair split across scopes must not
    /// become an ancestry relation however the marker is represented.
    /// </summary>
    [Fact]
    public void NumericAncestryNeverCrossesScopeBoundary()
    {
        const string parent = "4 Constructing Responses from Caches";
        const string child = "4 3 Validation";
        var contexts = new Dictionary<string, PdfCandidateContext>(StringComparer.Ordinal)
        {
            ["parent"] = Context("parent", 1, 700, parent, scope: "table_of_contents"),
            ["child"] = Context("child", 2, 700, child),
        };

        var facts = PdfHierarchyFactsInventory.Inspect([Heading("parent", parent), Heading("child", child)], contexts);

        var resolved = facts.Single(fact => fact.Id == "child");
        Assert.Null(resolved.MarkerPrefixParentCandidate);
        Assert.Equal("relationship_unresolved", resolved.ParentResolution);
    }

    private static PdfValidatedHeading Heading(string id, string text) =>
        new(id, new TextOffsetSpan(0, text.Length), PdfBlockRole.HeadingTopic, "document_body", "test");

    private static PdfCandidateContext Context(string id, int page, double topY, string text,
        string scope = "document_body")
    {
        var source = new PdfSourceFacts(id, text, page, 1, 72, topY, 400, topY - 12, scope, []);
        return new PdfCandidateContext(source, [], [], [], "document_body", []);
    }
}
