using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

public sealed class A99IdentityBenchmarkV7DTests
{
    private static string ProjectionPath => Path.Combine(TestRepository.Root(), "artifacts/identity-benchmark/v7/conservative-clustering/evaluation-v1-dev-regression/projection-freeze.json");
    private static string SummaryPath => Path.Combine(TestRepository.Root(), "artifacts/identity-benchmark/v7/conservative-clustering/evaluation-v1-dev-regression/summary.json");

    [Fact]
    public void V7D_projection_freezes_continuation_as_zero_before_gold_join()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(ProjectionPath));
        var root = doc.RootElement;
        Assert.Equal("V7D_PROJECTION_FROZEN_BEFORE_GOLD_JOIN", root.GetProperty("status").GetString());
        Assert.Equal(0, root.GetProperty("continuationPredictionCount").GetInt32());
        Assert.True(root.GetProperty("continuationInferenceForbidden").GetBoolean());
        Assert.Equal(0, root.GetProperty("goldReadCount").GetInt32());
    }

    [Fact]
    public void V7D_freezes_dev_metrics_and_never_treats_missing_continuation_as_inferred()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(SummaryPath));
        var root = doc.RootElement;
        Assert.Equal(128, root.GetProperty("total").GetInt32());
        Assert.Equal(87, root.GetProperty("correct").GetInt32());
        Assert.Equal(87, root.GetProperty("nodeConstraintCorrect").GetInt32());
        Assert.Equal(23, root.GetProperty("falseMerge").GetInt32());
        Assert.Equal(18, root.GetProperty("falseSplit").GetInt32());
        Assert.Equal(0, root.GetProperty("continuationPredictions").GetInt32());
        Assert.Equal(0, root.GetProperty("wrongRelationWithinCluster").GetInt32());
        Assert.Equal(0, root.GetProperty("providerCalls").GetInt32());
    }
}
