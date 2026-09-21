using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

public sealed class A99IdentityBenchmarkV6ATests
{

    [Fact]
    public void V6A_is_offline_and_keeps_owner_inference_unresolved()
    {
        var root = TestRepository.Root();
        using var summary = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "artifacts", "identity-benchmark", "v6", "diagnosis", "v5-failure-diagnosis-v1", "summary.json")));
        using var cases = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "artifacts", "identity-benchmark", "v6", "diagnosis", "v5-failure-diagnosis-v1", "cases.json")));
        Assert.Equal("V6A_COMPLETE", summary.RootElement.GetProperty("status").GetString());
        Assert.Equal(55, summary.RootElement.GetProperty("totalFalseMerges").GetInt32());
        Assert.Equal(0, summary.RootElement.GetProperty("modelCalls").GetInt32());
        Assert.Equal(0, summary.RootElement.GetProperty("providerCalls").GetInt32());
        Assert.All(cases.RootElement.EnumerateArray(), item =>
        {
            Assert.False(item.GetProperty("sourceOwnerEvidenceAvailable").GetBoolean());
            Assert.Equal("MODEL_SEMANTIC_GROUPING_OVERREACH", item.GetProperty("diagnosticCategory").GetString());
        });
    }
}
