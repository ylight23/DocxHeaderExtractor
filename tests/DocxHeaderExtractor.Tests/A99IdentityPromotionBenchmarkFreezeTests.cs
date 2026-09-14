using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Small, provider-free contract tests for the v1 benchmark preparation boundary.
/// These tests intentionally exercise only source/candidate/request/promotion contracts;
/// relation Gold is not an input to any case here.
/// </summary>
public sealed class A99IdentityPromotionBenchmarkFreezeTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Candidate_freeze_is_deterministic_and_gold_independent()
    {
        var source = new[]
        {
            Node("S1", 10, "Chapter I"),
            Node("S2", 11, "General provisions"),
            Node("S3", 20, "Article 1"),
        };

        var first = HdsaDeterministicIdentityCandidateGenerator.Generate(source);
        var second = HdsaDeterministicIdentityCandidateGenerator.Generate(source.Reverse());

        Assert.False(first.GoldUsed);
        Assert.False(first.CandidateGenerationIsRecallGate);
        Assert.Equal(
            JsonSerializer.Serialize(first.Candidates),
            JsonSerializer.Serialize(second.Candidates));
    }

    [Fact]
    public void Request_bytes_depend_on_source_and_target_not_relation_gold()
    {
        var request = new HdsaIdentityPairVerificationRequest(
            "catalog-sha",
            [RoleNode("A", 1), RoleNode("B", 2)],
            new("P01", "A", "B"),
            false);
        var requestBytes = JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions);

        // Gold relation metadata is deliberately not represented by the request DTO. Changing
        // an evaluation-side label therefore cannot change the frozen provider request bytes.
        var goldLabelA = "CONTINUATION_OF";
        var goldLabelB = "DISTINCT";
        Assert.NotEqual(goldLabelA, goldLabelB);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(requestBytes)),
            Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions))));
        Assert.DoesNotContain("CONTINUATION_OF", Encoding.UTF8.GetString(requestBytes));
        Assert.DoesNotContain("DISTINCT", Encoding.UTF8.GetString(requestBytes));
    }

    [Fact]
    public void Replay_cannot_reuse_response_for_a_different_target()
    {
        var request = new HdsaIdentityPairVerificationRequest(
            "catalog-sha",
            [RoleNode("A", 1), RoleNode("B", 2), RoleNode("C", 3)],
            new("P01", "A", "B"),
            false);
        var responseFromAnotherRequest = new HdsaIdentityPairVerificationResponse(
            "P02", "A", "C", "DISTINCT", "NONE");

        var validation = HdsaGlobalIdentityRetrieveVerifyContract.ValidateVerification(
            request, responseFromAnotherRequest);

        Assert.False(validation.Accepted);
        Assert.Equal("TARGET_PAIR_MISMATCH", validation.RejectionReason);
    }

    [Fact]
    public void Gold_and_legacy_cannot_authorize_benchmark_promotion()
    {
        var decision = HdsaSemanticIdentityInferenceDecision.ContinuationOf;
        var gold = HdsaSemanticIdentityPromotionPolicy.Evaluate(
            decision, "PARSER", "parser-evidence", true, goldUsed: true);
        var legacy = HdsaSemanticIdentityPromotionPolicy.Evaluate(
            decision, "PARSER", "parser-evidence", true, legacyUsed: true);

        Assert.All(new[] { gold, legacy }, result =>
        {
            Assert.Equal(HdsaSemanticIdentityPromotionAction.KeepSplit, result.Action);
            Assert.False(result.CollapseAuthorized);
        });
    }

    [Fact]
    public void Visual_source_identity_can_be_carried_without_relation_label()
    {
        var visual = new HdsaCandidateSourceNode(
            "IR-018:pdf-image-region:stable",
            ["IR-018:pdf-image-region:stable"],
            "Financial statement",
            2349);

        var json = JsonSerializer.Serialize(visual, JsonOptions);

        Assert.Contains("IR-018:pdf-image-region:stable", json);
        Assert.Contains("Financial statement", json);
        Assert.DoesNotContain("relation", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DISTINCT", json, StringComparison.Ordinal);
    }

    private static HdsaCandidateSourceNode Node(string id, int order, string text) =>
        new(id, [$"{id}-occurrence"], text, order);

    private static HdsaIdentityRoleNodeInput RoleNode(string id, int order) =>
        new(id, [$"{id}-occurrence"], $"text-{id}", order, "UNAVAILABLE", false);
}
