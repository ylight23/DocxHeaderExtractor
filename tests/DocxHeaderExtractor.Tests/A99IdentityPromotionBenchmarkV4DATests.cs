using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

public sealed class A99IdentityPromotionBenchmarkV4DATests
{
    private static JsonDocument Load(string path) => JsonDocument.Parse(File.ReadAllText(Path.Combine(TestRepository.Root(), path.Replace('/', Path.DirectorySeparatorChar))));

    [Fact]
    public void V4D_A_fails_closed_when_new_V4_reason_labels_have_no_frozen_V3_mapping()
    {
        using var manifest = Load("artifacts/identity-benchmark/v4/pruning/manifest.json");
        Assert.Equal("BLOCKED_ON_V3_PRUNER_CONTRACT_INCOMPATIBILITY", manifest.RootElement.GetProperty("status").GetString());
        Assert.Equal(3, manifest.RootElement.GetProperty("newReasonLabels").GetArrayLength());
        Assert.False(manifest.RootElement.GetProperty("newReasonLabelsHaveFrozenV3Interpretation").GetBoolean());
    }

    [Fact]
    public void V4D_A_does_not_open_Gold_or_create_provider_requests()
    {
        using var firewall = Load("artifacts/identity-benchmark/v4/pruning/firewall.json");
        var root = firewall.RootElement;
        Assert.Equal(0, root.GetProperty("goldReadCount").GetInt32());
        Assert.Equal(0, root.GetProperty("providerCalls").GetInt32());
        Assert.False(root.GetProperty("v4cEvaluationRead").GetBoolean());
        Assert.False(root.GetProperty("requestArtifactsCreated").GetBoolean());
    }

    [Fact]
    public void V4D_A_does_not_invent_shortlist_or_ranking_dispositions()
    {
        using var decisions = Load("artifacts/identity-benchmark/v4/pruning/pruning-decisions.json");
        using var shortlist = Load("artifacts/identity-benchmark/v4/pruning/shortlist.json");
        Assert.True(decisions.RootElement.GetProperty("blockedBeforeDisposition").GetBoolean());
        Assert.Equal(0, decisions.RootElement.GetProperty("decisions").GetArrayLength());
        Assert.Equal(0, shortlist.RootElement.GetProperty("candidates").GetArrayLength());
    }
}
