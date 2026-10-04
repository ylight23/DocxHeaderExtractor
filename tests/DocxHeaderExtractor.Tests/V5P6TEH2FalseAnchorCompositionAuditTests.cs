using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>Checks whether a frozen G2A anchor may safely hard-stop an H2 continuation.</summary>
public sealed class V5P6TEH2FalseAnchorCompositionAuditTests
{
    private const string GoldRegistry = "eval/a99-closed-loop/gold-current/registry.v1.json";
    private const string SourcePdf = "todo10_8/heading_corpus_100/03_tai_chinh_ke_toan/041_IBRD_Financial_Statements_June_2025.pdf";
    private const string GoldId = "SRC-041";
    private const string Root = "artifacts/v5-p6t-function-membership/p6te-src041-h2-challenge";
    private const string ERoot = "artifacts/v5-p6t-function-membership/p6te-src041-e-challenge";

    [Fact]
    public void P6TE_H2_continuation_crosses_a_distinct_G2A_anchor_inside_frozen_Gold_multipart()
    {
        using var registry = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(GoldRegistry)));
        var authority = registry.RootElement.GetProperty("authorities").EnumerateArray()
            .Single(item => item.GetProperty("authorityId").GetString() == GoldId);
        var goldSha = authority.GetProperty("goldSha256").GetString()!;
        var sourceSha = CanonicalSemanticSourceHash.Compute(TestRepository.Path(SourcePdf));
        var goldPath = authority.GetProperty("canonicalGoldPath").GetString()!;
        using var gold = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(goldPath)));
        Assert.Equal(goldSha, Hash(File.ReadAllText(TestRepository.Path(goldPath))));
        Assert.Equal(sourceSha, gold.RootElement.GetProperty("source").GetProperty("sourceSha256").GetString());

        var goldParts = gold.RootElement.GetProperty("semantic").GetProperty("claims").EnumerateArray()
            .Select(claim => claim.GetProperty("sourceParts").EnumerateArray()
                .Select(part => part.GetProperty("sourceAlias").GetString()!).ToArray())
            .Single(parts => parts.SequenceEqual(new[] { "L3526:S0", "L3527:S0" }, StringComparer.Ordinal));

        using var f1 = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{ERoot}/f1.raw-capture.v1.json")));
        using var g2a = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{ERoot}/g2a.raw-capture.v1.json")));
        using var h2 = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{Root}/h2.raw-capture.v1.json")));
        using var h2Manifest = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{Root}/h2-request-manifest.v1.json")));
        Assert.Equal("TOTAL_F1_LEDGER_ACCEPTED", f1.RootElement.GetProperty("classification").GetString());
        Assert.Equal("G2A_TOTAL_LEDGER_ACCEPTED", g2a.RootElement.GetProperty("classification").GetString());
        Assert.Equal("H2_TOTAL_EDGE_LEDGER_ACCEPTED", h2.RootElement.GetProperty("classification").GetString());
        Assert.Equal("stop", h2.RootElement.GetProperty("finishReason").GetString());
        Assert.Equal(1, h2.RootElement.GetProperty("providerCalls").GetInt32());
        Assert.Equal(0, h2.RootElement.GetProperty("retryCount").GetInt32());
        Assert.Equal("PREPARED_NOT_AUTHORIZED", h2Manifest.RootElement.GetProperty("status").GetString());
        Assert.Equal(h2.RootElement.GetProperty("requestHash").GetString(),
            h2Manifest.RootElement.GetProperty("request").GetProperty("providerBodySha256").GetString());

        var g2Decisions = g2a.RootElement.GetProperty("parsed").GetProperty("decisions").EnumerateArray().ToArray();
        var leftAnchor = g2Decisions.Single(item => item.GetProperty("primary").GetString() == "O4")
            .GetProperty("anchor").GetString();
        var rightAnchor = g2Decisions.Single(item => item.GetProperty("primary").GetString() == "O5")
            .GetProperty("anchor").GetString();
        Assert.Equal("HAS_STRUCTURAL_EXTENT", leftAnchor);
        Assert.Equal("HAS_STRUCTURAL_EXTENT", rightAnchor);

        var h2Edges = h2.RootElement.GetProperty("parsed").GetProperty("decisions").EnumerateArray().ToArray();
        var crossing = h2Edges.Single(item => item.GetProperty("Left").GetString() == "O4" && item.GetProperty("Right").GetString() == "O5");
        Assert.Equal("CONTINUES_STRUCTURAL_UNIT", crossing.GetProperty("Boundary").GetString());

        // Demonstrate the exact effect of the proposed hard invariant without applying it to runtime:
        // it would replace the provider's continuation with STOP before a distinct G2A anchor.
        var forcedStopAliases = new[] { "L3526:S0" };
        var hardRuleWouldMatchGold = forcedStopAliases.SequenceEqual(goldParts, StringComparer.Ordinal);
        Assert.False(hardRuleWouldMatchGold);

        FreezeArtifact.AssertJson(Root, "h2-e-composition-audit.v1.json", new
        {
            schemaVersion = "v5-p6te-src041-h2-e-composition-audit-v1",
            status = "FROZEN_PROVIDER_FREE_AUDIT",
            authority = new
            {
                sourceSha256 = sourceSha,
                goldSha256 = goldSha,
                goldMultipartParts = goldParts,
                primaryOccurrence = "O4",
                continuationOccurrence = "O5",
                g2aRawResponseSha256 = g2a.RootElement.GetProperty("rawResponseSha256").GetString(),
                h2RawResponseSha256 = h2.RootElement.GetProperty("rawResponseSha256").GetString(),
                requestManifestSha256 = Hash(File.ReadAllText(TestRepository.Path($"{Root}/h2-request-manifest.v1.json"))),
            },
            observed = new
            {
                g2aPrimary = leftAnchor,
                g2aContinuation = rightAnchor,
                h2Boundary = crossing.GetProperty("Boundary").GetString(),
                finishReason = h2.RootElement.GetProperty("finishReason").GetString(),
                retries = h2.RootElement.GetProperty("retryCount").GetInt32(),
            },
            hypotheticalHardRule = new
            {
                rule = "NO_CROSS_DISTINCT_FROZEN_G2A_ANCHOR",
                effect = "OVERRIDES_H2_CONTINUATION_TO_STOP_BEFORE_O5",
                reconstructedAliases = forcedStopAliases,
                exactGoldMatch = hardRuleWouldMatchGold,
                verdict = "WOULD_CREATE_UNDEREXTENT",
            },
            providerCallsDuringAudit = 0,
            goldMutation = "NONE",
            rawCaptureMutation = "NONE",
            runtimeChanged = false,
            conclusion = "GLOBAL_NO_CROSS_ANCHOR_HARD_INVARIANT_REJECTED_BY_AUTHORITY_COMPLETE_SRC041_E_CASE; H2_CONTINUATION_AND_G2A_ANCHOR_CONFLICT_MUST_REMAIN_EXPLICIT_OR_UNRESOLVED",
        });
    }

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
