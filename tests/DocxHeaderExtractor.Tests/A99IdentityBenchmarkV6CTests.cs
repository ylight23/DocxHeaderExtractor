using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

public sealed class A99IdentityBenchmarkV6CTests
{
    private static string Root() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));

    [Fact]
    public void V6C_preflight_is_document_global_and_source_only()
    {
        var path = Path.Combine(Root(), "artifacts", "identity-benchmark", "v6", "owner-induction", "preflight-v1", "manifest.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var json = doc.RootElement;
        Assert.Equal("READY_FOR_PROVIDER_EXECUTION", json.GetProperty("status").GetString());
        Assert.Equal(3, json.GetProperty("requestCount").GetInt32());
        Assert.Equal(226, json.GetProperty("occurrenceCount").GetInt32());
        Assert.Equal(22, json.GetProperty("parserScopeGroupCount").GetInt32());
        Assert.Equal(0, json.GetProperty("goldReadCount").GetInt32());
        Assert.Equal(0, json.GetProperty("v5cReadCount").GetInt32());
        Assert.Equal(0, json.GetProperty("v6aReadCount").GetInt32());
        Assert.Equal(0, json.GetProperty("semanticOwnerAssignments").GetInt32());
    }

    [Fact]
    public void V6C_v2_addressable_preflight_is_a_new_no_call_boundary()
    {
        var path = Path.Combine(Root(), "artifacts", "identity-benchmark", "v6", "owner-induction", "preflight-v2-addressable", "manifest.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var json = doc.RootElement;
        Assert.Equal("READY_FOR_SEPARATE_PROVIDER_AUTHORIZATION", json.GetProperty("status").GetString());
        Assert.Equal("preflight-v1", json.GetProperty("supersedes").GetString());
        Assert.Equal(3, json.GetProperty("requestCount").GetInt32());
        Assert.Equal(226, json.GetProperty("occurrenceCount").GetInt32());
        Assert.False(json.GetProperty("v1PredictionMutation").GetBoolean());
        Assert.Equal(0, json.GetProperty("providerCalls").GetInt32());
        Assert.Equal(0, json.GetProperty("goldReadCount").GetInt32());
    }
}
