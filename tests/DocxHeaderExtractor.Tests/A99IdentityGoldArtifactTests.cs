using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

public sealed class A99IdentityGoldArtifactTests
{
    [Fact]
    public void User_reviewed_identity_artifact_preserves_authority_boundary()
    {
        using var gold = Load("artifacts/identity-gold/semantic-identity-gold.user-reviewed.v2.json");
        var root = gold.RootElement;

        Assert.Equal("USER_REVIEWED_IDENTITY_GOLD_FROZEN", root.GetProperty("status").GetString());
        Assert.Equal("USER_REVIEWED_ASSISTANT_PROPOSAL / USER", root.GetProperty("authority").GetString());
        Assert.False(root.GetProperty("independentABGold").GetBoolean());
        Assert.Equal(5, root.GetProperty("bindingSummary").GetProperty("semanticItems").GetInt32());
        Assert.Equal(0, root.GetProperty("bindingSummary").GetProperty("machineEvaluableItems").GetInt32());
        Assert.Equal(5, root.GetProperty("bindingSummary").GetProperty("bindingIncompleteItems").GetInt32());
    }

    [Fact]
    public void Approved_identity_decisions_and_binding_status_are_preserved()
    {
        using var gold = Load("artifacts/identity-gold/semantic-identity-gold.user-reviewed.v2.json");
        var decisions = gold.RootElement.GetProperty("decisions");

        Assert.Equal("DISTINCT_SEMANTIC_NODE", Find(decisions, "IR-018").GetProperty("relation").GetString());
        Assert.Equal("CONTINUATION_OF", Find(decisions, "IR-019").GetProperty("relation").GetString());
        Assert.Equal("SAME_SEMANTIC_REPEAT", Find(decisions, "IR-020").GetProperty("relation").GetString());
        Assert.Equal("SAME_SEMANTIC_REPEAT", Find(decisions, "IR-021").GetProperty("relation").GetString());
        Assert.Equal("DISTINCT_SEMANTIC_NODE", Find(decisions, "IR-022").GetProperty("relation").GetString());

        foreach (var decision in decisions.EnumerateArray())
        {
            Assert.Equal("HIGH", decision.GetProperty("semanticConfidence").GetString());
            Assert.Equal("SEMANTICALLY_FROZEN_BUT_BINDING_INCOMPLETE", decision.GetProperty("exactBindingStatus").GetString());
            Assert.False(decision.GetProperty("machineEvaluable").GetBoolean());
        }
    }

    [Fact]
    public void Benchmark_manifest_freezes_source_requests_before_provider_execution()
    {
        using var manifest = Load("artifacts/identity-benchmark/v1/manifest.json");
        var root = manifest.RootElement;

        Assert.Equal("READY_FOR_PROVIDER_EXECUTION", root.GetProperty("status").GetString());
        Assert.False(root.GetProperty("goldConsumedBeforePredictionFreeze").GetBoolean());
        Assert.Equal(0, root.GetProperty("goldReadCountBeforePredictionFreeze").GetInt32());
        Assert.Equal(0, root.GetProperty("actualNewCalls").GetInt32());
        Assert.True(root.GetProperty("plannedCalls").GetInt32() > 0);
        Assert.True(root.GetProperty("requestsFrozen").GetBoolean());
        Assert.False(root.GetProperty("predictionsFrozen").GetBoolean());
        Assert.Equal("READY_FOR_PROVIDER_EXECUTION", root.GetProperty("stopGate").GetString());
    }

    [Fact]
    public void Evidence_fabric_document_is_design_only()
    {
        var path = Path.Combine(TestRepository.Root(), "docs", "architecture", "generic-modular-pipeline-evidence-fabric.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("IMPLEMENTATION STATUS: NOT YET IMPLEMENTED", text, StringComparison.Ordinal);
        Assert.Contains("`GOLD` is evaluation authority only", text, StringComparison.Ordinal);
    }

    private static JsonElement Find(JsonElement decisions, string itemId) =>
        decisions.EnumerateArray().Single(item => item.GetProperty("itemId").GetString() == itemId);

    private static JsonDocument Load(string relativePath) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(TestRepository.Root(), relativePath)));

}
