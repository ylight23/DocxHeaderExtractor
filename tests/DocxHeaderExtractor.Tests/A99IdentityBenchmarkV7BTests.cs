using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

public sealed class A99IdentityBenchmarkV7BTests
{
    private static string Root => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
    private static string ManifestPath => Path.Combine(Root, "artifacts/identity-benchmark/v7/identity-verification/preflight-v1-independent-positive-edge/manifest.json");
    private static string RequestsPath => Path.Combine(Root, "artifacts/identity-benchmark/v7/identity-verification/preflight-v1-independent-positive-edge/requests.json");

    [Fact]
    public void V7B_preflight_is_three_document_global_requests_and_gold_free()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(ManifestPath));
        var root = doc.RootElement;
        Assert.Equal("READY_FOR_SEPARATE_PROVIDER_AUTHORIZATION", root.GetProperty("status").GetString());
        Assert.Equal(3, root.GetProperty("requestCount").GetInt32());
        Assert.Equal(58, root.GetProperty("frozenV7AProposalCount").GetInt32());
        Assert.Equal(0, root.GetProperty("providerCalls").GetInt32());
        Assert.Equal(0, root.GetProperty("goldReadCount").GetInt32());
        Assert.False(root.GetProperty("autoCollapse").GetBoolean());
        Assert.False(root.GetProperty("connectedComponentsAuthority").GetBoolean());
    }

    [Fact]
    public void V7B_requests_exclude_v7a_rationale_and_cover_each_edge_once()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(RequestsPath));
        var root = doc.RootElement;
        Assert.Equal(3, root.GetProperty("requestCount").GetInt32());
        Assert.Equal(58, root.GetProperty("frozenV7AProposalCount").GetInt32());
        var proposalIds = root.GetProperty("requests").EnumerateArray()
            .SelectMany(x => x.GetProperty("request").GetProperty("proposedEdges").EnumerateArray()
                .Select(edge => $"{x.GetProperty("request").GetProperty("documentId").GetString()}:{edge.GetProperty("proposalId").GetString()}"))
            .ToArray();
        Assert.Equal(58, proposalIds.Length);
        Assert.Equal(58, proposalIds.Distinct(StringComparer.Ordinal).Count());
        foreach (var record in root.GetProperty("requests").EnumerateArray())
        {
            var request = record.GetProperty("request");
            Assert.False(request.GetProperty("independence").GetProperty("v7aRationaleIncluded").GetBoolean());
            Assert.False(request.GetProperty("independence").GetProperty("v7aDecisionExplanationIncluded").GetBoolean());
            Assert.False(request.GetProperty("outputContract").GetProperty("autoCollapse").GetBoolean());
            Assert.False(request.GetProperty("outputContract").GetProperty("connectedComponentsAuthority").GetBoolean());
        }
    }
}
