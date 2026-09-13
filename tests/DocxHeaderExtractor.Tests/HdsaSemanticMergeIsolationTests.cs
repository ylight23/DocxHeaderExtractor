using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Tests;

public sealed class HdsaSemanticMergeIsolationTests
{
    [Fact]
    public void Merge_keeps_unrelated_ids_and_source_order_stable()
    {
        var baseline = Components("A", "B", "C", "D", "E");
        var operation = new HdsaSemanticMergeOperation("DE", ["D", "E"]);

        var result = HdsaSemanticMergeIsolation.Apply(baseline, operation);

        Assert.True(result.IsValid);
        Assert.Equal(["A", "B", "C", "DE"], result.Components.Select(item => item.SemanticNodeId));
        Assert.Equal([0, 1, 2], result.Components.Where(item => item.SemanticNodeId is "A" or "B" or "C").Select(item => item.SourceOrder));
        Assert.Equal("A", result.SemanticNodeProjection["A"]);
        Assert.Equal("B", result.SemanticNodeProjection["B"]);
        Assert.Equal("C", result.SemanticNodeProjection["C"]);
        Assert.Equal("DE", result.SemanticNodeProjection["D"]);
        Assert.Equal("DE", result.SemanticNodeProjection["E"]);
        Assert.Equal(["O4", "O5"], result.Components.Single(item => item.SemanticNodeId == "DE").MemberOccurrenceIds);

        var reversed = HdsaSemanticMergeIsolation.Apply(baseline,
            new HdsaSemanticMergeOperation("DE", ["E", "D"]));
        Assert.Equal(["O4", "O5"], reversed.Components.Single(item => item.SemanticNodeId == "DE").MemberOccurrenceIds);
    }

    [Fact]
    public void Candidate_projection_collapses_only_references_to_merged_component()
    {
        var result = HdsaSemanticMergeIsolation.Apply(Components("A", "B", "C", "D", "E"),
            new HdsaSemanticMergeOperation("DE", ["D", "E"]));

        var projected = HdsaSemanticMergeIsolation.ProjectCandidateReferences(["A", "D", "B", "E", "C"], result.SemanticNodeProjection);

        Assert.Equal(["A", "DE", "B", "C"], projected);
        Assert.Equal(["A", "B", "C"], projected.Where(item => item is "A" or "B" or "C"));
    }

    [Fact]
    public void Unrelated_parent_request_bytes_are_unchanged_by_experimental_merge()
    {
        var baseline = Components("A", "B", "C", "D", "E");
        var result = HdsaSemanticMergeIsolation.Apply(baseline,
            new HdsaSemanticMergeOperation("DE", ["D", "E"], "EXPERIMENTAL"));
        var request = new HdsaMergeIsolationParentRequest("C", ["A", "B"], true, "C context");

        var projected = HdsaSemanticMergeIsolation.ProjectRequest(request, result.SemanticNodeProjection);

        Assert.Equal(HdsaSemanticMergeIsolation.SerializeRequest(request), HdsaSemanticMergeIsolation.SerializeRequest(projected));
        Assert.Equal(HdsaSemanticMergeIsolation.RequestHash(request), HdsaSemanticMergeIsolation.RequestHash(projected));
    }

    [Fact]
    public void Merge_rejects_unknown_or_overlapping_components_fail_closed()
    {
        var unknown = HdsaSemanticMergeIsolation.Apply(Components("A", "B"),
            new HdsaSemanticMergeOperation("X", ["A", "Z"]));
        var overlapping = HdsaSemanticMergeIsolation.Apply(
        [
            new("A", ["O1"], "A", 0),
            new("B", ["O1"], "B", 1),
        ], new HdsaSemanticMergeOperation("AB", ["A", "B"]));

        Assert.False(unknown.IsValid);
        Assert.Contains("UNKNOWN_MERGE_COMPONENT:Z", unknown.Errors);
        Assert.False(overlapping.IsValid);
        Assert.Contains("OVERLAPPING_COMPONENT_MEMBERSHIP", overlapping.Errors);
    }

    [Fact]
    public void Stable_merged_id_is_order_independent()
    {
        Assert.Equal(HdsaSemanticMergeIsolation.StableMergedNodeId(["O2", "O1"]),
            HdsaSemanticMergeIsolation.StableMergedNodeId(["O1", "O2"]));
    }

    private static IReadOnlyList<HdsaSemanticCatalogComponent> Components(params string[] ids) =>
        ids.Select((id, index) => new HdsaSemanticCatalogComponent(id, [$"O{index + 1}"], id, index)).ToArray();
}
