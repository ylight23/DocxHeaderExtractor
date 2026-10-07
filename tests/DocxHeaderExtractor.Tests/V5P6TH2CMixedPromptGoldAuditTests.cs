using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Scores the immutable H2C recovery set only as a mixed-prompt diagnostic.  It is deliberately
/// not a clean H2C qualification: 29 ledgers used the original prompt and two use the clarified
/// anchor-echo prompt after the original fixed example caused local quarantine.
/// </summary>
public sealed class V5P6TH2CMixedPromptGoldAuditTests
{
    private const string Root = "artifacts/v5-p6t-function-membership";
    private const string SnapshotRoot = "eval/a99-closed-loop/pdf-canonical-source-v1";
    private const string V1ManifestPath = Root + "/p6th2c-end-pointer-preflight/h2c-exact-end-pointer-preflight.v1.json";
    private const string ManifestPath = Root + "/p6th2c-end-pointer-preflight-v2/h2c-exact-end-pointer-preflight.v2.json";
    private const string PrimaryRoot = Root + "/p6th2c-end-pointer-capture-20261005";
    private const string ClarifiedRoot = Root + "/p6th2c-end-pointer-clarified-retry-20261005";
    private const string OutputRoot = Root + "/p6th2c-mixed-prompt-gold-audit";

    private static readonly Source[] Sources =
    [
        new("SRC-089", "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf", "p6tf1-preflight/retry-src089-result.v1.json", F1Shape.ResultRow, true),
        new("SRC-041", "todo10_8/heading_corpus_100/03_tai_chinh_ke_toan/041_IBRD_Financial_Statements_June_2025.pdf", "p6te-src041-e-challenge/f1.raw-capture.v1.json", F1Shape.RawCapture, false),
        new("SRC-095", "todo10_8/heading_corpus_100/07_system_generated/095_RFC9114_HTTP_3.pdf", "p6tf1-preflight/result.v1.json", F1Shape.ResultRows, true),
        new("DOC-0252", "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf", "p6te-doc0252-e-challenge/f1.raw-capture.v1.json", F1Shape.RawCapture, false),
        new("DOC-0256", "todo10_8/heading_corpus_100/05_bien_ban_hop/076_ICP_IACG08_Minutes_2023.pdf", "p6te-doc0256-e-challenge/f1.raw-capture.v1.json", F1Shape.RawCapture, false),
    ];

    [Fact]
    public void Immutable_H2C_recovery_set_is_scored_as_mixed_prompt_diagnostic_only()
    {
        using var v1ManifestDocument = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(V1ManifestPath)));
        using var manifestDocument = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(ManifestPath)));
        using var primaryResult = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{PrimaryRoot}/result.v1.json")));
        using var primaryFreeze = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{PrimaryRoot}/capture-freeze.v2.json")));
        using var clarifiedSummary = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{ClarifiedRoot}/retry-until-accepted-summary.v1.json")));

        Assert.Equal(31, primaryResult.RootElement.GetProperty("providerCalls").GetInt32());
        Assert.Equal(29, primaryResult.RootElement.GetProperty("acceptedLedgers").GetInt32());
        Assert.False(primaryResult.RootElement.GetProperty("goldRead").GetBoolean());
        Assert.Equal("RAW_CAPTURE_SET_HASH_VERIFIED_GOLD_CLOSED", primaryFreeze.RootElement.GetProperty("status").GetString());
        Assert.Equal(2, clarifiedSummary.RootElement.GetProperty("acceptedAnchorCount").GetInt32());

        var v1Manifest = v1ManifestDocument.RootElement.GetProperty("requestUniverse").EnumerateArray()
            .ToDictionary(Key, StringComparer.Ordinal);
        var manifest = manifestDocument.RootElement.GetProperty("requestUniverse").EnumerateArray()
            .ToDictionary(Key, StringComparer.Ordinal);
        Assert.Equal(31, v1Manifest.Count);
        Assert.Equal(31, manifest.Count);
        var aliases = Sources.ToDictionary(source => source.DocumentId, source => BuildOccurrenceAliases(source), StringComparer.Ordinal);
        var gold = Sources.ToDictionary(source => source.DocumentId, source => LoadGold(source.DocumentId), StringComparer.Ordinal);
        var clarifiedFiles = H2cCaptureArchive.List(ClarifiedRoot, "*.raw-capture.v2.json", SearchOption.AllDirectories);
        Assert.Equal(2, clarifiedFiles.Count);

        var rows = new List<Row>();
        foreach (var request in manifest.Values.OrderBy(value => value.GetProperty("DocumentId").GetString(), StringComparer.Ordinal)
                     .ThenBy(value => value.GetProperty("Anchor").GetString(), StringComparer.Ordinal))
        {
            var documentId = request.GetProperty("DocumentId").GetString()!;
            var packId = request.GetProperty("PackId").GetString()!;
            var anchor = request.GetProperty("Anchor").GetString()!;
            var issued = request.GetProperty("IssuedOccurrences").EnumerateArray().Select(value => value.GetString()!).ToArray();
            var selected = SelectContractValidCapture(documentId, anchor, issued, clarifiedFiles);
            var expectedRequest = selected.PromptVersion == "PRIMARY_PROMPT_V1" ? v1Manifest[Key(request)] : request;
            Assert.Equal(expectedRequest.GetProperty("ProviderBodySha256").GetString(), selected.ProviderBodySha256);
            Assert.Equal(Hash(selected.RawResponse), selected.RawResponseSha256);
            if (selected.RawSse is not null) Assert.Equal(Hash(selected.RawSse), selected.RawSseSha256);
            Assert.Equal("stop", selected.FinishReason);

            var predictedAliases = selected.HeadingMembers.Select(value => aliases[documentId][value]).ToArray();
            var anchorAlias = aliases[documentId][anchor];
            var matches = gold[documentId].Extents.Where(value => value[0] == anchorAlias).ToArray();
            Assert.True(matches.Length <= 1, $"duplicate Gold start for {documentId}/{anchor}");
            var outcome = matches.Length == 0 ? "FALSE_G2A_ANCHOR" : Classify(predictedAliases, matches[0]);
            rows.Add(new Row(documentId, packId, anchor, selected.PromptVersion, selected.RawCaptureSha256,
                selected.RawResponseSha256, selected.RawSseSha256, selected.SystemPromptSha256,
                selected.ProviderBodySha256, selected.FinishReason, selected.ReasoningTokens,
                selected.HeadingMembers.Count, outcome));
        }

        Assert.Equal(31, rows.Count);
        Assert.Equal(29, rows.Count(value => value.PromptVersion == "PRIMARY_PROMPT_V1"));
        Assert.Equal(2, rows.Count(value => value.PromptVersion == "CLARIFIED_PROMPT_V2_RECOVERY"));
        var trueAnchors = rows.Where(value => value.Outcome != "FALSE_G2A_ANCHOR").ToArray();
        Assert.Equal(27, trueAnchors.Length);
        Assert.Equal(4, rows.Count(value => value.Outcome == "FALSE_G2A_ANCHOR"));

        FreezeArtifact.AssertJson(OutputRoot, "h2c-mixed-prompt-end-pointer-gold-audit.v1.json", new
        {
            schemaVersion = "v5-p6th2c-mixed-prompt-end-pointer-gold-audit-v1",
            status = "MIXED_PROMPT_DIAGNOSTIC_NOT_CLEAN_CAUSAL_OR_POPULATION_QUALIFICATION",
            authority = new
            {
                v1RequestManifestSha256 = Hash(File.ReadAllText(TestRepository.Path(V1ManifestPath))),
                v2RequestManifestSha256 = Hash(File.ReadAllText(TestRepository.Path(ManifestPath))),
                primaryCaptureSetSha256 = primaryFreeze.RootElement.GetProperty("captureSetSha256").GetString(),
                clarifiedRetrySummarySha256 = Hash(File.ReadAllText(TestRepository.Path($"{ClarifiedRoot}/retry-until-accepted-summary.v1.json"))),
                goldAuthorities = Sources.Select(source => new
                {
                    documentId = source.DocumentId,
                    canonicalGoldPath = FrozenHistoryGold.Entry(source.DocumentId).CanonicalGoldPath,
                    goldSha256 = gold[source.DocumentId].GoldSha256,
                    sourceSha256 = gold[source.DocumentId].SourceSha256,
                }).ToArray(),
                providerCallsDuringAudit = 0,
                goldMutation = "NONE",
                rawCaptureMutation = "NONE",
                runtimeChanged = false,
            },
            captureComposition = new
            {
                totalContractValidLedgers = rows.Count,
                primaryPromptV1 = rows.Count(value => value.PromptVersion == "PRIMARY_PROMPT_V1"),
                clarifiedPromptV2Recovery = rows.Count(value => value.PromptVersion == "CLARIFIED_PROMPT_V2_RECOVERY"),
                primaryCalls = 31,
                priorExactBodyRetriesWithOldPrompt = 13,
                clarifiedRecoveryCalls = 2,
                totalHistoricalProviderCalls = 46,
                cleanQualificationRequired = "31_FROZEN_REQUESTS_USING_ONE_FIXED_PROMPT_WITH_NO_RETRIES",
            },
            extentDiagnostic = new
            {
                trueGoldAnchors = trueAnchors.Length,
                falseG2AAnchorsReportedSeparately = rows.Count(value => value.Outcome == "FALSE_G2A_ANCHOR"),
                exact = trueAnchors.Count(value => value.Outcome == "EXACT"),
                underextent = trueAnchors.Count(value => value.Outcome == "UNDEREXTENT"),
                overextent = trueAnchors.Count(value => value.Outcome == "OVEREXTENT"),
                wrongParts = trueAnchors.Count(value => value.Outcome == "WRONG_PARTS"),
                byPromptVersion = rows.GroupBy(value => value.PromptVersion).Select(group => new
                {
                    promptVersion = group.Key,
                    n = group.Count(),
                    trueGoldAnchors = group.Count(value => value.Outcome != "FALSE_G2A_ANCHOR"),
                    exact = group.Count(value => value.Outcome == "EXACT"),
                    underextent = group.Count(value => value.Outcome == "UNDEREXTENT"),
                    overextent = group.Count(value => value.Outcome == "OVEREXTENT"),
                    wrongParts = group.Count(value => value.Outcome == "WRONG_PARTS"),
                    falseG2AAnchors = group.Count(value => value.Outcome == "FALSE_G2A_ANCHOR"),
                }).OrderBy(value => value.promptVersion, StringComparer.Ordinal).ToArray(),
                rows,
            },
            interpretation = new
            {
                permitted = "ENGINEERING_RECOVERY_AND_SEMANTIC_DIAGNOSTIC_ONLY",
                prohibited = "CLEAN_H2C_CAUSAL_CLAIM_OR_POPULATION_QUALIFICATION",
                goldWasOpenedOnlyAfterRawHashFreeze = true,
                rawSourceDerivedResponsesIncluded = false,
            },
        });
    }

    private static IReadOnlyDictionary<string, string> BuildOccurrenceAliases(Source source)
    {
        var sourceHash = CanonicalSemanticSourceHash.Compute(TestRepository.Path(source.PdfPath));
        var plan = PdfCandidateAuthorityQualificationAdapter.PrepareFromSnapshot(
            TestRepository.Path($"{SnapshotRoot}/{sourceHash}.json"), source.DocumentId);
        using var capture = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{Root}/{source.F1Path}")));
        var row = source.F1Shape switch
        {
            F1Shape.RawCapture => capture.RootElement,
            F1Shape.ResultRow => capture.RootElement.GetProperty("row"),
            F1Shape.ResultRows => capture.RootElement.GetProperty("rows").EnumerateArray().Single(value => value.GetProperty("documentId").GetString() == source.DocumentId),
            _ => throw new InvalidDataException("unknown F1 shape"),
        };
        var pack = plan.Packs.Single(value => value.PackId == row.GetProperty("packId").GetString());
        var prepared = PdfTotalOccurrenceRoleQualificationAdapter.PrepareFunctionMembershipF1(
            plan, pack, source.F1UsesCorrespondence ? Correspondences(pack) : new Dictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>>(StringComparer.Ordinal));
        Assert.Equal(96, prepared.Request.Occurrences.Count);
        return prepared.Request.Occurrences.ToDictionary(value => value.Id, value => value.Atom.Alias, StringComparer.Ordinal);
    }

    private static SelectedCapture SelectContractValidCapture(string documentId, string anchor, IReadOnlyList<string> issued, IReadOnlyList<string> clarifiedFiles)
    {
        var primary = ReadCapture(PrimaryRoot, $"{documentId}_{anchor}.raw-capture.v1.json", "PRIMARY_PROMPT_V1");
        if (TryParseLedger(primary.RawResponse, anchor, issued, out var primaryMembers))
            return primary with { HeadingMembers = primaryMembers };

        var matches = clarifiedFiles.Where(path =>
        {
            using var capture = H2cCaptureArchive.Read(ClarifiedRoot, path);
            var root = capture.Root;
            return root.GetProperty("documentId").GetString() == documentId && root.GetProperty("anchor").GetString() == anchor;
        }).ToArray();
        var path = Assert.Single(matches);
        var recovery = ReadCapture(ClarifiedRoot, path, "CLARIFIED_PROMPT_V2_RECOVERY");
        Assert.True(TryParseLedger(recovery.RawResponse, anchor, issued, out var members), $"clarified recovery invalid: {documentId}/{anchor}");
        return recovery with { HeadingMembers = members };
    }

    private static SelectedCapture ReadCapture(string captureRoot, string relativePath, string promptVersion)
    {
        using var capture = H2cCaptureArchive.Read(captureRoot, relativePath);
        var root = capture.Root;
        return new SelectedCapture(
            promptVersion,
            capture.FileSha256,
            root.GetProperty("rawResponse").GetString()!,
            root.GetProperty("rawResponseSha256").GetString()!,
            root.TryGetProperty("rawSse", out var sse) ? sse.GetString() : null,
            root.GetProperty("rawSseSha256").GetString()!,
            root.GetProperty("systemPromptSha256").GetString()!,
            root.GetProperty("providerBodySha256").GetString()!,
            root.GetProperty("finishReason").GetString()!,
            root.TryGetProperty("reasoningTokens", out var tokens) && tokens.ValueKind == JsonValueKind.Number ? tokens.GetInt32() : null,
            []);
    }

    private static bool TryParseLedger(string raw, string anchor, IReadOnlyList<string> issued, out IReadOnlyList<string> members)
    {
        members = [];
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 || !root.TryGetProperty("decisions", out var decisions) || decisions.GetArrayLength() != 1) return false;
            var decision = decisions[0];
            if (decision.ValueKind != JsonValueKind.Object || decision.EnumerateObject().Count() != 5 || decision.GetProperty("anchor").GetString() != anchor) return false;
            var values = decision.GetProperty("headingMembers").EnumerateArray().Select(value => value.GetString()!).ToArray();
            if (values.Length == 0 || !values.SequenceEqual(issued.Take(values.Length), StringComparer.Ordinal) || decision.GetProperty("endOccurrence").GetString() != values[^1]) return false;
            var outside = decision.GetProperty("firstOutsideOccurrence");
            var valid = values.Length == issued.Count
                ? outside.ValueKind == JsonValueKind.Null && decision.GetProperty("firstOutsideRole").GetString() == "NO_VISIBLE_SUCCESSOR"
                : outside.GetString() == issued[values.Length] && decision.GetProperty("firstOutsideRole").GetString() is "NEW_HEADING" or "BODY_CONTENT" or "PAGE_FURNITURE" or "TABLE_OR_STRUCTURED_CONTENT" or "OTHER_NON_HEADING";
            if (!valid) return false;
            members = values;
            return true;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException) { return false; }
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>> Correspondences(PdfCandidateAuthorityPreparedPack pack)
    {
        var owned = pack.OwnedAliases.ToHashSet(StringComparer.Ordinal);
        var candidates = pack.Universe.Candidates.ToDictionary(value => value.Id, StringComparer.Ordinal);
        var result = new Dictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>>(StringComparer.Ordinal);
        foreach (var relation in pack.Universe.Relations)
        {
            if (!candidates.TryGetValue(relation.CandidateId, out var candidate) || !owned.Contains(candidate.Endpoint.Parts[0].Alias)) continue;
            var values = result.TryGetValue(candidate.Endpoint.Parts[0].Alias, out var old) ? old.ToList() : [];
            if (!values.Any(value => value.TargetPage == relation.TargetPage && value.TargetText == relation.TargetText)) values.Add(new(relation.TargetPage, relation.TargetText));
            result[candidate.Endpoint.Parts[0].Alias] = values;
        }
        return result;
    }

    private static GoldDocument LoadGold(string documentId)
    {
        var entry = FrozenHistoryGold.Entry(documentId);
        FrozenHistoryGold.RequireCapability(documentId, GoldCapability.Occurrence);
        using var document = FrozenHistoryGold.Resolve(documentId);
        Assert.Equal(entry.SourceSha256, document.RootElement.GetProperty("source").GetProperty("sourceSha256").GetString());
        var extents = document.RootElement.GetProperty("semantic").GetProperty("claims").EnumerateArray().Select(claim =>
            claim.GetProperty("sourceParts").EnumerateArray().Select(part => part.GetProperty("sourceAlias").GetString()!).ToArray()).ToArray();
        return new(entry.GoldSha256, entry.SourceSha256, extents);
    }

    private static string Classify(IReadOnlyList<string> predicted, IReadOnlyList<string> expected)
    {
        if (predicted.SequenceEqual(expected, StringComparer.Ordinal)) return "EXACT";
        if (predicted.Count < expected.Count && predicted.SequenceEqual(expected.Take(predicted.Count), StringComparer.Ordinal)) return "UNDEREXTENT";
        if (predicted.Count > expected.Count && expected.SequenceEqual(predicted.Take(expected.Count), StringComparer.Ordinal)) return "OVEREXTENT";
        return "WRONG_PARTS";
    }

    private static string Key(JsonElement row) => $"{row.GetProperty("DocumentId").GetString()}|{row.GetProperty("PackId").GetString()}|{row.GetProperty("Anchor").GetString()}";
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private enum F1Shape { RawCapture, ResultRow, ResultRows }
    private sealed record Source(string DocumentId, string PdfPath, string F1Path, F1Shape F1Shape, bool F1UsesCorrespondence);
    private sealed record GoldDocument(string GoldSha256, string SourceSha256, string[][] Extents);
    private sealed record SelectedCapture(string PromptVersion, string RawCaptureSha256, string RawResponse, string RawResponseSha256,
        string? RawSse, string RawSseSha256, string SystemPromptSha256, string ProviderBodySha256, string FinishReason,
        int? ReasoningTokens, IReadOnlyList<string> HeadingMembers);
    private sealed record Row(string DocumentId, string PackId, string Anchor, string PromptVersion, string RawCaptureSha256,
        string RawResponseSha256, string RawSseSha256, string SystemPromptSha256, string ProviderBodySha256, string FinishReason,
        int? ReasoningTokens, int HeadingMemberCount, string Outcome);
}
