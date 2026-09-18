using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using Xunit;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Level is owned by the harness but derived only from the immediate-parent relations the model
/// returned. Numbering shape must never re-enter as a level authority here.
/// </summary>
public class ModelRelationHierarchyResolverTests
{
    private static CanonicalSemanticBoundHeading Heading(
        string alias, string sourceId, int ordinal, string text, params string[] relationHints) =>
        new(alias, sourceId, ordinal, text, "SECTION", "Heading", "document_body",
            relationHints, 0, text.Length);

    [Fact]
    public void Root_children_are_level_one_and_parent_chain_increments()
    {
        var derived = ModelRelationHierarchyResolver.DeriveHierarchyFromModelRelations(
        [
            Heading("S0001", "p1", 1, "Part One", "parent-node:ROOT"),
            Heading("S0002", "p2", 2, "Chapter A", "parent-node:S0001"),
            Heading("S0003", "p3", 3, "Section A.1", "parent-node:S0002"),
        ]).ToDictionary(item => item.SourceId, StringComparer.Ordinal);

        Assert.Equal(1, derived["p1"].Level);
        Assert.Equal(2, derived["p2"].Level);
        Assert.Equal(3, derived["p3"].Level);
        Assert.Null(derived["p1"].ParentSourceId);
        Assert.Equal("p1", derived["p2"].ParentSourceId);
        Assert.Equal("p2", derived["p3"].ParentSourceId);
        Assert.Equal("model-root", derived["p1"].Resolution);
        Assert.Equal("model-parent-relation", derived["p3"].Resolution);
    }

    [Fact]
    public void Siblings_under_one_parent_share_a_level()
    {
        var derived = ModelRelationHierarchyResolver.DeriveHierarchyFromModelRelations(
        [
            Heading("S0001", "p1", 1, "Data Sheet", "parent-node:ROOT"),
            Heading("S0002", "p2", 2, "Only One Proposal", "parent-node:S0001"),
            Heading("S0003", "p3", 3, "Proposal Validity", "parent-node:S0001"),
        ]).ToDictionary(item => item.SourceId, StringComparer.Ordinal);

        Assert.Equal(2, derived["p2"].Level);
        Assert.Equal(2, derived["p3"].Level);
    }

    [Fact]
    public void Missing_parent_hint_stays_unresolved_instead_of_guessing_depth()
    {
        var derived = ModelRelationHierarchyResolver.DeriveHierarchyFromModelRelations(
        [
            Heading("S0001", "p1", 1, "1.2.3 Deeply Numbered Heading"),
        ]).Single();

        Assert.Equal("unresolved", derived.Resolution);
        Assert.Null(derived.ParentSourceId);
        // The numbering path "1.2.3" must not become level 3.
        Assert.Equal(1, derived.Level);
    }

    [Fact]
    public void Self_parent_is_rejected()
    {
        var derived = ModelRelationHierarchyResolver.DeriveHierarchyFromModelRelations(
        [
            Heading("S0001", "p1", 1, "Self", "parent-node:S0001"),
        ]).Single();

        Assert.Equal("unresolved", derived.Resolution);
        Assert.Null(derived.ParentSourceId);
    }

    [Fact]
    public void Parent_that_does_not_precede_the_child_is_rejected()
    {
        var derived = ModelRelationHierarchyResolver.DeriveHierarchyFromModelRelations(
        [
            Heading("S0001", "p1", 1, "Child", "parent-node:S0002"),
            Heading("S0002", "p2", 2, "Later", "parent-node:ROOT"),
        ]).ToDictionary(item => item.SourceId, StringComparer.Ordinal);

        Assert.Equal("unresolved", derived["p1"].Resolution);
        Assert.Null(derived["p1"].ParentSourceId);
    }

    [Fact]
    public void Unknown_alias_is_rejected()
    {
        var derived = ModelRelationHierarchyResolver.DeriveHierarchyFromModelRelations(
        [
            Heading("S0001", "p1", 1, "Root", "parent-node:ROOT"),
            Heading("S0002", "p2", 2, "Child", "parent-node:S9999"),
        ]).ToDictionary(item => item.SourceId, StringComparer.Ordinal);

        Assert.Equal("unresolved", derived["p2"].Resolution);
        Assert.Null(derived["p2"].ParentSourceId);
    }

    [Fact]
    public void Level_is_clamped_and_cannot_cycle_because_parents_must_precede()
    {
        var headings = Enumerable.Range(1, 12)
            .Select(index => Heading($"S{index:0000}", $"p{index}", index, $"H{index}",
                index == 1 ? "parent-node:ROOT" : $"parent-node:S{index - 1:0000}"))
            .ToArray();

        var derived = ModelRelationHierarchyResolver.DeriveHierarchyFromModelRelations(headings);

        Assert.All(derived, item => Assert.InRange(item.Level, 1, 9));
        Assert.Equal(9, derived[^1].Level);
    }

    [Fact]
    public void Repeated_occurrences_collapse_only_when_the_model_declares_one_node()
    {
        var derived = ModelRelationHierarchyResolver.DeriveHierarchyFromModelRelations(
        [
            Heading("S0001", "p67", 1, "PART I", "parent-node:ROOT", "same-node:part-1"),
            Heading("S0002", "p142", 2, "PART I", "parent-node:ROOT", "same-node:part-1"),
        ]).ToArray();

        Assert.True(derived[0].IsPrimaryOccurrence);
        Assert.False(derived[1].IsPrimaryOccurrence);
        Assert.Equal(derived[0].SemanticNodeKey, derived[1].SemanticNodeKey);
    }

    [Fact]
    public void Identical_wording_without_a_same_node_hint_stays_two_sections()
    {
        // Two different forms can both be titled "CURRICULUM VITAE"; text is not identity.
        var derived = ModelRelationHierarchyResolver.DeriveHierarchyFromModelRelations(
        [
            Heading("S0001", "p429", 1, "CURRICULUM VITAE (CV)", "parent-node:ROOT"),
            Heading("S0002", "p1037", 2, "CURRICULUM VITAE (CV)", "parent-node:ROOT"),
        ]).ToArray();

        Assert.True(derived[0].IsPrimaryOccurrence);
        Assert.True(derived[1].IsPrimaryOccurrence);
        Assert.NotEqual(derived[0].SemanticNodeKey, derived[1].SemanticNodeKey);
    }
}
