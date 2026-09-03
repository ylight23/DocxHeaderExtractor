using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfHierarchyFactsInventoryTests
{
    [Fact]
    public void InventoriesOnlyValidatedHeadingsAndKeepsUnmarkedRelationshipUnresolved()
    {
        var chapter = Context("chapter", 1, 700, "1. Chapter", new PdfMarkerFact("Arabic:1", 1, "arabic", true));
        var section = Context("section", 1, 680, "1.1 Scope", new PdfMarkerFact("Arabic:2", 2, "arabic", true));
        var plain = Context("plain", 1, 660, "Topic without marker", null);
        var contexts = new Dictionary<string, PdfCandidateContext>(StringComparer.Ordinal)
        {
            ["chapter"] = chapter,
            ["section"] = section,
            ["plain"] = plain,
        };
        var validated = new[]
        {
            Heading("chapter"),
            Heading("section"),
            Heading("plain"),
        };

        var facts = PdfHierarchyFactsInventory.Inspect(validated, contexts);

        Assert.Equal(new[] { "chapter", "section", "plain" }, facts.Select(fact => fact.Id));
        Assert.Equal("chapter", facts.Single(fact => fact.Id == "section").MarkerPrefixParentCandidate);
        Assert.Contains("marker_depth:2", facts.Single(fact => fact.Id == "section").Evidence);
        var unmarked = facts.Single(fact => fact.Id == "plain");
        Assert.Null(unmarked.MarkerPrefixParentCandidate);
        Assert.Equal("relationship_unresolved", unmarked.ParentResolution);
        Assert.Contains("relationship_unresolved", unmarked.Evidence);
    }

    [Fact]
    public void DoesNotCrossScopeBoundaryForMatchingMarkerPrefix()
    {
        var tocParent = Context("toc-parent", 1, 700, "1. Contents", new PdfMarkerFact("Arabic:1", 1, "arabic", true), "table_of_contents");
        var bodyChild = Context("body-child", 2, 700, "1.1 Scope", new PdfMarkerFact("Arabic:2", 2, "arabic", true), "document_body");
        var contexts = new Dictionary<string, PdfCandidateContext>(StringComparer.Ordinal)
        {
            ["toc-parent"] = tocParent,
            ["body-child"] = bodyChild,
        };

        var facts = PdfHierarchyFactsInventory.Inspect([Heading("toc-parent"), Heading("body-child")], contexts);

        var child = facts.Single(fact => fact.Id == "body-child");
        Assert.Null(child.MarkerPrefixParentCandidate);
        Assert.Equal("relationship_unresolved", child.ParentResolution);
    }

    private static PdfValidatedHeading Heading(string id) => new(id, new TextOffsetSpan(0, 1), PdfBlockRole.HeadingTopic,
        "document_body", "test");

    private static PdfCandidateContext Context(string id, int page, double topY, string text, PdfMarkerFact? marker,
        string scope = "document_body")
    {
        var source = new PdfSourceFacts(id, text, page, 1, 72, topY, 400, topY - 12, scope, [])
        {
            Marker = marker,
        };
        return new PdfCandidateContext(source, [], [], [], "document_body", []);
    }
}
