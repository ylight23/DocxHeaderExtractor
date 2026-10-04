using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>P6T-A freezes the total role pass only. Pass two extent selection remains deliberately blocked.</summary>
public sealed class V5P6TATotalOccurrenceRoleContractTests
{
    private const string SnapshotRoot = "eval/a99-closed-loop/pdf-canonical-source-v1";
    private const string OutputRoot = "artifacts/v5-p6t-total-occurrence-role";

    [Fact]
    public void P6TA_total_role_contract_is_exactly_complete_and_two_pack_canary_is_prepared_not_authorized()
    {
        var plans = new[] { ("SRC-089", SourcePdfCorpus.Src089), ("SRC-095", SourcePdfCorpus.Src095) }.ToDictionary(value => value.Item1, value =>
        {
            var sha = CanonicalSemanticSourceHash.Compute(TestRepository.Path(value.Item2));
            return PdfCandidateAuthorityQualificationAdapter.PrepareFromSnapshot(TestRepository.Path($"{SnapshotRoot}/{sha}.json"), value.Item1);
        }, StringComparer.Ordinal);
        var prepared = plans.Select(pair => PdfTotalOccurrenceRoleQualificationAdapter.Prepare(pair.Value,
            pair.Value.Packs.Single(pack => pack.PackId.EndsWith("PACK_001", StringComparison.Ordinal)))).OrderBy(value => value.SourcePack.DocumentId, StringComparer.Ordinal).ToArray();
        Assert.Equal(2, prepared.Length);
        foreach (var item in prepared)
        {
            Assert.Equal(96, item.Request.Occurrences.Count);
            Assert.Equal(item.SourcePack.OwnedAliases, item.Request.Occurrences.Select(value => value.Atom.Alias));
            Assert.Equal(Enumerable.Range(1, 96).Select(value => $"O{value}"), item.Request.Occurrences.Select(value => value.Id));
            Assert.DoesNotContain("candidate", item.Request.UserMessage, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sourceAlias", item.Request.UserMessage, StringComparison.Ordinal);
            using var body = JsonDocument.Parse(item.ProviderBody);
            Assert.True(body.RootElement.GetProperty("reasoning").GetProperty("enabled").GetBoolean());
            Assert.False(body.RootElement.GetProperty("reasoning").TryGetProperty("effort", out _));
            Assert.Equal("json_object", body.RootElement.GetProperty("response_format").GetProperty("type").GetString());
            var valid = JsonSerializer.Serialize(new { decisions = item.Request.Occurrences.Select(value => new { occurrence = value.Id, role = "OTHER" }).ToArray() });
            Assert.True(Encoding.UTF8.GetByteCount(valid) < PdfCandidateAuthorityQualificationAdapter.ResponseUtf8ByteCap);
            var parsed = PdfTotalOccurrenceRoleQualificationAdapter.Parse(item, valid);
            Assert.Equal(96, parsed.Decisions.Count); Assert.All(parsed.Decisions, value => Assert.Equal(V5OccurrenceRoleV1.OTHER, value.Role));
            Assert.Throws<InvalidOperationException>(() => PdfTotalOccurrenceRoleQualificationAdapter.Parse(item,
                JsonSerializer.Serialize(new { decisions = item.Request.Occurrences.Skip(1).Select(value => new { occurrence = value.Id, role = "OTHER" }).ToArray() })));
        }
        var output = new
        {
            schemaVersion = "v5-p6ta-total-occurrence-role-manifest-v1", status = "PREPARED_NOT_AUTHORIZED",
            providerCalls = 0, goldRead = false, goldMutation = "NONE", sharedRuntime = "UNCHANGED",
            treatment = new { model = "qwen/qwen3.7-flash", provider = "alibaba", temperature = 0, reasoning = new { enabled = true }, effort = "OMITTED", responseFormat = "json_object" },
            contract = new { pass1 = "TOTAL_ROLE", expectedDecisions = "exactly one O# decision per owned occurrence", roles = new[] { "HEADING", "REPRESENTATION", "OTHER" }, extentOrLocatorOutput = "FORBIDDEN", pass2 = "BLOCKED_UNTIL_A_VALID_P6TA_PASS1_RESPONSE_EXISTS" },
            execution = new { maximumFuturePrimaryProviderCalls = 2, retry = "NOT_AUTHORIZED", repair = false, fallback = false, goldDuringRun = false },
            knownRisk = new { P5K = "Prior exhaustive ledger experiments were unreliable. P6T-A is smaller role-only JSON but still requires totality, so no reliability inference is permitted before the two-pack canary." },
            rows = prepared.Select(item => new { item.SourcePack.DocumentId, item.SourcePack.PackId, ownedOccurrences = item.Request.Occurrences.Count,
                ownedAliases = item.SourcePack.OwnedAliases, roleRequestHash = item.Request.UserMessageSha256, systemPromptSha256 = Hash(item.Request.SystemPrompt),
                providerRequestHash = item.ProviderRequestHash, item.ProviderRequestBytes, item.SourcePack.MaxCompletionTokens,
                candidateUniverseFingerprint = item.SourcePack.Universe.Fingerprint, sourceSha256 = plans[item.SourcePack.DocumentId].SourceSha256 }).ToArray(),
        };
        FreezeArtifact.AssertJson(OutputRoot, "two-pack-total-role-manifest.v1.json", output);
    }

    private static string Hash(string value) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
