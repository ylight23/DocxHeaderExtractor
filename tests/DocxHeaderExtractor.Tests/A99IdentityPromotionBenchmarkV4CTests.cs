using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

public sealed class A99IdentityPromotionBenchmarkV4CTests
{
    private static string Root() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
    private static JsonDocument Load(string path) => JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), path.Replace('/', Path.DirectorySeparatorChar))));

    [Fact]
    public void V4C_records_integrity_pass_before_gold_and_no_provider_execution()
    {
        using var evaluation = Load("artifacts/identity-benchmark/v4/evaluation/broad-retrieval-evaluation.json");
        var root = evaluation.RootElement;
        Assert.Equal("PASS", root.GetProperty("integrityBeforeGold").GetProperty("integrity").GetString());
        Assert.True(root.GetProperty("goldOpenedAfterFreeze").GetBoolean());
        Assert.Equal(0, root.GetProperty("firewall").GetProperty("providerCalls").GetInt32());
        Assert.Equal(0, root.GetProperty("firewall").GetProperty("modelCalls").GetInt32());
        Assert.False(root.GetProperty("firewall").GetProperty("requestsCreated").GetBoolean());
        Assert.False(root.GetProperty("firewall").GetProperty("predictionsCreated").GetBoolean());
    }

    [Fact]
    public void V4C_uses_only_positive_relations_for_recall_and_keeps_distinct_as_diagnostic()
    {
        using var evaluation = Load("artifacts/identity-benchmark/v4/evaluation/broad-retrieval-evaluation.json");
        var metrics = evaluation.RootElement.GetProperty("metrics");
        Assert.Equal(3, metrics.GetProperty("positiveDenominator").GetInt32());
        Assert.Equal(2, metrics.GetProperty("distinctDiagnostics").GetArrayLength());
        Assert.DoesNotContain(metrics.GetProperty("distinctDiagnostics").EnumerateArray(), item => item.GetProperty("itemId").GetString() is "IR-019" or "IR-020" or "IR-021");
    }

    [Fact]
    public void V4C_classifies_lineage_and_does_not_double_count_overlapping_reasons()
    {
        using var cases = Load("artifacts/identity-benchmark/v4/evaluation/case-report.json");
        var rows = cases.RootElement.GetProperty("cases").EnumerateArray().ToDictionary(item => item.GetProperty("itemId").GetString()!);
        Assert.Equal("RETRIEVED_V4_MULTI_SIGNAL", rows["IR-019"].GetProperty("outcome").GetString());
        Assert.True(rows["IR-019"].GetProperty("v4Only").GetBoolean());
        Assert.Equal("RETRIEVED_V4_MULTI_SIGNAL", rows["IR-020"].GetProperty("outcome").GetString());
        Assert.True(rows["IR-020"].GetProperty("v4Only").GetBoolean());
        Assert.Contains("TERMINAL_ACRONYM_VARIANT", rows["IR-020"].GetProperty("reasons").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal("RETRIEVED_V3_EXISTING", rows["IR-021"].GetProperty("outcome").GetString());
        Assert.False(rows["IR-021"].GetProperty("v4Only").GetBoolean());
        Assert.Equal(3, cases.RootElement.GetProperty("cases").EnumerateArray().Count(item => item.GetProperty("isPositive").GetBoolean() && item.GetProperty("broadPresent").GetBoolean()));
    }

    [Fact]
    public void V4C_does_not_mutate_frozen_V4B_broad_artifact()
    {
        using var evaluation = Load("artifacts/identity-benchmark/v4/evaluation/broad-retrieval-evaluation.json");
        using var manifest = Load("artifacts/identity-benchmark/v4/challenger/manifest.json");
        Assert.Equal(manifest.RootElement.GetProperty("v4BroadCandidateSetSha256").GetString(), evaluation.RootElement.GetProperty("firewall").GetProperty("broadCandidateSha256").GetString());
        Assert.Equal(96_069, evaluation.RootElement.GetProperty("metrics").GetProperty("candidateVolume").GetProperty("v4Broad").GetInt32());
    }
}
