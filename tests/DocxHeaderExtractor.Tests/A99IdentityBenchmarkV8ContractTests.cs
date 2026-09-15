using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

public sealed class A99IdentityBenchmarkV8ContractTests
{
    private static string Root => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
    private static string ManifestPath => Path.Combine(Root, "artifacts/identity-benchmark/v8/proof-carrying-identity/preflight-v1/manifest.json");
    private static string ContractPath => Path.Combine(Root, "artifacts/identity-benchmark/v8/proof-carrying-identity/preflight-v1/contract.json");
    private static string ChecksPath => Path.Combine(Root, "artifacts/identity-benchmark/v8/proof-carrying-identity/preflight-v1/synthetic-gate-checks.json");

    [Fact]
    public void V8_contract_is_frozen_without_holdout_gold_or_provider()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(ManifestPath));
        var root = doc.RootElement;
        Assert.Equal("V8_CONTRACT_FROZEN_HOLDOUT_NOT_SELECTED", root.GetProperty("status").GetString());
        Assert.Equal(0, root.GetProperty("providerCalls").GetInt32());
        Assert.Equal(0, root.GetProperty("goldReadCount").GetInt32());
        Assert.False(root.GetProperty("holdoutSelected").GetBoolean());
        Assert.False(root.GetProperty("generalizationClaim").GetBoolean());
    }

    [Fact]
    public void V8_gate_keeps_uncertain_or_unfalsified_edges_split()
    {
        using var contract = JsonDocument.Parse(File.ReadAllText(ContractPath));
        using var checks = JsonDocument.Parse(File.ReadAllText(ChecksPath));
        Assert.True(contract.RootElement.GetProperty("deterministicPromotionGate").GetProperty("absenceOfContradictionIsNotProof").GetBoolean());
        Assert.False(contract.RootElement.GetProperty("deterministicPromotionGate").GetProperty("autoCollapse").GetBoolean());
        Assert.Equal("PASS", checks.RootElement.GetProperty("status").GetString());
        Assert.Equal(1, checks.RootElement.GetProperty("cases").EnumerateArray().Count(x => x.GetProperty("expected").GetString() == "ACCEPTED_FOR_V8_GRAPH_ONLY"));
    }
}
