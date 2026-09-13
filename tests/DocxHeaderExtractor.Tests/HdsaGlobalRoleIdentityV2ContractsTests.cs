using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Tests;

public sealed class HdsaGlobalRoleIdentityV2ContractsTests
{
    [Fact]
    public void Role_contract_requires_each_known_node_exactly_once()
    {
        var proposal = HdsaGlobalRoleClassificationContract.Parse("""
            {
              "expectedNodeCount": 2,
              "classifiedNodeCount": 2,
              "roles": {
                "S0001": "DOCUMENT_FRAMING",
                "S0005": "OUTLINE_ROOT"
              }
            }
            """);

        var result = HdsaGlobalRoleClassificationContract.Validate(
            new HashSet<string>(["S0001", "S0005"], StringComparer.Ordinal), proposal);

        Assert.True(result.Accepted);
        Assert.Equal(2, result.Nodes.Count);
    }

    [Fact]
    public void Role_contract_rejects_incomplete_keyset_even_when_counts_claim_complete()
    {
        var proposal = HdsaGlobalRoleClassificationContract.Parse("""
            {
              "expectedNodeCount": 2,
              "classifiedNodeCount": 2,
              "roles": {
                "S0001": "DOCUMENT_FRAMING"
              }
            }
            """);

        var result = HdsaGlobalRoleClassificationContract.Validate(
            new HashSet<string>(["S0001", "S0005"], StringComparer.Ordinal), proposal);

        Assert.False(result.Accepted);
        Assert.Equal("CLASSIFIED_NODE_COUNT_MISMATCH", result.RejectionReason);
    }

    [Fact]
    public void Role_contract_rejects_duplicate_key_in_raw_json()
    {
        var exception = Assert.Throws<FormatException>(() =>
            HdsaGlobalRoleClassificationContract.Parse("""
                {
                  "expectedNodeCount": 1,
                  "classifiedNodeCount": 1,
                  "roles": {
                    "S0001": "DOCUMENT_FRAMING",
                    "S0001": "OUTLINE_ROOT"
                  }
                }
                """));

        Assert.Equal("HDSA_GLOBAL_ROLE_DUPLICATE_NODE_ID", exception.Message);
    }

    [Fact]
    public void Identity_contract_accepts_directional_continuation_and_builds_component()
    {
        var request = Request("A", "B", "C");
        var response = new HdsaGlobalIdentityRelationResponse([
            Relation("P01-02", "A", "B", "CONTINUATION_OF", "RIGHT_TO_LEFT"),
            Relation("P01-03", "A", "C", "DISTINCT", "NONE"),
            Relation("P02-03", "B", "C", "DISTINCT", "NONE"),
        ]);

        var result = HdsaGlobalIdentityRelationContract.Validate(request, response);

        Assert.True(result.Accepted);
        Assert.Contains(result.AcceptedComponents, group => group.SequenceEqual(["A", "B"]));
        Assert.Contains(result.AcceptedComponents, group => group.SequenceEqual(["C"]));
    }

    [Fact]
    public void Identity_contract_rejects_positive_relation_contradicted_by_distinct()
    {
        var request = Request("A", "B", "C");
        var response = new HdsaGlobalIdentityRelationResponse([
            Relation("P01-02", "A", "B", "SAME_SEMANTIC_REPEAT", "NONE"),
            Relation("P01-03", "A", "C", "DISTINCT", "NONE"),
            Relation("P02-03", "B", "C", "CONTINUATION_OF", "RIGHT_TO_LEFT"),
        ]);

        var result = HdsaGlobalIdentityRelationContract.Validate(request, response);

        Assert.False(result.Accepted);
        Assert.Equal("POSITIVE_DISTINCT_CONTRADICTION", result.RejectionReason);
    }

    [Fact]
    public void Identity_contract_rejects_unknown_pair_without_fabricating_identity()
    {
        var request = Request("A", "B", "C");
        var response = new HdsaGlobalIdentityRelationResponse([
            Relation("P01-02", "A", "B", "DISTINCT", "NONE"),
            Relation("P01-03", "A", "C", "DISTINCT", "NONE"),
            Relation("P99-99", "B", "C", "DISTINCT", "NONE"),
        ]);

        var result = HdsaGlobalIdentityRelationContract.Validate(request, response);

        Assert.False(result.Accepted);
        Assert.Equal("UNKNOWN_PAIR_ID", result.RejectionReason);
    }

    private static HdsaGlobalIdentityRelationRequest Request(params string[] ids)
    {
        var nodes = ids.Select((id, index) => new HdsaGlobalRoleNodeInput(
            id, [$"O{index + 1:D2}"], $"text-{id}", index + 1)).ToArray();
        var pairs = new List<HdsaGlobalIdentityPairInput>();
        for (var i = 0; i < nodes.Length; i++)
        for (var j = i + 1; j < nodes.Length; j++)
            pairs.Add(new($"P{i + 1:D2}-{j + 1:D2}", nodes[i], nodes[j],
                "OUTLINE_HEADING", "OUTLINE_HEADING", true, true));
        return new("catalog", pairs, false);
    }

    private static HdsaGlobalIdentityRelationProposal Relation(
        string pairId, string left, string right, string relation, string direction) =>
        new(pairId, left, right, relation, direction, "high", "fixture");
}
