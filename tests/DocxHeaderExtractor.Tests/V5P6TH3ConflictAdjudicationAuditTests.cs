using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>Offline score of the two frozen H3 disagreements; Gold is read only here, after capture.</summary>
public sealed class V5P6TH3ConflictAdjudicationAuditTests
{
    private const string RegistryPath = "eval/a99-closed-loop/gold-current/registry.v1.json";
    private const string SnapshotRoot = "eval/a99-closed-loop/pdf-canonical-source-v1";
    private const string RootV1 = "artifacts/v5-p6t-function-membership/p6th3-conflict-adjudication";
    private const string RootV2 = "artifacts/v5-p6t-function-membership/p6th3-conflict-adjudication-v2";
    private static readonly (string Id, string Pdf, string Pack, string Anchor, string Left, string Right)[] Cases =
    [
        ("SRC-089", SourcePdfCorpus.Src089, "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_001", "O17", "O18", "O19"),
        ("SRC-041", SourcePdfCorpus.Src041, "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_060", "O4", "O4", "O5"),
    ];

    [Fact]
    public void P6TH3_conflict_adjudication_matches_both_authority_complete_gold_edges()
    {
        using var registry = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(RegistryPath)));
        var rows = new List<object>();
        var exactResults = new List<bool>();
        using var rejectionReceipt = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{RootV1}/client-preflight-rejection-receipt.v1.json")));
        Assert.Equal("LOCAL_REJECTION_BEFORE_NETWORK", rejectionReceipt.RootElement.GetProperty("status").GetString());
        Assert.Equal(0, rejectionReceipt.RootElement.GetProperty("externalProviderCalls").GetInt32());
        foreach (var spec in Cases)
        {
            var sourceSha = CanonicalSemanticSourceHash.Compute(TestRepository.Path(spec.Pdf));
            var plan = PdfCandidateAuthorityQualificationAdapter.PrepareFromSnapshot(
                TestRepository.Path($"{SnapshotRoot}/{sourceSha}.json"), spec.Id);
            var pack = plan.Packs.Single(value => value.PackId == spec.Pack);
            var f1 = PdfTotalOccurrenceRoleQualificationAdapter.PrepareFunctionMembershipF1(plan, pack,
                new Dictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>>(StringComparer.Ordinal));
            var aliasByOccurrence = f1.Request.Occurrences.ToDictionary(value => value.Id, value => value.Atom.Alias, StringComparer.Ordinal);
            var anchorAlias = aliasByOccurrence[spec.Anchor]; var leftAlias = aliasByOccurrence[spec.Left]; var rightAlias = aliasByOccurrence[spec.Right];

            var authority = registry.RootElement.GetProperty("authorities").EnumerateArray()
                .Single(item => item.GetProperty("authorityId").GetString() == spec.Id);
            Assert.Equal(sourceSha, authority.GetProperty("sourceSha256").GetString());
            var goldPath = authority.GetProperty("canonicalGoldPath").GetString()!;
            var goldText = File.ReadAllText(TestRepository.Path(goldPath));
            Assert.Equal(authority.GetProperty("goldSha256").GetString(), Hash(goldText));
            using var gold = JsonDocument.Parse(goldText);
            Assert.Equal(sourceSha, gold.RootElement.GetProperty("source").GetProperty("sourceSha256").GetString());
            var claims = gold.RootElement.GetProperty("semantic").GetProperty("claims").EnumerateArray()
                .Select(claim => claim.GetProperty("sourceParts").EnumerateArray()
                    .Select(part => part.GetProperty("sourceAlias").GetString()!).ToArray()).ToArray();
            var anchorClaim = Assert.Single(claims.Where(parts => parts.Contains(anchorAlias, StringComparer.Ordinal)));
            var leftClaim = Assert.Single(claims.Where(parts => parts.Contains(leftAlias, StringComparer.Ordinal)));
            var rightClaim = Assert.Single(claims.Where(parts => parts.Contains(rightAlias, StringComparer.Ordinal)));
            var expected = ReferenceEquals(leftClaim, rightClaim) ? "CONTINUES_LEFT_STRUCTURAL_UNIT" : "NEW_STRUCTURAL_UNIT";
            Assert.True(anchorClaim.SequenceEqual(leftClaim, StringComparer.Ordinal), $"anchor-left-not-same-gold-unit:{spec.Id}");

            using var raw = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{RootV2}/{spec.Id}.raw-capture.v1.json")));
            using var manifest = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{RootV2}/{spec.Id}.request-manifest.v1.json")));
            using var originalAttempt = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{RootV1}/{spec.Id}.raw-capture.v1.json")));
            var rawRoot = raw.RootElement;
            Assert.Equal("H3_SINGLE_CONFLICT_LEDGER_ACCEPTED", rawRoot.GetProperty("classification").GetString());
            Assert.Equal("stop", rawRoot.GetProperty("finishReason").GetString());
            Assert.Equal(0, rawRoot.GetProperty("retryCount").GetInt32());
            Assert.True(rawRoot.GetProperty("reasoningExecutionConfirmed").GetBoolean());
            Assert.Equal("PREPARED_NOT_AUTHORIZED", manifest.RootElement.GetProperty("status").GetString());
            Assert.Equal(rawRoot.GetProperty("requestHash").GetString(), manifest.RootElement.GetProperty("request").GetProperty("providerBodySha256").GetString());
            var observed = rawRoot.GetProperty("parsed").GetProperty("resolution").GetString()!;
            exactResults.Add(observed == expected);
            var row = new
            {
                documentId = spec.Id, sourceSha256 = sourceSha, goldSha256 = authority.GetProperty("goldSha256").GetString(),
                anchor = spec.Anchor, left = spec.Left, right = spec.Right,
                anchorAlias, leftAlias, rightAlias, expected, observed, exact = observed == expected,
                finishReason = rawRoot.GetProperty("finishReason").GetString(), retryCount = rawRoot.GetProperty("retryCount").GetInt32(),
                promptTokens = rawRoot.GetProperty("promptTokens").GetInt32(), completionTokens = rawRoot.GetProperty("completionTokens").GetInt32(),
                reasoningTokens = rawRoot.GetProperty("reasoningTokens").GetInt32(),
                requestSha256 = rawRoot.GetProperty("requestHash").GetString(),
                rawResponseSha256 = rawRoot.GetProperty("rawResponseSha256").GetString(),
                previousClientPreflightClassification = originalAttempt.RootElement.GetProperty("classification").GetString(),
            };
            rows.Add(row);
        }

        var exact = exactResults.Count(value => value);
        Assert.Equal(2, exact);
        FreezeArtifact.AssertJson(RootV2, "gold-audit.v1.json", new
        {
            schemaVersion = "v5-p6th3-conflict-adjudication-gold-audit-v1",
            status = "FROZEN_OFFLINE_AUDIT",
            method = "two predeclared opposite SRC-089/SRC-041 G2A-vs-H2 conflicts; exact edge truth derived from unchanged strict Gold sourceParts after raw capture freeze",
            treatment = new { changedDimension = "conflict-only adjudication pass", model = "qwen/qwen3.7-flash", reasoning = "enabled; effort omitted", maxNetworkProviderCalls = 2 },
            score = new { exact, total = rows.Count, qualificationScope = "CANARY_ONLY_NOT_CROSS_COHORT_QUALIFICATION" },
            providerCallsDuringAudit = 0, goldMutation = "NONE", runtimeChanged = false,
            rows,
            conclusion = "H3_MATCHED_OPPOSITE_CONFLICT_CANARY_2_OF_2; NOT_SUFFICIENT_FOR_RUNTIME_PROMOTION",
        });
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
