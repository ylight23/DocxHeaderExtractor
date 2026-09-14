using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

public sealed class A99IdentityPromotionBenchmarkV3BTests
{
    [Fact]
    public void V3B_evaluates_only_after_v3a_integrity_and_keeps_positive_denominator_separate()
    {
        using var evaluation = JsonDocument.Parse(File.ReadAllText(RepoPath("artifacts/identity-benchmark/v3/evaluation/retrieval-evaluation.json")));
        using var cases = JsonDocument.Parse(File.ReadAllText(RepoPath("artifacts/identity-benchmark/v3/evaluation/case-report.json")));
        var root = evaluation.RootElement;

        Assert.Equal("V3_RETRIEVAL_RECALL_FAILURE", root.GetProperty("status").GetString());
        Assert.True(root.GetProperty("goldOpenedAfterV3AIntegrity").GetBoolean());
        Assert.Equal("USER_REVIEWED_IDENTITY_GOLD_FROZEN", root.GetProperty("goldAuthority").GetString());
        Assert.False(root.GetProperty("independentABGold").GetBoolean());
        Assert.Equal(3, root.GetProperty("metrics").GetProperty("positiveDenominator").GetInt32());
        Assert.Equal(0, root.GetProperty("firewall").GetProperty("providerCalls").GetInt32());
        Assert.Equal(0, root.GetProperty("firewall").GetProperty("modelCalls").GetInt32());
        Assert.Equal(5, cases.RootElement.GetProperty("cases").GetArrayLength());
    }

    [Fact]
    public void V3B_does_not_claim_retrieval_precision_from_distinct_diagnostics()
    {
        var raw = File.ReadAllText(RepoPath("artifacts/identity-benchmark/v3/evaluation/retrieval-evaluation.json"));
        Assert.DoesNotContain("retrievalPrecision", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("precision", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void V3B_preserves_v3a_freeze_hashes_and_reports_failure_attribution()
    {
        using var evaluation = JsonDocument.Parse(File.ReadAllText(RepoPath("artifacts/identity-benchmark/v3/evaluation/retrieval-evaluation.json")));
        using var failures = JsonDocument.Parse(File.ReadAllText(RepoPath("artifacts/identity-benchmark/v3/evaluation/retrieval-failure-attribution.json")));
        var root = evaluation.RootElement;
        Assert.Equal("PASS", root.GetProperty("v3aFreeze").GetProperty("integrity").GetString());
        Assert.True(root.GetProperty("firewall").GetProperty("v3aArtifactsMutated").GetBoolean() == false);
        Assert.True(failures.RootElement.GetProperty("rows").GetArrayLength() > 0);
        Assert.Equal(0, failures.RootElement.GetProperty("providerCalls").GetInt32());
    }

    private static string RepoPath(string relative) => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../", relative.Replace('/', Path.DirectorySeparatorChar)));
}
