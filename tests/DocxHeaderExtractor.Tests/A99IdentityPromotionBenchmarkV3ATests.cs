using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

public sealed class A99IdentityPromotionBenchmarkV3ATests
{
    [Fact]
    public void V3A_freezes_source_only_shortlist_without_opening_gold()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(RepoPath("artifacts/identity-benchmark/v3/manifest.json")));
        using var v2Manifest = JsonDocument.Parse(File.ReadAllText(RepoPath("artifacts/identity-benchmark/v2/manifest.json")));
        using var ranking = JsonDocument.Parse(File.ReadAllText(RepoPath("artifacts/identity-benchmark/v3/candidate-ranking-config.json")));
        using var inventory = JsonDocument.Parse(File.ReadAllText(RepoPath("artifacts/identity-benchmark/v3/source-only-feature-inventory.json")));
        using var firewall = JsonDocument.Parse(File.ReadAllText(RepoPath("artifacts/identity-benchmark/v3/firewall.json")));

        var m = manifest.RootElement;
        Assert.Equal("READY_FOR_V3_RETRIEVAL_EVALUATION", m.GetProperty("status").GetString());
        Assert.Equal(95999, m.GetProperty("broadCandidateCount").GetInt32());
        Assert.Equal(v2Manifest.RootElement.GetProperty("candidateSetSha256").GetString(), m.GetProperty("broadCandidateSetSha256").GetString());
        Assert.True(m.GetProperty("shortlistedCount").GetInt32() > 0);
        Assert.True(m.GetProperty("shortlistedCount").GetInt32() < m.GetProperty("broadCandidateCount").GetInt32());
        Assert.False(m.GetProperty("exactBytesPersisted").GetBoolean());
        Assert.True(m.GetProperty("exactBytesDeterministicallyReconstructible").GetBoolean());
        Assert.Equal(0, m.GetProperty("providerCalls").GetInt32());
        Assert.Equal(0, m.GetProperty("goldReadCount").GetInt32());

        Assert.False(ranking.RootElement.GetProperty("modelUsed").GetBoolean());
        Assert.False(ranking.RootElement.GetProperty("goldUsed").GetBoolean());
        Assert.Contains(ranking.RootElement.GetProperty("dimensions").EnumerateArray(), item => item.GetString() == "pairIdAscending");

        var featureNames = inventory.RootElement.GetProperty("features").EnumerateArray().Select(item => item.GetProperty("name").GetString()).ToArray();
        Assert.Contains("normalizedTextAffinity", featureNames);
        Assert.Equal("UNAVAILABLE", inventory.RootElement.GetProperty("features").EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "modelScore").GetProperty("availability").GetString());
        Assert.Equal("UNAVAILABLE", inventory.RootElement.GetProperty("features").EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "goldRelationLabel").GetProperty("availability").GetString());

        var f = firewall.RootElement;
        Assert.False(f.GetProperty("goldArtifactLoaded").GetBoolean());
        Assert.False(f.GetProperty("modelDerivedFeatures").GetBoolean());
        Assert.False(f.GetProperty("goldDerivedFeatures").GetBoolean());
        Assert.True(f.GetProperty("everyBroadCandidateHasDisposition").GetBoolean());
        Assert.False(f.GetProperty("shortlistBudgetExceeded").GetBoolean());
        Assert.True(f.GetProperty("requestShaCheckedBeforeNetwork").GetBoolean());
        Assert.Equal(0, f.GetProperty("dryRunProviderCalls").GetInt32());
    }

    [Fact]
    public void V3A_pruning_decisions_cover_every_broad_candidate_without_gold_fields()
    {
        using var candidates = JsonDocument.Parse(File.ReadAllText(RepoPath("artifacts/identity-benchmark/v1/candidate-set.json")));
        using var decisions = JsonDocument.Parse(File.ReadAllText(RepoPath("artifacts/identity-benchmark/v3/candidate-pruning-decisions.json")));

        var broad = candidates.RootElement.GetProperty("candidates").EnumerateArray().Select(item => item.GetProperty("pairId").GetString()!).ToHashSet(StringComparer.Ordinal);
        var rows = decisions.RootElement.GetProperty("decisions").EnumerateArray().ToArray();
        Assert.Equal(95999, rows.Length);
        Assert.Equal(broad, rows.Select(item => item.GetProperty("candidateId").GetString()!).ToHashSet(StringComparer.Ordinal));
        Assert.All(rows, row => Assert.Contains(row.GetProperty("decision").GetString(), new[] { "SHORTLISTED", "PRUNED_DOMINATED", "PRUNED_BUDGET" }));
        Assert.All(rows, row => Assert.DoesNotContain("gold", row.GetRawText(), StringComparison.OrdinalIgnoreCase));
        Assert.All(rows, row => Assert.DoesNotContain("model", row.GetRawText(), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void V3A_request_manifest_is_bounded_and_uses_the_v2_builder()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(RepoPath("artifacts/identity-benchmark/v3/manifest.json")));
        using var requests = JsonDocument.Parse(File.ReadAllText(RepoPath("artifacts/identity-benchmark/v3/request-manifest.json")));
        using var shortlist = JsonDocument.Parse(File.ReadAllText(RepoPath("artifacts/identity-benchmark/v3/shortlist-manifest.json")));

        var m = manifest.RootElement;
        var r = requests.RootElement;
        Assert.Equal(m.GetProperty("requestCount").GetInt32(), r.GetProperty("requestCount").GetInt32());
        Assert.True(r.GetProperty("requestCount").GetInt32() < 95999);
        Assert.Equal("hdsa-canonical-pair-verifier-request-builder-v1", r.GetProperty("canonicalBuilderVersion").GetString());
        Assert.False(r.GetProperty("goldDerivedInput").GetBoolean());
        Assert.False(r.GetProperty("exactBytesPersisted").GetBoolean());
        Assert.All(r.GetProperty("requests").EnumerateArray(), item =>
        {
            Assert.True(item.GetProperty("requestBytes").GetInt32() > 0);
            Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("requestSha256").GetString()));
            Assert.False(item.GetProperty("reconstruction").GetProperty("goldDerivedInput").GetBoolean());
        });
        Assert.Equal(m.GetProperty("shortlistedCount").GetInt32(), shortlist.RootElement.GetProperty("shortlistedCount").GetInt32());
        Assert.Equal(m.GetProperty("shortlistSha256").GetString(), shortlist.RootElement.GetProperty("shortlistSha256").GetString());
    }

    [Fact]
    public void V3A_tool_has_no_provider_or_gold_execution_path()
    {
        var source = File.ReadAllText(RepoPath("tools/identity-promotion-benchmark-v3a/Program.cs"));

        Assert.Contains("PROVIDER_CALLS=0", source, StringComparison.Ordinal);
        Assert.Contains("GOLD_READS=0", source, StringComparison.Ordinal);
        Assert.DoesNotContain("relationGold", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GoldUsed = true", source, StringComparison.Ordinal);
        Assert.Contains("HdsaCanonicalPairVerifierRequestBuilder", source, StringComparison.Ordinal);
    }

    private static string RepoPath(string relative) => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../", relative.Replace('/', Path.DirectorySeparatorChar)));
}
