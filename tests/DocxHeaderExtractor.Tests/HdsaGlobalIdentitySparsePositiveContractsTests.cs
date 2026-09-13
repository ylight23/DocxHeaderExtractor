using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Tests;

public sealed class HdsaGlobalIdentitySparsePositiveContractsTests
{
    [Fact]
    public void Empty_positive_output_is_valid_and_all_pairs_are_no_claim()
    {
        var request = Request(("A", "B"), ("A", "C"), ("B", "C"));
        var response = HdsaGlobalIdentitySparsePositiveContract.Parse(
            """{"positiveRelations":[]}""");

        var result = HdsaGlobalIdentitySparsePositiveContract.Validate(request, response);

        Assert.True(result.Accepted);
        Assert.Equal(3, result.NoClaimPairCount);
        Assert.Equal(3, result.PositiveComponents.Count);
    }

    [Fact]
    public void Positive_relation_creates_component_without_distinct_claims()
    {
        var request = Request(("A", "B"), ("A", "C"), ("B", "C"));
        var response = HdsaGlobalIdentitySparsePositiveContract.Parse("""
            {
              "positiveRelations": [
                {
                  "left": "A",
                  "right": "B",
                  "relation": "CONTINUATION_OF",
                  "direction": "RIGHT_TO_LEFT"
                }
              ]
            }
            """);

        var result = HdsaGlobalIdentitySparsePositiveContract.Validate(request, response);

        Assert.True(result.Accepted);
        Assert.Contains(result.PositiveComponents, group => group.SequenceEqual(["A", "B"]));
        Assert.Equal(2, result.NoClaimPairCount);
    }

    [Fact]
    public void Multiple_continuation_parents_fail_closed()
    {
        var request = Request(("A", "C"), ("B", "C"));
        var response = new HdsaSparsePositiveIdentityResponse([
            new("A", "C", "CONTINUATION_OF", "RIGHT_TO_LEFT"),
            new("B", "C", "CONTINUATION_OF", "RIGHT_TO_LEFT"),
        ]);

        var result = HdsaGlobalIdentitySparsePositiveContract.Validate(request, response);

        Assert.False(result.Accepted);
        Assert.Equal("MULTIPLE_CONTINUATION_PARENTS", result.RejectionReason);
    }

    [Fact]
    public void Continuation_cycle_fails_closed()
    {
        var request = Request(("A", "B"), ("B", "C"), ("C", "A"));
        var response = new HdsaSparsePositiveIdentityResponse([
            new("A", "B", "CONTINUATION_OF", "RIGHT_TO_LEFT"),
            new("B", "C", "CONTINUATION_OF", "RIGHT_TO_LEFT"),
            new("C", "A", "CONTINUATION_OF", "RIGHT_TO_LEFT"),
        ]);

        var result = HdsaGlobalIdentitySparsePositiveContract.Validate(request, response);

        Assert.False(result.Accepted);
        Assert.Equal("CONTINUATION_CYCLE", result.RejectionReason);
    }

    [Fact]
    public void Unknown_or_distinct_output_cannot_enter_positive_path()
    {
        var request = Request(("A", "B"));
        var response = new HdsaSparsePositiveIdentityResponse([
            new("A", "B", "DISTINCT", "RIGHT_TO_LEFT"),
        ]);

        var result = HdsaGlobalIdentitySparsePositiveContract.Validate(request, response);

        Assert.False(result.Accepted);
        Assert.Equal("POSITIVE_RELATION_INVALID", result.RejectionReason);
    }

    private static HdsaGlobalIdentityRelationRequest Request(
        params (string Left, string Right)[] pairIds)
    {
        var nodes = pairIds.SelectMany(pair => new[] { pair.Left, pair.Right })
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select((id, index) => new HdsaGlobalRoleNodeInput(
                id, [$"O{index + 1:D2}"], $"text-{id}", index + 1))
            .ToDictionary(item => item.NodeId, StringComparer.Ordinal);
        var pairs = pairIds.Select((pair, index) => new HdsaGlobalIdentityPairInput(
            $"P{index + 1:D2}", nodes[pair.Left], nodes[pair.Right],
            "OUTLINE_HEADING", "OUTLINE_HEADING", true, true)).ToArray();
        return new("catalog", pairs, false);
    }
}
