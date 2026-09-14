using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

public sealed class A99IdentityPromotionBenchmarkV4BTests
{
    [Fact]
    public void V4B_freezes_dev_exposed_source_only_broad_challenger()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(RepoPath("artifacts/identity-benchmark/v4/challenger/manifest.json")));
        using var firewall = JsonDocument.Parse(File.ReadAllText(RepoPath("artifacts/identity-benchmark/v4/challenger/firewall.json")));
        var m = manifest.RootElement;
        Assert.Equal("READY_FOR_V4_RETRIEVAL_EVALUATION", m.GetProperty("status").GetString());
        Assert.Equal("DEV_EXPOSED_CHALLENGER", m.GetProperty("developmentStatus").GetString());
        Assert.False(m.GetProperty("independentGeneralizationClaim").GetBoolean());
        Assert.Equal(95999, m.GetProperty("v3BroadCandidateCount").GetInt32());
        Assert.True(m.GetProperty("v4AddedCandidateCount").GetInt32() > 0);
        Assert.Equal(m.GetProperty("v3BroadCandidateCount").GetInt32() + m.GetProperty("v4AddedCandidateCount").GetInt32(), m.GetProperty("v4BroadCandidateCount").GetInt32());
        Assert.Equal(0, m.GetProperty("providerCalls").GetInt32());
        Assert.Equal(0, m.GetProperty("goldReadCount").GetInt32());
        Assert.Equal("PASS", m.GetProperty("metamorphicSelfTests").GetString());
        Assert.False(firewall.RootElement.GetProperty("relationLabelsConsumed").GetBoolean());
        Assert.False(firewall.RootElement.GetProperty("promotionApplied").GetBoolean());
        Assert.False(firewall.RootElement.GetProperty("v3ArtifactsModified").GetBoolean());
    }

    [Fact]
    public void V4B_contains_all_three_generic_signal_families_and_no_requests()
    {
        using var contract = JsonDocument.Parse(File.ReadAllText(RepoPath("artifacts/identity-benchmark/v4/challenger/retrieval-contract.json")));
        using var scale = JsonDocument.Parse(File.ReadAllText(RepoPath("artifacts/identity-benchmark/v4/challenger/scalability-report.json")));
        using var additions = JsonDocument.Parse(File.ReadAllText(RepoPath("artifacts/identity-benchmark/v4/challenger/candidate-additions.json")));
        var names = contract.RootElement.GetProperty("signals").EnumerateArray().Select(item => item.GetProperty("name").GetString()).ToArray();
        Assert.Contains("SHARED_STRUCTURAL_HEADING_KEY", names);
        Assert.Contains("TERMINAL_ACRONYM_VARIANT", names);
        Assert.Contains("EXPLICIT_CONTINUATION_VARIANT", names);
        Assert.False(contract.RootElement.GetProperty("signals").EnumerateArray().Any(item => item.GetProperty("impliesRelation").GetBoolean()));
        Assert.False(File.Exists(RepoPath("artifacts/identity-benchmark/v4/challenger/request-manifest.json")));
        Assert.False(File.Exists(RepoPath("artifacts/identity-benchmark/v4/challenger/prediction.json")));
        Assert.Equal(0, additions.RootElement.GetProperty("providerCalls").GetInt32());
        Assert.NotEqual("BLOCKED_ON_V4_RETRIEVAL_EXPLOSION", scale.RootElement.GetProperty("scaleGuard").GetString());
    }

    [Fact]
    public void V4B_records_source_provenance_without_gold_fields()
    {
        using var source = JsonDocument.Parse(File.ReadAllText(RepoPath("artifacts/identity-benchmark/v4/challenger/source-reference.json")));
        using var additions = JsonDocument.Parse(File.ReadAllText(RepoPath("artifacts/identity-benchmark/v4/challenger/candidate-additions.json")));
        Assert.Equal(0, source.RootElement.GetProperty("goldReadCount").GetInt32());
        Assert.All(additions.RootElement.GetProperty("candidates").EnumerateArray(), item =>
        {
            Assert.True(item.GetProperty("sourceEvidenceReferences").GetArrayLength() > 0);
            Assert.DoesNotContain("gold", item.GetRawText(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("relation", item.GetRawText(), StringComparison.OrdinalIgnoreCase);
        });
    }

    private static string RepoPath(string relative) => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../", relative.Replace('/', Path.DirectorySeparatorChar)));
}
