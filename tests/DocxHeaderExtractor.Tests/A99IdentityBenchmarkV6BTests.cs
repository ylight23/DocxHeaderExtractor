using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

public sealed class A99IdentityBenchmarkV6BTests
{

    [Fact]
    public void V6B_freezes_source_evidence_without_semantic_owner_decisions()
    {
        var path = Path.Combine(TestRepository.Root(), "artifacts", "identity-benchmark", "v6", "owner-evidence", "source-only-freeze-v1", "manifest.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var json = doc.RootElement;
        Assert.Equal("SOURCE_ONLY_OWNER_EVIDENCE_FROZEN", json.GetProperty("status").GetString());
        Assert.Equal(0, json.GetProperty("modelCalls").GetInt32());
        Assert.Equal(0, json.GetProperty("providerCalls").GetInt32());
        Assert.Equal(0, json.GetProperty("goldReadCount").GetInt32());
        Assert.Equal(0, json.GetProperty("semanticOwnerAssignments").GetInt32());
    }
}
