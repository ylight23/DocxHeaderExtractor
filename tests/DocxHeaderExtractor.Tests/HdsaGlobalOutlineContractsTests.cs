using System.Text.Json;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Tests;

public sealed class HdsaGlobalOutlineContractsTests
{
    [Fact]
    public void Whole_outline_request_is_deterministic_and_contains_each_node_once()
    {
        var request = Request();
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        var first = JsonSerializer.Serialize(request, options);
        var second = JsonSerializer.Serialize(request, options);

        Assert.Equal(first, second);
        using var document = JsonDocument.Parse(first);
        Assert.Equal(3, document.RootElement.GetProperty("nodes").GetArrayLength());
        Assert.Equal(3, document.RootElement.GetProperty("nodes").EnumerateArray()
            .Select(item => item.GetProperty("semanticNodeId").GetString()).Distinct().Count());
        Assert.False(request.GoldDerivedInput);
    }

    [Fact]
    public void Global_proposal_builds_tree_and_proposed_level_does_not_control_depth()
    {
        var request = Request();
        var proposal = new HdsaGlobalOutlineHierarchyProposal(
        [
            new("A", "ROOT", 99, "high", "root"),
            new("B", "A", 1, "high", "child"),
            new("C", "B", null, "medium", "grandchild"),
        ]);

        var validation = HdsaGlobalOutlineHierarchyContract.Validate(request, proposal);

        Assert.True(validation.Accepted);
        Assert.Equal((1, 2, 3), (
            validation.Tree.Nodes.Single(item => item.Id == "A").Level,
            validation.Tree.Nodes.Single(item => item.Id == "B").Level,
            validation.Tree.Nodes.Single(item => item.Id == "C").Level));
    }

    [Fact]
    public void Fabricated_parent_and_cycle_are_rejected_without_repair()
    {
        var request = Request();
        var fabricated = new HdsaGlobalOutlineHierarchyProposal(
        [
            new("A", "ROOT", 1, "high", "root"),
            new("B", "NOT_IN_CATALOG", 2, "high", "bad"),
            new("C", "B", 3, "high", "child"),
        ]);
        var cycle = new HdsaGlobalOutlineHierarchyProposal(
        [
            new("A", "C", 1, "high", "cycle"),
            new("B", "A", 2, "high", "cycle"),
            new("C", "B", 3, "high", "cycle"),
        ]);

        Assert.False(HdsaGlobalOutlineHierarchyContract.Validate(request, fabricated).Accepted);
        var cycleValidation = HdsaGlobalOutlineHierarchyContract.Validate(request, cycle);
        Assert.False(cycleValidation.Accepted);
        Assert.Contains("PARENT_CYCLE", cycleValidation.GraphValidation.Errors);
    }

    [Fact]
    public void Root_and_unresolved_are_peer_outcomes_and_unknown_node_is_rejected()
    {
        var request = Request();
        var valid = new HdsaGlobalOutlineHierarchyProposal(
        [
            new("A", "ROOT", 1, "high", "root"),
            new("B", "UNRESOLVED", null, "low", "uncertain"),
            new("C", "A", 2, "high", "child"),
        ]);
        var unknown = new HdsaGlobalOutlineHierarchyProposal(
        [
            new("A", "ROOT", 1, "high", "root"),
            new("B", "A", 2, "high", "child"),
            new("X", "A", 2, "high", "unknown"),
        ]);

        Assert.True(HdsaGlobalOutlineHierarchyContract.Validate(request, valid).Accepted);
        Assert.False(HdsaGlobalOutlineHierarchyContract.Validate(request, unknown).Accepted);
    }

    [Fact]
    public void Gold_derived_request_is_rejected_by_contract_firewall()
    {
        var request = Request() with { GoldDerivedInput = true };
        var proposal = new HdsaGlobalOutlineHierarchyProposal(
        [new("A", "ROOT", 1, "high", "root"), new("B", "A", 2, "high", "child"), new("C", "B", 3, "high", "grandchild")]);

        var validation = HdsaGlobalOutlineHierarchyContract.Validate(request, proposal);

        Assert.False(validation.Accepted);
        Assert.Equal("GOLD_DERIVED_INPUT", validation.RejectionReason);
    }

    private static HdsaGlobalOutlineHierarchyRequest Request() => new(
        "catalog",
        [
            new("A", ["S1"], "A text", 1, "order=1"),
            new("B", ["S2"], "B text", 2, "order=2"),
            new("C", ["S3"], "C text", 3, "order=3"),
        ],
        []);
}
