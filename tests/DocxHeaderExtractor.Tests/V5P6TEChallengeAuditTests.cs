using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>Provider-free audit joining frozen F1/G2A captures to strict Gold and source hashes.</summary>
public sealed class V5P6TEChallengeAuditTests
{
    private const string RegistryPath = "eval/a99-closed-loop/gold-current/registry.v1.json";
    private const string ArtifactRoot = "artifacts/v5-p6t-function-membership/p6te-e-challenge-audit";
    private static readonly (string Id, string Root)[] Cases =
    [
        ("DOC-0256", "artifacts/v5-p6t-function-membership/p6te-doc0256-e-challenge"),
        ("DOC-0252", "artifacts/v5-p6t-function-membership/p6te-doc0252-e-challenge"),
        ("SRC-041", "artifacts/v5-p6t-function-membership/p6te-src041-e-challenge"),
    ];

    [Fact]
    public void P6TE_false_anchor_inside_true_multipart_is_audited_against_frozen_authorities()
    {
        using var registry = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(RegistryPath)));
        var rows = new List<object>();
        var confirmed = 0;
        foreach (var spec in Cases)
        {
            var root = spec.Root;
            using var manifest = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{root}/f1-request-manifest.v1.json")));
            using var f1 = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{root}/f1.raw-capture.v1.json")));
            using var g2Manifest = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{root}/g2a-request-manifest.v1.json")));
            using var g2 = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{root}/g2a.raw-capture.v1.json")));
            using var receipt = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{root}/execution-receipt.v1.json")));

            var goldSha = manifest.RootElement.GetProperty("challenge").GetProperty("goldSha256").GetString()!;
            var sourceSha = manifest.RootElement.GetProperty("challenge").GetProperty("sourceSha256").GetString()!;
            var targetAlias = manifest.RootElement.GetProperty("challenge").GetProperty("continuationAlias").GetString()!;
            var targetOccurrence = manifest.RootElement.GetProperty("challenge").GetProperty("continuationOccurrence").GetString()!;
            var registryAuthority = registry.RootElement.GetProperty("authorities").EnumerateArray()
                .Single(item => item.GetProperty("authorityId").GetString() == spec.Id);
            Assert.Equal(goldSha, registryAuthority.GetProperty("goldSha256").GetString());
            Assert.True(registryAuthority.GetProperty("occurrenceEvaluable").GetBoolean());
            var goldPath = registryAuthority.GetProperty("canonicalGoldPath").GetString()!;
            using var gold = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(goldPath)));
            Assert.Equal(sourceSha, gold.RootElement.GetProperty("source").GetProperty("sourceSha256").GetString());

            var matchingClaims = gold.RootElement.GetProperty("semantic").GetProperty("claims").EnumerateArray()
                .Select(claim => claim.GetProperty("sourceParts").EnumerateArray().Select(part => part.GetProperty("sourceAlias").GetString()!).ToArray())
                .Where(parts => parts.Contains(targetAlias, StringComparer.Ordinal)).ToArray();
            var goldClaim = Assert.Single(matchingClaims);
            var partIndex = Array.IndexOf(goldClaim, targetAlias);
            Assert.True(goldClaim.Length > 1);
            Assert.True(partIndex > 0, $"challenge alias must be a non-initial Gold part:{spec.Id}:{targetAlias}");

            var f1Row = f1.RootElement;
            Assert.Equal("TOTAL_F1_LEDGER_ACCEPTED", f1Row.GetProperty("classification").GetString());
            Assert.Equal("stop", f1Row.GetProperty("finishReason").GetString());
            Assert.Equal(0, f1Row.GetProperty("retryCount").GetInt32());
            Assert.Equal(96, f1Row.GetProperty("issuedOccurrences").GetInt32());
            Assert.Equal(Hash(File.ReadAllText(TestRepository.Path($"{root}/f1.raw-capture.v1.json"))), receipt.RootElement.GetProperty("f1RawSha256").GetString());
            var functionRow = f1Row.GetProperty("parsed").GetProperty("decisions").EnumerateArray()
                .Single(item => item.GetProperty("occurrence").GetString() == targetOccurrence);
            Assert.Equal("ESTABLISHES_STRUCTURE", functionRow.GetProperty("function").GetString());

            var g2Row = g2.RootElement;
            Assert.Equal("G2A_TOTAL_LEDGER_ACCEPTED", g2Row.GetProperty("classification").GetString());
            Assert.Equal("stop", g2Row.GetProperty("finishReason").GetString());
            Assert.Equal(0, g2Row.GetProperty("retryCount").GetInt32());
            Assert.Equal(Hash(File.ReadAllText(TestRepository.Path($"{root}/g2a.raw-capture.v1.json"))), receipt.RootElement.GetProperty("g2aRawSha256").GetString());
            Assert.True(g2Manifest.RootElement.GetProperty("targetIncludedByF1Function").GetBoolean());
            var anchorRow = g2Row.GetProperty("parsed").GetProperty("decisions").EnumerateArray()
                .Single(item => item.GetProperty("alias").GetString() == targetAlias);
            var anchor = anchorRow.GetProperty("anchor").GetString()!;
            var outcome = partIndex > 0 && functionRow.GetProperty("function").GetString() == "ESTABLISHES_STRUCTURE" && anchor == "HAS_STRUCTURAL_EXTENT"
                ? "E_FALSE_ANCHOR_INSIDE_TRUE_MULTIPART_CONFIRMED" : "E_SHAPE_NOT_OBSERVED_FOR_THIS_CONTROL";
            if (outcome == "E_FALSE_ANCHOR_INSIDE_TRUE_MULTIPART_CONFIRMED") confirmed++;

            rows.Add(new
            {
                documentId = spec.Id,
                goldSha256 = goldSha,
                sourceSha256 = sourceSha,
                targetAlias,
                targetOccurrence,
                goldMultipartParts = goldClaim,
                goldPartIndex = partIndex,
                function = functionRow.GetProperty("function").GetString(),
                anchor,
                g2aClassification = g2Row.GetProperty("classification").GetString(),
                f1RawSha256 = receipt.RootElement.GetProperty("f1RawSha256").GetString(),
                g2aRawSha256 = receipt.RootElement.GetProperty("g2aRawSha256").GetString(),
                outcome,
            });
        }

        Assert.Equal(1, confirmed);
        FreezeArtifact.AssertJson(ArtifactRoot, "false-anchor-inside-multipart-audit.v1.json", new
        {
            schemaVersion = "v5-p6te-cross-document-false-anchor-audit-v1",
            status = "FROZEN_PROVIDER_FREE_AUDIT",
            method = "frozen strict Gold continuation alias + source hash + immutable F1/G2A raw decisions; no historical candidate substitutions",
            providerCallsDuringAudit = 0,
            rawCaptureMutation = "NONE",
            goldMutation = "NONE",
            runtimeChanged = false,
            totalAuditedCases = rows.Count,
            falseAnchorInsideTrueMultipart = confirmed,
            verdict = "E_CHALLENGE_AUTHORITY_COMPLETE_ON_SRC041; DOC0252_AND_DOC0256_ARE_NEGATIVE_CONTROLS",
            rows,
        });
    }

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
