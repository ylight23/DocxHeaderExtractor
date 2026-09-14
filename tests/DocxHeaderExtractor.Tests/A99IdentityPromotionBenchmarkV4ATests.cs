using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

public sealed class A99IdentityPromotionBenchmarkV4ATests
{
    [Fact]
    public void V4A_keeps_forensics_offline_and_does_not_modify_v3()
    {
        using var forensic = JsonDocument.Parse(File.ReadAllText(RepoPath("artifacts/identity-benchmark/v4/retrieval-failure-forensics.v1.json")));
        using var coverage = JsonDocument.Parse(File.ReadAllText(RepoPath("artifacts/identity-benchmark/v4/candidate-rule-coverage.json")));
        var root = forensic.RootElement;
        Assert.Equal("READY_FOR_V4_RETRIEVAL_CHALLENGER_DESIGN", root.GetProperty("metadata").GetProperty("status").GetString());
        Assert.Equal(0, root.GetProperty("metadata").GetProperty("providerCalls").GetInt32());
        Assert.Equal(0, root.GetProperty("metadata").GetProperty("modelCalls").GetInt32());
        Assert.False(root.GetProperty("metadata").GetProperty("v1V2V3ArtifactsModified").GetBoolean());
        Assert.False(coverage.RootElement.GetProperty("goldUsedForGeneration").GetBoolean());
        Assert.Equal(95999, coverage.RootElement.GetProperty("totalBroadCandidates").GetInt32());
    }

    [Fact]
    public void V4A_traces_the_two_broad_misses_and_keeps_ir021_as_contrast()
    {
        using var forensic = JsonDocument.Parse(File.ReadAllText(RepoPath("artifacts/identity-benchmark/v4/retrieval-failure-forensics.v1.json")));
        var traces = forensic.RootElement.GetProperty("targetTrace").EnumerateArray().ToDictionary(item => item.GetProperty("itemId").GetString()!);
        Assert.False(traces["IR-019"].GetProperty("currentCandidatePresent").GetBoolean());
        Assert.False(traces["IR-020"].GetProperty("currentCandidatePresent").GetBoolean());
        Assert.True(traces["IR-021"].GetProperty("currentCandidatePresent").GetBoolean());
        Assert.Equal("TEXT_SIGNAL_FAILED", traces["IR-019"].GetProperty("textAffinityRule").GetProperty("status").GetString());
        Assert.Equal("TEXT_SIGNAL_FAILED", traces["IR-020"].GetProperty("textAffinityRule").GetProperty("status").GetString());
        Assert.Equal("ELIGIBLE", traces["IR-021"].GetProperty("textAffinityRule").GetProperty("status").GetString());
    }

    [Fact]
    public void V4A_does_not_emit_a_v4_shortlist_or_provider_artifact()
    {
        Assert.False(File.Exists(RepoPath("artifacts/identity-benchmark/v4/shortlist.json")));
        Assert.False(File.Exists(RepoPath("artifacts/identity-benchmark/v4/request-manifest.json")));
        using var counterfactual = JsonDocument.Parse(File.ReadAllText(RepoPath("artifacts/identity-benchmark/v4/counterfactual-source-only-signals.json")));
        Assert.Equal("COUNTERFACTUAL_ONLY_NO_RULE_IMPLEMENTED", counterfactual.RootElement.GetProperty("status").GetString());
        Assert.Equal(0, counterfactual.RootElement.GetProperty("providerCalls").GetInt32());
    }

    private static string RepoPath(string relative) => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../", relative.Replace('/', Path.DirectorySeparatorChar)));
}
