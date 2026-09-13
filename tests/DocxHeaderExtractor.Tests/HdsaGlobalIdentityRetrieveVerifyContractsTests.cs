using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Tests;

public sealed class HdsaGlobalIdentityRetrieveVerifyContractsTests
{
    [Fact]
    public void Empty_discovery_is_valid_and_means_no_claim()
    {
        var request = DiscoveryRequest(3);
        var response = HdsaGlobalIdentityRetrieveVerifyContract.ParseDiscovery(
            "{\"candidatePairIds\":[]}");

        var result = HdsaGlobalIdentityRetrieveVerifyContract.ValidateDiscovery(request, response);

        Assert.True(result.Accepted);
        Assert.Empty(result.CandidatePairIds);
        Assert.Equal(2, result.MaxCandidatePairCount);
    }

    [Fact]
    public void Discovery_rejects_candidate_explosion()
    {
        var request = DiscoveryRequest(3);
        var response = new HdsaIdentityCandidateDiscoveryResponse(["P01", "P02", "P03"]);

        var result = HdsaGlobalIdentityRetrieveVerifyContract.ValidateDiscovery(request, response);

        Assert.False(result.Accepted);
        Assert.Equal("CANDIDATE_DISCOVERY_NOT_SPARSE", result.RejectionReason);
    }

    [Fact]
    public void Discovery_rejects_unknown_and_duplicate_ids()
    {
        var request = DiscoveryRequest(3);

        var unknown = HdsaGlobalIdentityRetrieveVerifyContract.ValidateDiscovery(
            request, new HdsaIdentityCandidateDiscoveryResponse(["P99"]));
        Assert.False(unknown.Accepted);
        Assert.Equal("UNKNOWN_PAIR_ID", unknown.RejectionReason);

        var duplicate = HdsaGlobalIdentityRetrieveVerifyContract.ValidateDiscovery(
            request, new HdsaIdentityCandidateDiscoveryResponse(["P01", "P01"]));
        Assert.False(duplicate.Accepted);
        Assert.Equal("DUPLICATE_PAIR_ID", duplicate.RejectionReason);
    }

    [Fact]
    public void Verification_accepts_unresolved_and_directional_continuation()
    {
        var request = VerificationRequest();
        var unresolved = HdsaGlobalIdentityRetrieveVerifyContract.ParseVerification(
            "{\"pairId\":\"P01\",\"left\":\"A\",\"right\":\"B\",\"relation\":\"UNRESOLVED\",\"direction\":\"NONE\"}");
        var continuation = HdsaGlobalIdentityRetrieveVerifyContract.ParseVerification(
            "{\"pairId\":\"P01\",\"left\":\"A\",\"right\":\"B\",\"relation\":\"CONTINUATION_OF\",\"direction\":\"RIGHT_TO_LEFT\"}");

        Assert.True(HdsaGlobalIdentityRetrieveVerifyContract.ValidateVerification(request, unresolved).Accepted);
        Assert.True(HdsaGlobalIdentityRetrieveVerifyContract.ValidateVerification(request, continuation).Accepted);
    }

    [Fact]
    public void Verification_rejects_wrong_target_and_direction()
    {
        var request = VerificationRequest();
        var wrongTarget = new HdsaIdentityPairVerificationResponse("P02", "A", "B", "DISTINCT", "NONE");
        var wrongDirection = new HdsaIdentityPairVerificationResponse("P01", "A", "B", "CONTINUATION_OF", "NONE");

        Assert.Equal("TARGET_PAIR_MISMATCH",
            HdsaGlobalIdentityRetrieveVerifyContract.ValidateVerification(request, wrongTarget).RejectionReason);
        Assert.Equal("CONTINUATION_DIRECTION_INVALID",
            HdsaGlobalIdentityRetrieveVerifyContract.ValidateVerification(request, wrongDirection).RejectionReason);
    }

    private static HdsaIdentityCandidateDiscoveryRequest DiscoveryRequest(int pairCount)
    {
        var nodes = new[] { Node("A", 1), Node("B", 2) };
        var pairs = Enumerable.Range(0, pairCount)
            .Select(index => new HdsaIdentityCandidatePair($"P{index + 1:D2}", nodes[index % nodes.Length].NodeId, nodes[(index + 1) % nodes.Length].NodeId))
            .ToArray();
        return new("catalog", nodes, pairs, false);
    }

    private static HdsaIdentityPairVerificationRequest VerificationRequest() =>
        new("catalog", [Node("A", 1), Node("B", 2)], new("P01", "A", "B"), false);

    private static HdsaIdentityRoleNodeInput Node(string id, int order) =>
        new(id, [$"O{order:D2}"], $"text-{id}", order, "OUTLINE_HEADING", true);
}
