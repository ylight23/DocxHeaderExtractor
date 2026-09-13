using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Tests;

public sealed class HdsaConstructContractsTests
{
    [Fact]
    public void Parent_relations_build_a_depth_derived_tree()
    {
        var ids = new[] { "A", "B", "C", "D" };
        var normalized = HdsaRelationNormalizer.Normalize(ids,
        [
            new("A", "B", HdsaRelationType.ParentOf),
            new("B", "C", HdsaRelationType.ParentOf),
            new("A", "D", HdsaRelationType.ParentOf),
            new("B", "C", HdsaRelationType.ParentOf),
            new("C", "D", HdsaRelationType.SiblingOf),
        ]);

        var graph = HdsaGraphValidator.Validate(ids, normalized);
        var tree = HdsaTreeConstructor.Build(ids, graph);

        Assert.True(tree.IsValid);
        Assert.Equal((1, 2, 3, 2), (
            tree.Nodes.Single(node => node.Id == "A").Level,
            tree.Nodes.Single(node => node.Id == "B").Level,
            tree.Nodes.Single(node => node.Id == "C").Level,
            tree.Nodes.Single(node => node.Id == "D").Level));
        Assert.Equal("A", tree.Nodes.Single(node => node.Id == "B").ParentId);
        Assert.Equal("B", tree.Nodes.Single(node => node.Id == "C").ParentId);
        Assert.Equal("A", tree.Nodes.Single(node => node.Id == "D").ParentId);
        Assert.Single(normalized.Relations, relation => relation.Relation == HdsaRelationType.SiblingOf);
    }

    [Fact]
    public void Symmetric_relations_are_canonicalized_and_deduplicated()
    {
        var result = HdsaRelationNormalizer.Normalize(["A", "B"],
        [
            new("B", "A", HdsaRelationType.SiblingOf),
            new("A", "B", HdsaRelationType.SiblingOf),
        ]);

        var relation = Assert.Single(result.Relations);
        Assert.Equal(("A", "B"), (relation.From, relation.To));
        Assert.Equal(HdsaRelationType.SiblingOf, relation.Relation);
    }

    [Fact]
    public void Parent_cycle_is_rejected_fail_closed()
    {
        var ids = new[] { "A", "B", "C" };
        var normalized = HdsaRelationNormalizer.Normalize(ids,
        [
            new("A", "B", HdsaRelationType.ParentOf),
            new("B", "C", HdsaRelationType.ParentOf),
            new("C", "A", HdsaRelationType.ParentOf),
        ]);

        var graph = HdsaGraphValidator.Validate(ids, normalized);
        var tree = HdsaTreeConstructor.Build(ids, graph);

        Assert.False(graph.IsValid);
        Assert.Contains("PARENT_CYCLE", graph.Errors);
        Assert.Empty(graph.AcceptedRelations.Where(relation => relation.Relation == HdsaRelationType.ParentOf));
        Assert.False(tree.IsValid);
        Assert.Empty(tree.Nodes);
    }

    [Fact]
    public void Multiple_parent_claims_are_rejected_without_a_tie_breaker()
    {
        var ids = new[] { "A", "B", "C" };
        var normalized = HdsaRelationNormalizer.Normalize(ids,
        [
            new("A", "C", HdsaRelationType.ParentOf),
            new("B", "C", HdsaRelationType.ParentOf),
        ]);

        var graph = HdsaGraphValidator.Validate(ids, normalized);
        var tree = HdsaTreeConstructor.Build(ids, graph);

        Assert.False(graph.IsValid);
        Assert.Contains("MULTIPLE_PARENTS", graph.Errors);
        Assert.Equal(2, graph.RejectedRelations.Count(rejection => rejection.Reason == "MULTIPLE_PARENTS"));
        Assert.False(tree.IsValid);
        Assert.Empty(tree.Nodes);
    }

    [Fact]
    public void Non_parent_relations_do_not_change_depth()
    {
        var ids = new[] { "root", "child", "continuation" };
        var normalized = HdsaRelationNormalizer.Normalize(ids,
        [
            new("root", "child", HdsaRelationType.ParentOf),
            new("continuation", "child", HdsaRelationType.ContinuationOf),
            new("child", "continuation", HdsaRelationType.SameSemanticNode),
        ]);

        var graph = HdsaGraphValidator.Validate(ids, normalized);
        var tree = HdsaTreeConstructor.Build(ids, graph);

        Assert.True(tree.IsValid);
        Assert.Equal(1, tree.Nodes.Single(node => node.Id == "root").Level);
        Assert.Equal(2, tree.Nodes.Single(node => node.Id == "child").Level);
        Assert.Equal(1, tree.Nodes.Single(node => node.Id == "continuation").Level);
    }
}
