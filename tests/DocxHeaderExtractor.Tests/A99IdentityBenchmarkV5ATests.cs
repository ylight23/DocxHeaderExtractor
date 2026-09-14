using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

public sealed class A99IdentityBenchmarkV5ATests
{
    private static string RepositoryRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));

    [Fact]
    public void V5A_freeze_is_source_only_and_does_not_assign_identity()
    {
        var root = RepositoryRoot();
        var path = Path.Combine(root, "artifacts", "identity-benchmark", "v5", "source-only-clusters", "freeze", "manifest.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var value = doc.RootElement;
        Assert.Equal("FROZEN_SOURCE_ONLY_AMBIGUITY_CLUSTERS", value.GetProperty("status").GetString());
        Assert.Equal(128, value.GetProperty("candidateCount").GetInt32());
        Assert.Equal(0, value.GetProperty("modelCalls").GetInt32());
        Assert.Equal(0, value.GetProperty("providerCalls").GetInt32());
        Assert.Equal(0, value.GetProperty("goldReadCount").GetInt32());
        Assert.False(value.GetProperty("knownPairLabelsUsed").GetBoolean());
        Assert.False(value.GetProperty("semanticMergePerformed").GetBoolean());
        Assert.False(value.GetProperty("semanticNodeIdsAssigned").GetBoolean());
    }

    [Fact]
    public void V5A_edges_retain_source_evidence_provenance_only()
    {
        var root = RepositoryRoot();
        var path = Path.Combine(root, "artifacts", "identity-benchmark", "v5", "source-only-clusters", "freeze", "edges.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var edges = doc.RootElement.GetProperty("edges").EnumerateArray().ToArray();
        Assert.Equal(128, edges.Length);
        Assert.All(edges, edge => Assert.NotEmpty(edge.GetProperty("reasons").EnumerateArray()));
        Assert.DoesNotContain(edges, edge => edge.TryGetProperty("relation", out _));
        Assert.DoesNotContain(edges, edge => edge.TryGetProperty("gold", out _));
    }
}
