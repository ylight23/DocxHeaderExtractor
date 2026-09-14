using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

public sealed class A99IdentityPromotionReadinessAuditTests
{
    [Fact]
    public void Frozen_v1_workload_is_gold_blind_and_has_no_provider_execution()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(RepoPath("artifacts/identity-benchmark/v1/manifest.json")));
        var root = document.RootElement;

        Assert.Equal(0, root.GetProperty("goldReadCountBeforePredictionFreeze").GetInt32());
        Assert.False(root.GetProperty("goldConsumedBeforePredictionFreeze").GetBoolean());
        Assert.Equal(0, root.GetProperty("providerCallsUsedAsGoldAuthority").GetInt32());
        Assert.True(root.GetProperty("candidateOrPromotionOutputsUsedAsGoldAuthority").GetBoolean() == false);
        Assert.Equal(95999, root.GetProperty("plannedCalls").GetInt32());
    }

    [Fact]
    public void Frozen_request_manifest_has_one_entry_per_candidate_and_positive_sizes()
    {
        using var candidates = JsonDocument.Parse(File.ReadAllText(RepoPath("artifacts/identity-benchmark/v1/candidate-set.json")));
        using var requests = JsonDocument.Parse(File.ReadAllText(RepoPath("artifacts/identity-benchmark/v1/request-manifest.json")));

        var candidateCount = candidates.RootElement.GetProperty("candidateCount").GetInt32();
        var requestEntries = requests.RootElement.GetProperty("requests").EnumerateArray().ToArray();
        Assert.Equal(candidateCount, requestEntries.Length);
        Assert.All(requestEntries, entry => Assert.True(entry.GetProperty("requestBytes").GetInt32() > 0));
        Assert.All(requestEntries, entry => Assert.False(entry.GetProperty("requestSha256").GetString() is null or ""));
    }

    [Fact]
    public void Current_identity_pair_runner_has_no_pre_verifier_cap_or_pruning_contract()
    {
        var source = File.ReadAllText(RepoPath("src/DocxHeaderExtractor.Eval/ReasoningRetention/HdsaDeterministicCandidatePairVerificationLiveRunner.cs"));

        Assert.Contains("foreach (var candidate in candidateInput.Candidates)", source, StringComparison.Ordinal);
        Assert.Contains("new HdsaIdentityPairVerificationRequest", source, StringComparison.Ordinal);
        Assert.Contains("ContextSize = 1_000_000", source, StringComparison.Ordinal);
        Assert.Contains("MaxOutputTokens = 16_000", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MaxCandidates", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CandidateCap", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RankCandidates", source, StringComparison.OrdinalIgnoreCase);
    }

    private static string RepoPath(string relative) => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../", relative.Replace('/', Path.DirectorySeparatorChar)));
}
