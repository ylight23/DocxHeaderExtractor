using System.Text.Json;
using DocxHeaderExtractor.Core.Models;

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

    [Fact]
    public void Canonical_request_template_is_byte_identical_to_direct_builder()
    {
        var nodes = new[]
        {
            new HdsaIdentityRoleNodeInput("S1", ["S1"], "Heading 😀", 0, "UNAVAILABLE", false),
            new HdsaIdentityRoleNodeInput("S2", ["S2"], "Second", 1, "UNAVAILABLE", false),
        };
        var target = new HdsaIdentityCandidatePair("P1", "S1", "S2");
        var request = new HdsaIdentityPairVerificationRequest("catalog", nodes, target, false);

        var direct = HdsaCanonicalPairVerifierRequestBuilder.Build(request);
        var templated = HdsaCanonicalPairVerifierRequestBuilder.CreateTemplate("catalog", nodes).Build(target);

        Assert.Equal(direct.Sha256, templated.Sha256);
        Assert.Equal(direct.Utf8Bytes, templated.Utf8Bytes);
    }

    [Fact]
    public void Frozen_request_guard_rejects_tampered_manifest_hash_before_transport()
    {
        var request = new HdsaIdentityPairVerificationRequest(
            "catalog", [new HdsaIdentityRoleNodeInput("S1", ["S1"], "Heading", 0, "UNAVAILABLE", false)],
            new HdsaIdentityCandidatePair("P1", "S1", "S1"), false);
        var built = HdsaCanonicalPairVerifierRequestBuilder.Build(request);

        var result = HdsaCanonicalPairVerifierRequestGuard.Verify(built, "tampered", built.Utf8Bytes.Length);

        Assert.False(result.Accepted);
        Assert.Equal("REQUEST_SHA256_MISMATCH", result.RejectionReason);
    }

    [Fact]
    public void V2_freeze_closes_parity_without_provider_or_gold()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(RepoPath("artifacts/identity-benchmark/v2/manifest.json")));
        using var parity = JsonDocument.Parse(File.ReadAllText(RepoPath("artifacts/identity-benchmark/v2/request-parity-report.json")));
        using var firewall = JsonDocument.Parse(File.ReadAllText(RepoPath("artifacts/identity-benchmark/v2/firewall.json")));

        var manifestRoot = manifest.RootElement;
        Assert.Equal("BLOCKED_ON_PRE_VERIFIER_SCALABILITY", manifestRoot.GetProperty("status").GetString());
        Assert.Equal(95999, manifestRoot.GetProperty("candidateCount").GetInt32());
        Assert.Equal(95999, manifestRoot.GetProperty("requestCount").GetInt32());
        Assert.False(manifestRoot.GetProperty("exactBytesPersisted").GetBoolean());
        Assert.True(manifestRoot.GetProperty("exactBytesDeterministicallyReconstructible").GetBoolean());
        Assert.True(manifestRoot.GetProperty("manifestConsumedByDryRunExecutor").GetBoolean());
        Assert.False(manifestRoot.GetProperty("liveRunnerConsumesManifest").GetBoolean());
        Assert.Equal(0, manifestRoot.GetProperty("providerCalls").GetInt32());
        Assert.Equal(0, manifestRoot.GetProperty("goldReadCountBeforePredictionFreeze").GetInt32());

        var parityRoot = parity.RootElement;
        Assert.True(parityRoot.GetProperty("allRequestedChecksPass").GetBoolean());
        Assert.Equal(11, parityRoot.GetProperty("historicalDoc0205").GetProperty("matchingRows").GetInt32());
        Assert.Equal(12, parityRoot.GetProperty("crossDocument").GetProperty("matchingRows").GetInt32());
        Assert.True(parityRoot.GetProperty("reconstruction").GetProperty("allSamplesIdentical").GetBoolean());
        Assert.Equal(95999, parityRoot.GetProperty("executorDryRun").GetProperty("checkedEntries").GetInt32());
        Assert.Equal(0, parityRoot.GetProperty("executorDryRun").GetProperty("rejectedEntries").GetInt32());

        var firewallRoot = firewall.RootElement;
        Assert.Equal(0, firewallRoot.GetProperty("providerCalls").GetInt32());
        Assert.Equal(0, firewallRoot.GetProperty("goldReadCount").GetInt32());
        Assert.True(firewallRoot.GetProperty("manifestConsumedByDryRunExecutor").GetBoolean());
        Assert.True(firewallRoot.GetProperty("allManifestEntriesGuarded").GetBoolean());
        Assert.False(firewallRoot.GetProperty("pruningAdded").GetBoolean());
    }

    [Fact]
    public void Current_generic_runner_uses_the_shared_canonical_builder()
    {
        var source = File.ReadAllText(RepoPath("src/DocxHeaderExtractor.Eval/ReasoningRetention/HdsaGlobalIdentityRetrieveVerifyLiveRunner.cs"));

        Assert.Contains("HdsaCanonicalPairVerifierRequestBuilder.Build", source, StringComparison.Ordinal);
        Assert.DoesNotContain("JsonSerializer.Serialize(verificationRequest", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Canonical_request_builder_has_no_relation_field_or_model_echo_requirement()
    {
        var request = new HdsaIdentityPairVerificationRequest(
            "catalog",
            [new HdsaIdentityRoleNodeInput("S1", ["S1"], "Heading", 0, "UNAVAILABLE", false)],
            new HdsaIdentityCandidatePair("P1", "S1", "S1"),
            false);
        var built = HdsaCanonicalPairVerifierRequestBuilder.Build(request);

        Assert.DoesNotContain("relation", built.Json, StringComparison.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(built.Json);
        Assert.False(document.RootElement.GetProperty("goldDerivedInput").GetBoolean());
    }

    [Fact]
    public void Frozen_request_guard_rejects_tampered_byte_length_before_transport()
    {
        var request = new HdsaIdentityPairVerificationRequest(
            "catalog", [new HdsaIdentityRoleNodeInput("S1", ["S1"], "Heading", 0, "UNAVAILABLE", false)],
            new HdsaIdentityCandidatePair("P1", "S1", "S1"), false);
        var built = HdsaCanonicalPairVerifierRequestBuilder.Build(request);

        var result = HdsaCanonicalPairVerifierRequestGuard.Verify(built, built.Sha256, built.Utf8Bytes.Length + 1);

        Assert.False(result.Accepted);
        Assert.Equal("REQUEST_BYTE_LENGTH_MISMATCH", result.RejectionReason);
    }

    private static string RepoPath(string relative) => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../", relative.Replace('/', Path.DirectorySeparatorChar)));
}
