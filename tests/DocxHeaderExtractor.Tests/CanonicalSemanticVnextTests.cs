using System.Text.Json;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Tests;

public sealed class CanonicalSemanticVnextTests
{
    [Fact]
    public void Contract_schema_has_semantic_fields_but_no_numeric_coordinates()
    {
        var json = JsonSerializer.Serialize(CanonicalSemanticContract.Schema());

        Assert.Contains("sourceAlias", json, StringComparison.Ordinal);
        Assert.Contains("isHeading", json, StringComparison.Ordinal);
        Assert.Contains("verbatimText", json, StringComparison.Ordinal);
        Assert.DoesNotContain("start", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("end", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("offset", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Binder_uses_exact_utf16_offsets_and_rejects_non_verbatim_text()
    {
        var catalog = new DocumentSourceCatalog([
            new DocumentSourceUnit("p1", 7, "😀 Heading", new SourceAnchor { SourceType = "docx", ParagraphId = "p1" }, new StructuralSpan(0, 10)),
        ]);
        var aliases = SemanticSourceAliasCatalog.FromCatalog(catalog);
        var proposals = new[]
        {
            new CanonicalSemanticProposal("S0001", true, "Heading", SemanticRole: "SECTION"),
            new CanonicalSemanticProposal("S0001", true, "Heading!", SemanticRole: "SECTION"),
        };

        var bound = CanonicalSemanticExactBinder.Bind(proposals, aliases, out var observations);

        var item = Assert.Single(bound);
        Assert.Equal(3, item.Start); // 😀 occupies two UTF-16 code units plus one space.
        Assert.Equal(10, item.End);
        Assert.Equal(CanonicalSemanticBindingStatus.Bound, observations[0].Status);
        Assert.Equal(CanonicalSemanticBindingStatus.NonVerbatimText, observations[1].Status);
    }

    [Fact]
    public void Resolver_keeps_repeated_occurrences_but_outline_projection_collapses_node()
    {
        var bound = new[]
        {
            new CanonicalSemanticBoundHeading("S0001", "p1", 1, "Financial Statements", "SECTION", "Heading", "document_body", [], 0, 19, true),
            new CanonicalSemanticBoundHeading("S0002", "p2", 2, "Financial Statements", "SECTION", "Heading", "continuation", [], 0, 19, true),
        };

        var graph = CanonicalSemanticGraphResolver.Resolve(bound);

        Assert.Equal(2, graph.Occurrences.Count);
        Assert.Equal("PRIMARY", graph.Occurrences[0].OccurrenceKind);
        Assert.Equal("CONTINUATION", graph.Occurrences[1].OccurrenceKind);
        Assert.Single(graph.OutlineProjection);
        Assert.Equal(graph.Occurrences[0].SemanticNodeId, graph.Occurrences[1].SemanticNodeId);
    }

    [Fact]
    public void Migrated_registry_has_v6_frozen_semantic_sources_and_no_exact_freeze()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "eval", "a99-closed-loop", "canonical-semantic-gold-vnext");
        root = Path.GetFullPath(root);
        if (!File.Exists(Path.Combine(root, "inventory.v1.json")))
            return; // The unit remains valid for package consumers that do not ship eval artifacts.

        using var inventory = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "inventory.v1.json")));
        var result = inventory.RootElement;
        Assert.Equal(21, result.GetProperty("totalTracked").GetInt32());
        Assert.Equal(21, result.GetProperty("frozenSemanticVnext").GetInt32());
        Assert.Equal(0, result.GetProperty("exactOccurrenceFrozen").GetInt32());
        Assert.Equal(21, result.GetProperty("documents").GetArrayLength());
    }
}
