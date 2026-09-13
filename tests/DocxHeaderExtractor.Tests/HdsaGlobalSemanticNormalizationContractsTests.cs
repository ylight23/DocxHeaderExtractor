using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Tests;

public sealed class HdsaGlobalSemanticNormalizationContractsTests
{
    [Fact]
    public void Positive_group_is_accepted_and_keeps_every_source_node_once()
    {
        var proposal = new HdsaGlobalSemanticNormalizationProposal([
            Node("A", "DOCUMENT_FRAMING", "G-A", "PRIMARY"),
            Node("B", "OUTLINE_ROOT", "G-B", "PRIMARY"),
            Node("C", "OUTLINE_HEADING", "G-C", "CONTINUATION"),
            Node("D", "OUTLINE_HEADING", "G-C", "CONTINUATION"),
        ]);

        var result = HdsaGlobalSemanticNormalizationContract.Validate(
            new HashSet<string>(["A", "B", "C", "D"], StringComparer.Ordinal), proposal);

        Assert.True(result.Accepted);
        Assert.Equal(3, result.AcceptedGroups.Count);
        Assert.Contains(result.AcceptedGroups, group => group.SequenceEqual(["C", "D"]));
    }

    [Fact]
    public void Model_only_ambiguous_group_fails_closed()
    {
        var proposal = new HdsaGlobalSemanticNormalizationProposal([
            Node("A", "OUTLINE_HEADING", "G-A", "CONTINUATION"),
            Node("B", "OUTLINE_HEADING", "G-A", "UNRESOLVED"),
        ]);

        var result = HdsaGlobalSemanticNormalizationContract.Validate(
            new HashSet<string>(["A", "B"], StringComparer.Ordinal), proposal);

        Assert.False(result.Accepted);
        Assert.Equal("AMBIGUOUS_IDENTITY_GROUP", result.RejectionReason);
    }

    [Fact]
    public void Identity_contract_does_not_accept_fabricated_nodes()
    {
        var proposal = new HdsaGlobalSemanticNormalizationProposal([
            Node("A", "OUTLINE_ROOT", "G-A", "PRIMARY"),
            Node("X", "OUTLINE_HEADING", "G-X", "PRIMARY"),
        ]);

        var result = HdsaGlobalSemanticNormalizationContract.Validate(
            new HashSet<string>(["A", "B"], StringComparer.Ordinal), proposal);

        Assert.False(result.Accepted);
        Assert.Equal("UNKNOWN_NODE_ID", result.RejectionReason);
    }

    private static HdsaGlobalSemanticNormalizationNodeProposal Node(
        string id, string role, string group, string relation) =>
        new(id, role, HdsaGlobalSemanticNormalizationRoles.OutlineBearing.Contains(role),
            group, relation, "high", "fixture");
}
