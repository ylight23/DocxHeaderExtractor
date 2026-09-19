using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using Xunit;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The placement pass exists because one prompt cannot reliably answer "is this a heading", "what
/// does it mean" and "where does it sit" at once — crowding it measurably costs placement. These
/// tests pin what the escalation is allowed to change: it may place a heading the first pass left
/// unresolved, and nothing else.
/// </summary>
public class HierarchyPlacementEscalationTests
{
    private static CanonicalSemanticBoundHeading Heading(
        string alias, string sourceId, int ordinal, string text, params string[] hints) =>
        new(alias, sourceId, ordinal, text, "SECTION", "Heading", "document_body",
            hints, 0, text.Length);

    private static IReadOnlyList<ModelRelationHierarchyResolver.DerivedHeadingHierarchy> Derive(
        params CanonicalSemanticBoundHeading[] bound) =>
        ModelRelationHierarchyResolver.DeriveHierarchyFromModelRelations(bound);

    [Fact]
    public void An_unresolved_heading_is_what_the_escalation_is_for()
    {
        var derived = Derive(
            Heading("S0001", "p1", 1, "Session I", "parent-node:ROOT"),
            Heading("S0002", "p2", 2, "Agenda item")).ToDictionary(item => item.SourceId, StringComparer.Ordinal);

        Assert.Equal(ModelRelationHierarchyResolver.ResolvedRoot, derived["p1"].Resolution);
        Assert.Equal(ModelRelationHierarchyResolver.Unresolved, derived["p2"].Resolution);
    }

    [Fact]
    public void A_placement_answer_resolves_the_heading_it_names()
    {
        // What the escalation adds is an ordinary parent hint, so the resolver treats a placed
        // heading exactly like one the first pass had placed itself.
        var derived = Derive(
            Heading("S0001", "p1", 1, "Session I", "parent-node:ROOT"),
            Heading("S0002", "p2", 2, "Agenda item", "parent-node:S0001"))
            .ToDictionary(item => item.SourceId, StringComparer.Ordinal);

        Assert.Equal(ModelRelationHierarchyResolver.ResolvedParent, derived["p2"].Resolution);
        Assert.Equal("p1", derived["p2"].ParentSourceId);
        Assert.Equal(2, derived["p2"].Level);
    }

    [Fact]
    public void Placement_can_answer_that_a_heading_sits_outside_the_tree()
    {
        var derived = Derive(
            Heading("S0001", "p1", 1, "MINUTES OF THE MEETING", "parent-node:NONE"),
            Heading("S0002", "p2", 2, "Session I", "parent-node:ROOT"))
            .ToDictionary(item => item.SourceId, StringComparer.Ordinal);

        Assert.Equal(ModelRelationHierarchyResolver.OutOfHierarchy, derived["p1"].Resolution);
        Assert.Equal(1, derived["p2"].Level);
    }

    [Fact]
    public void A_placement_naming_a_later_heading_is_still_rejected()
    {
        // The escalation feeds the same resolver, so its answers face the same validation: a
        // parent must precede its child whoever proposed it.
        var derived = Derive(
            Heading("S0001", "p1", 1, "Child", "parent-node:S0002"),
            Heading("S0002", "p2", 2, "Later"))
            .ToDictionary(item => item.SourceId, StringComparer.Ordinal);

        Assert.Equal(ModelRelationHierarchyResolver.Unresolved, derived["p1"].Resolution);
    }

    [Fact]
    public void A_placement_naming_an_unknown_alias_is_still_rejected()
    {
        var derived = Derive(
            Heading("S0001", "p1", 1, "Root", "parent-node:ROOT"),
            Heading("S0002", "p2", 2, "Child", "parent-node:S9999"))
            .ToDictionary(item => item.SourceId, StringComparer.Ordinal);

        Assert.Equal(ModelRelationHierarchyResolver.Unresolved, derived["p2"].Resolution);
    }

    [Fact]
    public void A_heading_already_placed_keeps_the_relation_the_semantic_pass_gave_it()
    {
        // Two parent hints on one heading: the first is the semantic pass's own decision and must
        // win, otherwise a later pass could quietly overturn settled meaning.
        var derived = Derive(
            Heading("S0001", "p1", 1, "Session I", "parent-node:ROOT"),
            Heading("S0002", "p2", 2, "Section", "parent-node:S0001", "parent-node:NONE"))
            .Single(item => item.SourceId == "p2");

        Assert.Equal(ModelRelationHierarchyResolver.ResolvedParent, derived.Resolution);
        Assert.Equal("p1", derived.ParentSourceId);
    }
}
