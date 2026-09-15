using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

public sealed class A99IdentityBenchmarkV7ATests
{
    private static string Root => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
    private static string ManifestPath => Path.Combine(Root, "artifacts/identity-benchmark/v7/identity-proposal/preflight-v1-sparse-positive/manifest.json");
    private static string RequestsPath => Path.Combine(Root, "artifacts/identity-benchmark/v7/identity-proposal/preflight-v1-sparse-positive/requests.json");

    [Fact]
    public void V7A_preflight_is_gold_free_and_no_auto_collapse()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(ManifestPath));
        var value = manifest.RootElement;
        Assert.Equal("READY_FOR_SEPARATE_PROVIDER_AUTHORIZATION", value.GetProperty("status").GetString());
        Assert.Equal(0, value.GetProperty("providerCalls").GetInt32());
        Assert.Equal(0, value.GetProperty("goldReadCount").GetInt32());
        Assert.False(value.GetProperty("autoCollapse").GetBoolean());
        Assert.False(value.GetProperty("generalizationClaim").GetBoolean());
    }

    [Fact]
    public void V7A_requests_are_sparse_positive_proposals_only()
    {
        using var requests = JsonDocument.Parse(File.ReadAllText(RequestsPath));
        var value = requests.RootElement;
        Assert.Equal(226, value.GetProperty("occurrenceCount").GetInt32());
        Assert.Equal(3, value.GetProperty("requestCount").GetInt32());
        Assert.False(value.GetProperty("autoCollapse").GetBoolean());
        Assert.False(value.GetProperty("goldDerivedInput").GetBoolean());
        foreach (var record in value.GetProperty("requests").EnumerateArray())
        {
            var request = record.GetProperty("request");
            Assert.False(request.GetProperty("outputContract").GetProperty("autoCollapse").GetBoolean());
            Assert.Equal("EACH_OCCURRENCE_SEPARATE_UNLESS_PROMOTION_LATER_ACCEPTS_PROPOSAL", request.GetProperty("identityDefault").GetString());
        }
    }
}
