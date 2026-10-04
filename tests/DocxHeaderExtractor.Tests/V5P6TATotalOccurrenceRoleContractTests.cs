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
            Assert.Contains("OTHER may be a continuation", item.Request.SystemPrompt, StringComparison.Ordinal);
            using var body = JsonDocument.Parse(item.ProviderBody);
            Assert.True(body.RootElement.GetProperty("reasoning").GetProperty("enabled").GetBoolean());
            Assert.False(body.RootElement.GetProperty("reasoning").TryGetProperty("effort", out _));
            Assert.Equal("json_object", body.RootElement.GetProperty("response_format").GetProperty("type").GetString());
            var valid = JsonSerializer.Serialize(new { decisions = item.Request.Occurrences.Select(value => new { occurrence = value.Id, role = "OTHER" }).ToArray() });
            Assert.True(Encoding.UTF8.GetByteCount(valid) < PdfCandidateAuthorityQualificationAdapter.ResponseUtf8ByteCap);
            var parsed = PdfTotalOccurrenceRoleQualificationAdapter.Parse(item, valid);
            Assert.Equal(96, parsed.Decisions.Count); Assert.All(parsed.Decisions, value => Assert.Equal(V5OccurrenceRoleV1.OTHER, value.Role));
            AssertReject(item, JsonSerializer.Serialize(new { decisions = item.Request.Occurrences.Skip(1).Select(value => new { occurrence = value.Id, role = "OTHER" }).ToArray() }),
                "total-role-decision-cardinality-invalid");
            AssertReject(item, Ledger(item, values => values.Select((value, index) => new { occurrence = index == 95 ? values[0].Id : value.Id, role = "OTHER" })),
                "total-role-occurrence-duplicate");
            AssertReject(item, Ledger(item, values => values.Select((value, index) => new { occurrence = index == 0 ? "O999" : value.Id, role = "OTHER" })),
                "total-role-occurrence-not-issued");
            AssertReject(item, Ledger(item, values => values.Select((value, index) => new { occurrence = value.Id, role = index == 0 ? "HEADING" : "OTHER" })),
                "total-role-not-in-enum");
            AssertReject(item, Ledger(item, values => values.Select((value, index) => new { occurrence = value.Id, role = index == 0 ? "REPRESENTATION" : "OTHER" })),
                "total-role-not-in-enum");
            AssertReject(item, Ledger(item, values => values.Select((value, index) => new { occurrence = value.Id, role = index == 0 ? "heading_start" : "OTHER" })),
                "total-role-not-in-enum");
            AssertReject(item, JsonSerializer.Serialize(new { decisions = item.Request.Occurrences.Select(value => new { occurrence = value.Id, role = "OTHER" })
                .Append(new { occurrence = "O1", role = "OTHER" }).ToArray() }), "total-role-decision-cardinality-invalid");
            AssertReject(item, JsonSerializer.Serialize(new { decisions = item.Request.Occurrences.Select((value, index) => index == 0
                ? new Dictionary<string, object> { ["occurrence"] = value.Id, ["role"] = "OTHER", ["unexpected"] = true }
                : new Dictionary<string, object> { ["occurrence"] = value.Id, ["role"] = "OTHER" }).ToArray() }), "total-role-decision-schema-invalid");
            AssertReject(item, JsonSerializer.Serialize(new { decisions = item.Request.Occurrences.Select(value => new { occurrence = value.Id, role = "OTHER" }).ToArray(), unexpected = true }),
                "total-role-root-invalid");
            if (item.SourcePack.DocumentId == "SRC-089")
            {
                var first = Assert.Single(item.Request.Occurrences.Where(value => value.Atom.Alias == "L0006:S0"));
                var continuation1 = Assert.Single(item.Request.Occurrences.Where(value => value.Atom.Alias == "L0007:S0"));
                var continuation2 = Assert.Single(item.Request.Occurrences.Where(value => value.Atom.Alias == "L0008:S0"));
                var multipart = item.SourcePack.Universe.Candidates.Where(value =>
                    value.Endpoint.Parts.Select(part => part.Alias).SequenceEqual(new[] { "L0006:S0", "L0007:S0", "L0008:S0" })).ToArray();
                Assert.Equal(2, multipart.Length); // Whole and strict-final-part alternatives share the same start domain.
                Assert.All(multipart, value => Assert.Equal(first.Atom.Alias, value.Endpoint.Parts[0].Alias));
                var anchored = JsonSerializer.Serialize(new { decisions = item.Request.Occurrences.Select(value => new
                {
                    occurrence = value.Id,
                    role = value.Id == first.Id ? "HEADING_START" : "OTHER",
                }).ToArray() });
                var anchorParsed = PdfTotalOccurrenceRoleQualificationAdapter.Parse(item, anchored);
                Assert.Equal(V5OccurrenceRoleV1.HEADING_START, anchorParsed.Decisions.Single(value => value.OccurrenceId == first.Id).Role);
                Assert.Equal(V5OccurrenceRoleV1.OTHER, anchorParsed.Decisions.Single(value => value.OccurrenceId == continuation1.Id).Role);
                Assert.Equal(V5OccurrenceRoleV1.OTHER, anchorParsed.Decisions.Single(value => value.OccurrenceId == continuation2.Id).Role);
            }
        }
        var output = new
        {
            schemaVersion = "v5-p6ta-total-occurrence-anchor-role-manifest-v1", status = "PREPARED_NOT_AUTHORIZED",
            providerCalls = 0, goldRead = false, goldMutation = "NONE", sharedRuntime = "UNCHANGED",
            treatment = new { model = "qwen/qwen3.7-flash", provider = "alibaba", temperature = 0, reasoning = new { enabled = true }, effort = "OMITTED", responseFormat = "json_object" },
            contract = new { semanticUnit = "SOURCE_ATOM_START", pass1 = "TOTAL_ANCHOR_ROLE", expectedDecisions = "exactly one O# decision per owned occurrence", roles = new[] { "HEADING_START", "REPRESENTATION_START", "OTHER" }, other = "may be a continuation of a semantic unit started earlier; never means non-membership", extentOrLocatorOutput = "FORBIDDEN", pass2 = new { status = "BLOCKED_UNTIL_A_VALID_P6TA_PASS1_RESPONSE_EXISTS", eligibleRole = "HEADING_START", candidateRule = "every selected C# must begin at the selected O# and may consume only subsequent owned atoms" } },
            execution = new { maximumFuturePrimaryProviderCalls = 2, retry = "NOT_AUTHORIZED", repair = false, fallback = false, goldDuringRun = false },
            knownRisk = new { P5K = "Prior exhaustive ledger experiments were unreliable. P6T-A is smaller role-only JSON but still requires totality, so no reliability inference is permitted before the two-pack canary." },
            rows = prepared.Select(item => new { item.SourcePack.DocumentId, item.SourcePack.PackId, ownedOccurrences = item.Request.Occurrences.Count,
                ownedAliases = item.SourcePack.OwnedAliases, roleRequestHash = item.Request.UserMessageSha256, systemPromptSha256 = Hash(item.Request.SystemPrompt),
                providerRequestHash = item.ProviderRequestHash, item.ProviderRequestBytes, item.SourcePack.MaxCompletionTokens,
                candidateUniverseFingerprint = item.SourcePack.Universe.Fingerprint, sourceSha256 = plans[item.SourcePack.DocumentId].SourceSha256 }).ToArray(),
        };
        FreezeArtifact.AssertJson(OutputRoot, "two-pack-total-role-manifest.v1.json", output);
    }

    private static string Ledger(PdfTotalRolePreparedPack item, Func<IReadOnlyList<V5IssuedOccurrenceV1>, IEnumerable<object>> decisions) =>
        JsonSerializer.Serialize(new { decisions = decisions(item.Request.Occurrences).ToArray() });

    private static void AssertReject(PdfTotalRolePreparedPack item, string response, string expectedReason)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => PdfTotalOccurrenceRoleQualificationAdapter.Parse(item, response));
        Assert.Equal(expectedReason, exception.Message);
    }

    private static string Hash(string value) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
