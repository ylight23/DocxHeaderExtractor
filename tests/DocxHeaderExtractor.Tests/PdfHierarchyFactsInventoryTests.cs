using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfHierarchyFactsInventoryTests
{
    [Fact]
    public void InventoriesOnlyValidatedHeadingsAndKeepsUnmarkedRelationshipUnresolved()
    {
        var chapter = Context("chapter", 1, 700, "1. Chapter", new SourceMarkerFact("Arabic:1", 1, "arabic", true));
        var section = Context("section", 1, 680, "1.1 Scope", new SourceMarkerFact("Arabic:2", 2, "arabic", true));
        var plain = Context("plain", 1, 660, "Topic without marker", null);
        var contexts = new Dictionary<string, PdfSemanticSourceContext>(StringComparer.Ordinal)
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
        Assert.Equal("chapter", facts.Single(fact => fact.Id == "section").MarkerPrefixParentId);
        Assert.Contains("marker_depth:2", facts.Single(fact => fact.Id == "section").Evidence);
        var unmarked = facts.Single(fact => fact.Id == "plain");
        Assert.Null(unmarked.MarkerPrefixParentId);
        Assert.Equal("relationship_unresolved", unmarked.ParentResolution);
        Assert.Contains("relationship_unresolved", unmarked.Evidence);
    }

    private static ValidatedHeading Heading(string id) => new(id, new TextOffsetSpan(0, 1), "REGION_STRUCTURE",
        "document_body", "test");

    private static PdfSemanticSourceContext Context(string id, int page, double topY, string text, SourceMarkerFact? marker,
        string scope = "document_body")
    {
        var source = new PdfSourceFacts(id, text, page, 1, 72, topY, 400, topY - 12, scope, [])
        {
            Marker = marker,
        };
        return new PdfSemanticSourceContext(source, [], [], [], "document_body");
    }
}
