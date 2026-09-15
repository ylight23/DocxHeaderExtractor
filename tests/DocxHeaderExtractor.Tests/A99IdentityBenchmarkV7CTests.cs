using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

public sealed class A99IdentityBenchmarkV7CTests
{
    private static string Root => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
    private static string SummaryPath => Path.Combine(Root, "artifacts/identity-benchmark/v7/conservative-clustering/preflight-v1-clique-equivalence/summary.json");
    private static string ManifestPath => Path.Combine(Root, "artifacts/identity-benchmark/v7/conservative-clustering/preflight-v1-clique-equivalence/manifest.json");

    [Fact]
    public void V7C_freezes_conservative_graph_without_gold_or_provider()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(SummaryPath));
        var root = doc.RootElement;
        Assert.Equal(41, root.GetProperty("acceptedSameEdges").GetInt32());
        Assert.Equal(0, root.GetProperty("acceptedEdgesRejectedByGlobalConsistency").GetInt32());
        Assert.Equal(0, root.GetProperty("keepSplitViolations").GetInt32());
        Assert.Equal(0, root.GetProperty("transitivityViolations").GetInt32());
        Assert.Equal(156, root.GetProperty("singletonClusters").GetInt32());
        Assert.Equal(33, root.GetProperty("multiMemberClusters").GetInt32());
        Assert.Equal(0, root.GetProperty("providerCalls").GetInt32());
        Assert.Equal(0, root.GetProperty("goldReadCount").GetInt32());
    }

    [Fact]
    public void V7C_keeps_invalid_verifier_document_out_of_positive_authority()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(ManifestPath));
        var root = doc.RootElement;
        Assert.False(root.GetProperty("connectedComponentsAuthority").GetBoolean());
        Assert.Contains("DOC-0252", root.GetProperty("input").GetProperty("invalidVerifierDocuments").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal("ABSENCE_OF_POSITIVE_AUTHORITY_MEANS_KEEP_SPLIT", root.GetProperty("defaultPolicy").GetString());
    }
}
