using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class P3cSemanticIdentityHierarchyBoundaryTests
{
    [Fact]
    public void Explicit_same_node_and_continuation_relations_share_identity()
    {
        var bound = new[]
        {
            Heading("S0001", "p1", 1, "SESSION V: Current Research", "same-node:session-v"),
            Heading("S0002", "p2", 2, "SESSION V: Current Research (Cont'd)", "same-node:session-v")
                with { Scope = "continuation" },
        };

        var graph = CanonicalSemanticIdentityResolver.Resolve(bound);

        Assert.Equal(2, graph.Occurrences.Count);
        Assert.Equal(graph.Occurrences[0].SemanticNodeId, graph.Occurrences[1].SemanticNodeId);
        Assert.Equal("CONTINUATION", graph.Occurrences[1].OccurrenceKind);
        Assert.Single(graph.OutlineProjection);
    }

    [Fact]
    public void Identical_text_without_an_explicit_identity_relation_remains_distinct()
    {
        var bound = new[]
        {
            Heading("S0001", "p1", 1, "Agenda"),
            Heading("S0002", "p2", 2, "Agenda"),
        };

        var graph = CanonicalSemanticIdentityResolver.Resolve(bound);

        Assert.NotEqual(graph.Occurrences[0].SemanticNodeId, graph.Occurrences[1].SemanticNodeId);
        Assert.Equal(2, graph.OutlineProjection.Count);
    }

    [Fact]
    public void Hierarchy_consumes_identity_key_but_parent_and_level_remain_separate()
    {
        var bound = new[]
        {
            Heading("S0001", "p1", 1, "Session", "parent-node:ROOT", "same-node:session"),
            Heading("S0002", "p2", 2, "Session continued", "parent-node:S0001", "same-node:session"),
        };

        var identityKey = CanonicalSemanticIdentityResolver.CreateNodeKey(bound[0]);
        var secondIdentityKey = CanonicalSemanticIdentityResolver.CreateNodeKey(bound[1]);
        var hierarchy = ModelRelationHierarchyResolver.DeriveHierarchyFromModelRelations(bound);

        Assert.Equal(identityKey, secondIdentityKey);
        Assert.Equal(identityKey, hierarchy[0].SemanticNodeKey);
        Assert.Equal(identityKey, hierarchy[1].SemanticNodeKey);
        Assert.Equal(1, hierarchy[0].Level);
        Assert.Equal(2, hierarchy[1].Level);
        Assert.Equal("p1", hierarchy[1].ParentSourceId);
    }

    [Fact]
    public void Changing_parent_hint_does_not_change_semantic_identity_key()
    {
        var first = Heading("S0001", "p1", 1, "Section", "parent-node:ROOT", "same-node:section");
        var second = first with { RelationHints = ["parent-node:NONE", "same-node:section"] };

        Assert.Equal(
            CanonicalSemanticIdentityResolver.CreateNodeKey(first),
            CanonicalSemanticIdentityResolver.CreateNodeKey(second));
    }

    private static CanonicalSemanticBoundHeading Heading(
        string alias, string sourceId, int ordinal, string text, params string[] hints) =>
        new(alias, sourceId, ordinal, text, "SECTION", "Heading", "document_body",
            hints, 0, text.Length);
}
