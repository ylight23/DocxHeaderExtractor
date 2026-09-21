using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

public sealed class A99IdentityPromotionBenchmarkV4EBTests
{
    private const string ArtifactRoot = "artifacts/identity-benchmark/v4/pruning-challenger/evaluation";

    private static JsonDocument Load(string fileName)
        => JsonDocument.Parse(File.ReadAllText(Path.Combine(TestRepository.Root(), ArtifactRoot.Replace('/', Path.DirectorySeparatorChar), fileName)));

    [Fact]
    public void V4E_B_opens_Gold_only_after_frozen_V4E_integrity_passes()
    {
        using var evaluation = Load("pruning-retention-evaluation.json");
        var root = evaluation.RootElement;
        var integrity = root.GetProperty("integrityBeforeGold");

        Assert.Equal("PASS", integrity.GetProperty("integrity").GetString());
        Assert.True(root.GetProperty("goldOpenedAfterFreeze").GetBoolean());
        Assert.Equal("USER_REVIEWED_IDENTITY_GOLD_FROZEN", root.GetProperty("goldAuthority").GetString());
        Assert.False(root.GetProperty("independentABGold").GetBoolean());
        Assert.Equal(7_702, integrity.GetProperty("shortlistCount").GetInt32());
        Assert.Equal(integrity.GetProperty("shortlistSha256").GetString(), root.GetProperty("v4eArtifacts").GetProperty("shortlistSha256").GetString());
    }

    [Fact]
    public void V4E_B_retains_all_positive_relations_and_excludes_distinct_diagnostics()
    {
        using var evaluation = Load("pruning-retention-evaluation.json");
        var metrics = evaluation.RootElement.GetProperty("metrics");

        Assert.Equal(3, metrics.GetProperty("v4BroadPositiveRecall").GetProperty("numerator").GetInt32());
        Assert.Equal(3, metrics.GetProperty("v4BroadPositiveRecall").GetProperty("denominator").GetInt32());
        Assert.Equal(3, metrics.GetProperty("v4ePositiveRetention").GetProperty("numerator").GetInt32());
        Assert.Equal(3, metrics.GetProperty("v4ePositiveRetention").GetProperty("denominator").GetInt32());
        Assert.Equal(1, metrics.GetProperty("continuationRetention").GetProperty("numerator").GetInt32());
        Assert.Equal(1, metrics.GetProperty("continuationRetention").GetProperty("denominator").GetInt32());
        Assert.Equal(2, metrics.GetProperty("sameRepeatRetention").GetProperty("numerator").GetInt32());
        Assert.Equal(2, metrics.GetProperty("sameRepeatRetention").GetProperty("denominator").GetInt32());
        Assert.Equal(2, metrics.GetProperty("distinctDiagnosticCoverage").GetArrayLength());
    }

    [Fact]
    public void V4E_B_case_lineage_distinguishes_V4_only_from_V3_existing()
    {
        using var report = Load("case-report.json");
        var cases = report.RootElement.GetProperty("cases")
            .EnumerateArray()
            .ToDictionary(item => item.GetProperty("itemId").GetString()!, StringComparer.Ordinal);

        Assert.Equal("RETAINED_V4_ONLY", cases["IR-019"].GetProperty("outcome").GetString());
        Assert.True(cases["IR-019"].GetProperty("v4Only").GetBoolean());
        Assert.True(cases["IR-019"].GetProperty("shortlistPresent").GetBoolean());
        Assert.Equal("RETAINED_V4_ONLY", cases["IR-020"].GetProperty("outcome").GetString());
        Assert.True(cases["IR-020"].GetProperty("shortlistPresent").GetBoolean());
        Assert.Equal("RETAINED_V3_EXISTING", cases["IR-021"].GetProperty("outcome").GetString());
        Assert.False(cases["IR-021"].GetProperty("v4Only").GetBoolean());
        Assert.True(cases["IR-021"].GetProperty("v3BroadPresent").GetBoolean());
    }

    [Fact]
    public void V4E_B_keeps_distinct_cases_as_diagnostics_not_false_negatives()
    {
        using var report = Load("case-report.json");
        var cases = report.RootElement.GetProperty("cases")
            .EnumerateArray()
            .Where(item => item.GetProperty("itemId").GetString() is "IR-018" or "IR-022")
            .ToArray();

        Assert.Equal(2, cases.Length);
        Assert.All(cases, item =>
        {
            Assert.False(item.GetProperty("isPositive").GetBoolean());
            Assert.Equal("DISTINCT_SEMANTIC_NODE", item.GetProperty("relation").GetString());
            Assert.Equal("SHORTLISTED", item.GetProperty("disposition").GetString());
        });
        using var failures = Load("failure-attribution.json");
        Assert.Empty(failures.RootElement.GetProperty("rows").EnumerateArray());
    }

    [Fact]
    public void V4E_B_does_not_execute_provider_or_promotion()
    {
        using var evaluation = Load("pruning-retention-evaluation.json");
        using var report = Load("case-report.json");
        var firewall = evaluation.RootElement.GetProperty("firewall");

        Assert.Equal(0, firewall.GetProperty("providerCalls").GetInt32());
        Assert.Equal(0, firewall.GetProperty("modelCalls").GetInt32());
        Assert.False(firewall.GetProperty("promotionExecution").GetBoolean());
        Assert.False(firewall.GetProperty("v4eArtifactsMutated").GetBoolean());
        Assert.Equal(0, report.RootElement.GetProperty("providerCalls").GetInt32());
        Assert.Equal(0, report.RootElement.GetProperty("modelCalls").GetInt32());
    }
}
