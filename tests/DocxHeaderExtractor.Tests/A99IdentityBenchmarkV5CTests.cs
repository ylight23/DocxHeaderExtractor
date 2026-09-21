using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

public sealed class A99IdentityBenchmarkV5CTests
{

    [Fact]
    public void V5C_prediction_freeze_is_before_gold_and_uses_no_calls()
    {
        var path = Path.Combine(TestRepository.Root(), "artifacts", "identity-benchmark", "v5", "semantic-node-induction", "evaluation", "prediction-freeze.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var json = doc.RootElement;
        Assert.Equal("V5C_PREDICTIONS_FROZEN_BEFORE_GOLD", json.GetProperty("status").GetString());
        Assert.Equal(128, json.GetProperty("candidateCount").GetInt32());
        Assert.Equal(0, json.GetProperty("goldReadCount").GetInt32());
        Assert.False(json.GetProperty("pairLabelsDerived").GetBoolean());
    }

    [Fact]
    public void V5C_evaluation_is_offline_and_has_failure_ownership()
    {
        var path = Path.Combine(TestRepository.Root(), "artifacts", "identity-benchmark", "v5", "semantic-node-induction", "evaluation", "manifest.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var json = doc.RootElement;
        Assert.Equal("V5C_COMPLETE", json.GetProperty("status").GetString());
        Assert.True(json.GetProperty("goldReadAfterPredictionFreeze").GetBoolean());
        Assert.Equal(0, json.GetProperty("modelCalls").GetInt32());
        Assert.Equal(0, json.GetProperty("providerCalls").GetInt32());
        Assert.True(json.GetProperty("pairLabelsDerived").GetBoolean());
    }

    [Fact]
    public void V5C_v2_is_direction_agnostic_and_reports_node_constraints()
    {
        var root = TestRepository.Root();
        var manifestPath = Path.Combine(root, "artifacts", "identity-benchmark", "v5", "semantic-node-induction", "evaluation-v2", "manifest.json");
        var summaryPath = Path.Combine(root, "artifacts", "identity-benchmark", "v5", "semantic-node-induction", "evaluation-v2", "summary.json");
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        using var summary = JsonDocument.Parse(File.ReadAllText(summaryPath));
        Assert.Equal("V5C_V2_COMPLETE", manifest.RootElement.GetProperty("status").GetString());
        Assert.True(manifest.RootElement.GetProperty("directionAgnosticEdgeDerivation").GetBoolean());
        Assert.Equal(1, summary.RootElement.GetProperty("continuationOnlyErrors").GetInt32());
        Assert.Equal(47, summary.RootElement.GetProperty("nodeConstraint").GetProperty("correct").GetInt32());
        Assert.Equal(103, summary.RootElement.GetProperty("nodeConstraint").GetProperty("validPairs").GetInt32());
    }
}
