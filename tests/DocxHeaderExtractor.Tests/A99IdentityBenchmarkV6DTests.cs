using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

public sealed class A99IdentityBenchmarkV6DTests
{
    private static string Root() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));

    [Fact]
    public void V6D_preflight_is_owner_conditioned_and_gold_free()
    {
        var root = Path.Combine(Root(), "artifacts", "identity-benchmark", "v6", "semantic-node-induction", "preflight-v1-owner-conditioned");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "manifest.json")));
        var m = manifest.RootElement;
        Assert.Equal("READY_FOR_SEPARATE_PROVIDER_AUTHORIZATION", m.GetProperty("status").GetString());
        Assert.Equal(3, m.GetProperty("requestCount").GetInt32());
        Assert.Equal(226, m.GetProperty("occurrenceCount").GetInt32());
        Assert.Equal(106, m.GetProperty("ownerCount").GetInt32());
        Assert.True(m.GetProperty("deterministicRebuild").GetBoolean());
        Assert.True(m.GetProperty("ownerAuthorityValid").GetBoolean());
        Assert.Equal("V6C_V3_CORRECTED_OFFLINE_REVALIDATION_V2", m.GetProperty("ownerAuthority").GetString());
        Assert.Equal(0, m.GetProperty("providerCalls").GetInt32());
        Assert.Equal(0, m.GetProperty("goldReadCount").GetInt32());
        Assert.Equal(0, m.GetProperty("v5cReadCount").GetInt32());
        Assert.Equal(0, m.GetProperty("v6aReadCount").GetInt32());
        Assert.Equal(0, m.GetProperty("v5PredictionReadCount").GetInt32());
    }

    [Fact]
    public void V6D_requests_use_owner_evidence_without_requesting_hierarchy()
    {
        var root = Path.Combine(Root(), "artifacts", "identity-benchmark", "v6", "semantic-node-induction", "preflight-v1-owner-conditioned");
        using var requests = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "requests.json")));
        foreach (var record in requests.RootElement.GetProperty("records").EnumerateArray())
        {
            var request = record.GetProperty("request");
            Assert.True(request.GetProperty("goldDerivedInput").GetBoolean() == false);
            Assert.True(request.GetProperty("semanticNodeRequested").GetBoolean());
            Assert.False(request.GetProperty("parentOrHierarchyRequested").GetBoolean());
            Assert.False(request.GetProperty("pairLabelsRequested").GetBoolean());
            Assert.Equal("V6C_V3_CORRECTED_OFFLINE_REVALIDATION_V2", request.GetProperty("ownerAuthority").GetString());
            Assert.Equal(request.GetProperty("occurrences").GetArrayLength(), request.GetProperty("targetOccurrenceRefs").GetArrayLength());
            Assert.Equal(request.GetProperty("occurrences").GetArrayLength(), request.GetProperty("ownerAssignments").GetArrayLength());
            Assert.All(request.GetProperty("targetOccurrenceRefs").EnumerateArray(), x => Assert.Matches("^U[0-9]{3}$", x.GetString()!));
            Assert.All(request.GetProperty("allowedEvidenceRefs").EnumerateArray(), x => Assert.Matches("^E[0-9]{3}$", x.GetString()!));
            Assert.All(request.GetProperty("allowedOwnerRefs").EnumerateArray(), x => Assert.Matches("^O[0-9]{2,3}$", x.GetString()!));
            foreach (var owner in request.GetProperty("owners").EnumerateArray()) Assert.False(owner.TryGetProperty("memberOccurrenceIds", out _));
        }
    }
}
