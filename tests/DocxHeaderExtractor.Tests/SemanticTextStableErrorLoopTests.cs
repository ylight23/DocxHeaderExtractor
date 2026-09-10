using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

public sealed class SemanticTextStableErrorLoopTests
{
    private const string Root = "eval/a99-closed-loop/semantic-text-stable-error-loop";

    [Fact]
    public void Offline_loop_verifies_frozen_baseline_and_keeps_gold_firewall()
    {
        using var summary = Load("summary.v1.json");
        var root = summary.RootElement;

        Assert.Equal("TERMINAL_OFFLINE_FORENSIC", root.GetProperty("status").GetString());
        Assert.Equal(15, root.GetProperty("successfulCells").GetInt32());
        Assert.Equal(0, root.GetProperty("providerCalls").GetInt32());
        Assert.Equal(0, root.GetProperty("modelCalls").GetInt32());
        Assert.Equal("MODEL_CAPABILITY_STABLE_GAP", root.GetProperty("terminalClassification").GetString());
        Assert.Equal("PASS", root.GetProperty("goldFirewall").GetString());
        Assert.False(root.GetProperty("runtimeGoldLeakage").GetBoolean());
        Assert.Equal(0, root.GetProperty("expectedProjectionExclusion").GetInt32());
        Assert.Equal(0, root.GetProperty("systemBugLoss").GetInt32());

        var aggregate = root.GetProperty("canonicalBaseline").GetProperty("aggregate");
        Assert.Equal((459, 425, 22, 34), (
            aggregate.GetProperty("gold").GetInt32(),
            aggregate.GetProperty("tp").GetInt32(),
            aggregate.GetProperty("fp").GetInt32(),
            aggregate.GetProperty("fn").GetInt32()));
        Assert.Equal(0.9381898454746137, aggregate.GetProperty("f1").GetDouble(), 12);

        var firstLoss = root.GetProperty("firstLossBuckets").EnumerateArray()
            .ToDictionary(x => x.GetProperty("bucket").GetString()!, x => x.GetProperty("count").GetInt32());
        Assert.Equal(16, firstLoss["MODEL_OMISSION"]);
        Assert.Equal(5, firstLoss["MODEL_WRONG_TEXT_BOUNDARY"]);
        Assert.Equal(3, firstLoss["MODEL_WRONG_TEXT"]);
        Assert.Equal(10, firstLoss["SYSTEM_AMBIGUOUS_DUPLICATE_TEXT"]);
        Assert.DoesNotContain("MODEL_TRUE_EXTRA", firstLoss.Keys);

        var enrichment = root.GetProperty("enrichmentV1Status").GetString();
        Assert.Equal("REJECTED_REGRESSION", enrichment);
    }

    [Fact]
    public void Stable_error_inventory_preserves_repeat_and_causal_evidence()
    {
        using var inventory = Load("stable-errors.v1.json");
        var root = inventory.RootElement;
        Assert.False(root.GetProperty("runtimeGoldLeakage").GetBoolean());
        Assert.Equal("PASS", root.GetProperty("goldFirewall").GetString());

        var stability = root.GetProperty("stability");
        Assert.Equal(9, stability.GetProperty("stableFn").GetInt32());
        Assert.Equal(3, stability.GetProperty("stableFp").GetInt32());
        Assert.Equal("MODEL_TRUE_EXTRA", stability.GetProperty("largestStableBucket").GetString());

        var baseline = Load("baseline.v1.json");
        Assert.Equal(15, baseline.RootElement.GetProperty("successfulCells").GetInt32());
        Assert.False(baseline.RootElement.GetProperty("verification").GetProperty("configurationSignaturePersisted").GetBoolean());
    }

    private static JsonDocument Load(string name) => JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), Root, name)));
    private static string RepoRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
}
