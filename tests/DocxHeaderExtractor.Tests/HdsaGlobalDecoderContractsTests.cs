using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Tests;

public sealed class HdsaGlobalDecoderContractsTests
{
    [Fact]
    public void Global_decoder_breaks_local_top_one_cycle_with_maximum_valid_tree()
    {
        var result = HdsaGlobalParentDecoder.Decode(["A", "B", "C"],
        [
            new("A", null, .10),
            new("B", null, .20),
            new("C", null, .10),
            new("B", "A", .80),
            new("C", "B", .85),
            new("B", "C", .87),
        ]);

        Assert.True(result.IsValid);
        Assert.True(result.Tree.IsValid);
        Assert.Equal((null, "A", "B"), (
            result.SelectedEdges.Single(edge => edge.ChildSemanticNodeId == "A").ParentSemanticNodeId,
            result.SelectedEdges.Single(edge => edge.ChildSemanticNodeId == "B").ParentSemanticNodeId,
            result.SelectedEdges.Single(edge => edge.ChildSemanticNodeId == "C").ParentSemanticNodeId));
        Assert.Equal((1, 2, 3), (
            result.Tree.Nodes.Single(node => node.Id == "A").Level,
            result.Tree.Nodes.Single(node => node.Id == "B").Level,
            result.Tree.Nodes.Single(node => node.Id == "C").Level));
    }

    [Fact]
    public void Root_is_a_competing_edge_not_a_fallback_after_candidate_generation()
    {
        var result = HdsaGlobalParentDecoder.Decode(["A", "B"],
        [
            new("A", null, .90),
            new("B", null, .10),
            new("A", "B", .80),
        ]);

        Assert.True(result.IsValid);
        Assert.Null(result.SelectedEdges.Single(edge => edge.ChildSemanticNodeId == "A").ParentSemanticNodeId);
        Assert.Null(result.SelectedEdges.Single(edge => edge.ChildSemanticNodeId == "B").ParentSemanticNodeId);
    }

    [Fact]
    public void Decoder_is_deterministic_under_candidate_input_permutation_and_ties()
    {
        var candidates = new[]
        {
            new HdsaParentEdgeScore("A", null, .50, 1, "root-a", "r1"),
            new HdsaParentEdgeScore("B", null, .50, 1, "root-b", "r1"),
            new HdsaParentEdgeScore("B", "A", .50, 1, "a-b", "r1"),
        };

        var first = HdsaGlobalParentDecoder.Decode(["A", "B"], candidates);
        var second = HdsaGlobalParentDecoder.Decode(["B", "A"], candidates.Reverse());

        Assert.True(first.IsValid);
        Assert.True(second.IsValid);
        Assert.Equal(
            first.SelectedEdges.Select(edge => (edge.ChildSemanticNodeId, edge.ParentSemanticNodeId)),
            second.SelectedEdges.Select(edge => (edge.ChildSemanticNodeId, edge.ParentSemanticNodeId)));
    }

    [Fact]
    public void Missing_root_or_parent_candidate_is_not_fabricated()
    {
        var result = HdsaGlobalParentDecoder.Decode(["A", "B"],
        [new("A", null, .9), new("B", "A", .8)]);

        Assert.True(result.IsValid);
        Assert.DoesNotContain(result.SelectedEdges, edge => edge.ChildSemanticNodeId == "B" && edge.ParentSemanticNodeId is null);

        var blocked = HdsaGlobalParentDecoder.Decode(["A", "B"], [new("A", null, .9)]);
        Assert.False(blocked.IsValid);
        Assert.Contains("MISSING_INCOMING_EDGE:B", blocked.Errors);
    }

    [Fact]
    public void Invalid_or_gold_derived_candidates_fail_closed()
    {
        var result = HdsaGlobalParentDecoder.Decode(["A"],
        [
            new("A", "UNKNOWN", .9),
            new("A", "A", .8),
            new("A", null, double.NaN),
            new("A", null, .7, GoldDerivedInput: true),
            new("A", null, .6),
        ]);

        Assert.True(result.IsValid);
        Assert.Single(result.SelectedEdges);
        Assert.Equal(.6, result.TotalScore);
        Assert.Equal(4, result.RejectedCandidates.Count);
    }
}
