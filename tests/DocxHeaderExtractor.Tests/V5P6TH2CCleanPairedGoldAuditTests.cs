using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.V5Qualification;

namespace DocxHeaderExtractor.Tests;

/// <summary>Paired provider-free Gold scoring for the immutable clean V1 and V2 H2-C captures.</summary>
public sealed class V5P6TH2CCleanPairedGoldAuditTests
{
    private const string Root = "artifacts/v5-p6t-function-membership";
    private const string SnapshotRoot = "eval/a99-closed-loop/pdf-canonical-source-v1";
    private const string PreflightPath = Root + "/p6th2c-clean-v1-v2-preflight/h2c-clean-v1-v2-preflight.v1.json";
    private const string InputManifestPath = Root + "/p6th2c-end-pointer-preflight-v2/h2c-exact-end-pointer-preflight.v2.json";
    private const string V1ResultPath = Root + "/p6th2c-clean-v1-rerun-capture-20261005/result.v1.json";
    private const string V2ResultPath = Root + "/p6th2c-clean-v2-capture-20261005/result.v1.json";
    private const string V1RawRoot = Root + "/p6th2c-clean-v1-rerun-capture-20261005/raw";
    private const string V2RawRoot = Root + "/p6th2c-clean-v2-capture-20261005/raw";
    private const string OutputRoot = Root + "/p6th2c-clean-paired-gold-audit";

    private static readonly Source[] Sources =
    [
        new("SRC-089", "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf", "p6tf1-preflight/retry-src089-result.v1.json", F1Shape.ResultRow, true),
        new("SRC-041", "todo10_8/heading_corpus_100/03_tai_chinh_ke_toan/041_IBRD_Financial_Statements_June_2025.pdf", "p6te-src041-e-challenge/f1.raw-capture.v1.json", F1Shape.RawCapture, false),
        new("SRC-095", "todo10_8/heading_corpus_100/07_system_generated/095_RFC9114_HTTP_3.pdf", "p6tf1-preflight/result.v1.json", F1Shape.ResultRows, true),
        new("DOC-0252", "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf", "p6te-doc0252-e-challenge/f1.raw-capture.v1.json", F1Shape.RawCapture, false),
        new("DOC-0256", "todo10_8/heading_corpus_100/05_bien_ban_hop/076_ICP_IACG08_Minutes_2023.pdf", "p6te-doc0256-e-challenge/f1.raw-capture.v1.json", F1Shape.RawCapture, false),
    ];

    [Fact]
    public void Clean_V1_and_V2_are_paired_against_unchanged_canonical_Gold()
    {
        var preflightBytes = File.ReadAllBytes(TestRepository.Path(PreflightPath));
        using var preflight = JsonDocument.Parse(preflightBytes);
        using var inputManifest = JsonDocument.Parse(File.ReadAllBytes(TestRepository.Path(InputManifestPath)));
        using var v1Result = JsonDocument.Parse(File.ReadAllBytes(TestRepository.Path(V1ResultPath)));
        using var v2Result = JsonDocument.Parse(File.ReadAllBytes(TestRepository.Path(V2ResultPath)));
        Assert.Equal("RAW_FROZEN_GOLD_NOT_READ", v1Result.RootElement.GetProperty("status").GetString());
        Assert.Equal("RAW_FROZEN_GOLD_NOT_READ", v2Result.RootElement.GetProperty("status").GetString());
        Assert.Equal(31, v1Result.RootElement.GetProperty("providerCalls").GetInt32());
        Assert.Equal(31, v2Result.RootElement.GetProperty("providerCalls").GetInt32());
        Assert.Equal(0, v1Result.RootElement.GetProperty("retries").GetInt32());
        Assert.Equal(0, v2Result.RootElement.GetProperty("retries").GetInt32());
        Assert.False(v1Result.RootElement.GetProperty("goldRead").GetBoolean());
        Assert.False(v2Result.RootElement.GetProperty("goldRead").GetBoolean());

        var manifestRows = preflight.RootElement.GetProperty("requests").EnumerateArray().ToArray();
        Assert.Equal(31, manifestRows.Length);
        var inputRows = inputManifest.RootElement.GetProperty("requestUniverse").EnumerateArray().ToDictionary(
            row => $"{row.GetProperty("DocumentId").GetString()}|{row.GetProperty("PackId").GetString()}|{row.GetProperty("Anchor").GetString()}", StringComparer.Ordinal);
        var aliases = Sources.ToDictionary(source => source.DocumentId, BuildOccurrenceAliases, StringComparer.Ordinal);
        var gold = Sources.ToDictionary(source => source.DocumentId, LoadGold, StringComparer.Ordinal);
        var rows = new List<PairRow>(31);
        var actualUserMessageHashMatches = 0;
        var manifestUserHashFieldMatches = 0;

        foreach (var manifestRow in manifestRows)
        {
            var documentId = manifestRow.GetProperty("documentId").GetString()!;
            var packId = manifestRow.GetProperty("packId").GetString()!;
            var anchor = manifestRow.GetProperty("anchor").GetString()!;
            var alias = manifestRow.GetProperty("anchorAlias").GetString()!;
            var issuedCount = manifestRow.GetProperty("issuedOccurrenceCount").GetInt32();
            var key = $"{documentId}|{packId}|{anchor}";
            var issued = inputRows[key].GetProperty("IssuedOccurrences").EnumerateArray().Select(value => value.GetString()!).ToArray();
            Assert.Equal(issuedCount, issued.Length);
            Assert.Equal(manifestRow.GetProperty("issuedOccurrencesSha256").GetString(), Hash(string.Join("\n", issued) + "\n"));
            var v1 = ReadCapture(V1RawRoot, documentId, anchor);
            var v2 = ReadCapture(V2RawRoot, documentId, anchor);
            Assert.Equal("stop", v1.FinishReason);
            Assert.Equal("stop", v2.FinishReason);
            Assert.Null(v1.TransportError);
            Assert.Null(v2.TransportError);
            Assert.Equal(manifestRow.GetProperty("providerBodies").GetProperty("v1").GetString(), v1.ProviderBodySha256);
            Assert.Equal(manifestRow.GetProperty("providerBodies").GetProperty("v2").GetString(), v2.ProviderBodySha256);
            Assert.Equal(issued, v1.IssuedOccurrences);
            Assert.Equal(alias, aliases[documentId][anchor]);
            if (v1.UserMessageSha256 == v2.UserMessageSha256) actualUserMessageHashMatches++;
            if (manifestRow.GetProperty("v1").GetProperty("userMessageSha256").GetString() == v1.UserMessageSha256 &&
                manifestRow.GetProperty("v2").GetProperty("userMessageSha256").GetString() == v2.UserMessageSha256) manifestUserHashFieldMatches++;

            var expectedGold = gold[documentId].Extents.SingleOrDefault(extent => extent[0] == alias);
            var v1Decision = TryParse(v1.RawResponse, anchor, issued, out var v1Parsed) ? v1Parsed : null;
            var v2Decision = TryParse(v2.RawResponse, anchor, issued, out var v2Parsed) ? v2Parsed : null;
            var v1Score = ScoreArm(v1Decision, expectedGold, issued, aliases[documentId]);
            var v2Score = ScoreArm(v2Decision, expectedGold, issued, aliases[documentId]);
            rows.Add(new PairRow(documentId, packId, anchor, alias, expectedGold?.ToArray(), v1Score, v2Score,
                Transition(v1Score, v2Score), v1.ProviderBodySha256, v2.ProviderBodySha256,
                v1.FinishReason, v2.FinishReason, v1.RawResponseSha256, v2.RawResponseSha256, v1.RawSseSha256, v2.RawSseSha256,
                v1.ReasoningTokens, v2.ReasoningTokens, v1.PromptTokens, v2.PromptTokens,
                v1.CompletionTokens, v2.CompletionTokens));
        }

        Assert.Equal(31, rows.Count);
        Assert.Equal(31, actualUserMessageHashMatches);
        var trueGoldRows = rows.Where(row => row.GoldExtent is not null).ToArray();
        Assert.Equal(27, trueGoldRows.Length);
        var v1Aggregate = Aggregate(trueGoldRows.Select(row => row.V1).ToArray());
        var v2Aggregate = Aggregate(trueGoldRows.Select(row => row.V2).ToArray());
        var transitionCounts = trueGoldRows.GroupBy(row => row.Transition, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        foreach (var name in new[] { "V1_WRONG_TO_V2_CORRECT", "V1_CORRECT_TO_V2_WRONG", "BOTH_CORRECT", "BOTH_WRONG" })
            transitionCounts.TryAdd(name, 0);

        FreezeArtifact.AssertJson(OutputRoot, "h2c-clean-v1-v2-paired-gold-audit.v1.json", new
        {
            schemaVersion = "v5-p6th2c-clean-v1-v2-paired-gold-audit-v1",
            status = "PAIRED_GOLD_SCORE_RAW_FROZEN_NO_PROVIDER_CALLS",
            authority = new
            {
                preflightSha256 = Hash(preflightBytes),
                v1ResultSha256 = Hash(File.ReadAllBytes(TestRepository.Path(V1ResultPath))),
                v2ResultSha256 = Hash(File.ReadAllBytes(TestRepository.Path(V2ResultPath))),
                goldAuthorities = Sources.Select(source => new { documentId = source.DocumentId, canonicalGoldPath = FrozenHistoryGold.Entry(source.DocumentId).CanonicalGoldPath,
                    goldSha256 = gold[source.DocumentId].GoldSha256, sourceSha256 = gold[source.DocumentId].SourceSha256 }).ToArray(),
                primaryProviderCallsDuringAudit = 0,
                goldMutation = "NONE",
                rawCaptureMutation = "NONE",
                runtimeChanged = false,
            },
            pairedCaptureIntegrity = new
            {
                requestPairs = rows.Count,
                v1ProviderCalls = 31,
                v2ProviderCalls = 31,
                v1Stop = rows.Count(row => row.V1FinishReason == "stop"),
                v2Stop = rows.Count(row => row.V2FinishReason == "stop"),
                v1Retries = 0,
                v2Retries = 0,
                providerBodyHashesMatchPreflight = true,
                actualV1V2UserMessageHashMatches = actualUserMessageHashMatches,
                preregisteredUserMessageHashFieldsMatchingActual = manifestUserHashFieldMatches,
                preregisteredUserHashFieldInterpretation = manifestUserHashFieldMatches == 31
                    ? "MATCHES_CAPTURE"
                    : "STALE_METADATA; EXACT_PROVIDER_BODY_HASH_AND_PAIRED_ACTUAL_USER_HASH_USED",
                sameModelProviderReasoningAndPacking = true,
            },
            cohort = new { registeredRequests = rows.Count, trueGoldAnchors = trueGoldRows.Length, falseG2aAnchors = rows.Count - trueGoldRows.Length },
            score = new { V1 = v1Aggregate, V2 = v2Aggregate },
            pairedContingency = new
            {
                v1WrongToV2Correct = transitionCounts["V1_WRONG_TO_V2_CORRECT"],
                v1CorrectToV2Wrong = transitionCounts["V1_CORRECT_TO_V2_WRONG"],
                bothCorrect = transitionCounts["BOTH_CORRECT"],
                bothWrong = transitionCounts["BOTH_WRONG"],
            },
            rows,
            interpretation = new
            {
                unitOfComparison = "same document + pack + anchor; correctness is exact full source extent against frozen Gold",
                firstOutsideExact = "predicted immediate successor identity equals the Gold first outside occurrence",
                overrunDistance = "extra contiguous occurrences beyond the Gold extent; aggregate reports total and mean/max among overextents",
                prohibited = "NO_GOLD_MUTATION_NO_PROVIDER_RERUN_NO_RUNTIME_PROMOTION_FROM_THIS_SCORE_ALONE",
            },
        });
    }

    private static ArmScore ScoreArm(Decision? decision, string[]? goldExtent, IReadOnlyList<string> issued,
        IReadOnlyDictionary<string, string> aliasByOccurrence)
    {
        if (goldExtent is null) return new(decision is not null, "FALSE_G2A_ANCHOR", null, null, 0, false, 0);
        if (decision is null) return new(false, "INVALID_CONTRACT", null, null, 0, false, goldExtent.Length);
        var predictedAliases = decision.Members.Select(id => aliasByOccurrence[id]).ToArray();
        var outcome = Classify(predictedAliases, goldExtent);
        var goldOutside = goldExtent.Length < issued.Count ? issued[goldExtent.Length] : null;
        return new(true, outcome, decision.Members[^1], decision.FirstOutsideOccurrence,
            Math.Max(0, predictedAliases.Length - goldExtent.Length), decision.FirstOutsideOccurrence == goldOutside, goldExtent.Length);
    }

    private static object Aggregate(IReadOnlyList<ArmScore> scores)
    {
        var over = scores.Where(score => score.Outcome == "OVEREXTENT").Select(score => score.OverrunDistance).ToArray();
        return new
        {
            exact = scores.Count(score => score.Outcome == "EXACT"),
            overextent = scores.Count(score => score.Outcome == "OVEREXTENT"),
            underextent = scores.Count(score => score.Outcome == "UNDEREXTENT"),
            wrongParts = scores.Count(score => score.Outcome == "WRONG_PARTS"),
            invalidContract = scores.Count(score => score.Outcome == "INVALID_CONTRACT"),
            singletonExact = scores.Count(score => score.Outcome == "EXACT" && score.GoldPartCount == 1),
            singletonN = scores.Count(score => score.GoldPartCount == 1),
            multipartExact = scores.Count(score => score.Outcome == "EXACT" && score.GoldPartCount > 1),
            multipartN = scores.Count(score => score.GoldPartCount > 1),
            firstOutsideExact = scores.Count(score => score.FirstOutsideExact),
            firstOutsideN = scores.Count(score => score.Outcome != "FALSE_G2A_ANCHOR"),
            overrunDistance = new
            {
                total = over.Sum(),
                meanAmongOverextents = over.Length == 0 ? 0 : Math.Round(over.Average(), 3),
                max = over.Length == 0 ? 0 : over.Max(),
            },
            byOutcome = scores.GroupBy(score => score.Outcome, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => new { outcome = group.Key, count = group.Count() }).ToArray(),
        };
    }

    private static string Transition(ArmScore v1, ArmScore v2)
    {
        if (v1.Outcome == "FALSE_G2A_ANCHOR" || v2.Outcome == "FALSE_G2A_ANCHOR") return "FALSE_G2A_ANCHOR";
        var a = v1.Outcome == "EXACT"; var b = v2.Outcome == "EXACT";
        return (a, b) switch
        {
            (false, true) => "V1_WRONG_TO_V2_CORRECT",
            (true, false) => "V1_CORRECT_TO_V2_WRONG",
            (true, true) => "BOTH_CORRECT",
            _ => "BOTH_WRONG",
        };
    }

    private static Capture ReadCapture(string root, string documentId, string anchor)
    {
        var path = TestRepository.Path($"{root}/{documentId}_{anchor}.raw-capture.v1.json");
        var bytes = File.ReadAllBytes(path);
        using var document = JsonDocument.Parse(bytes);
        var row = document.RootElement;
        var response = row.GetProperty("rawResponse").GetString()!;
        var sse = row.GetProperty("rawSse").GetString()!;
        Assert.Equal(Hash(response), row.GetProperty("rawResponseSha256").GetString());
        Assert.Equal(Hash(sse), row.GetProperty("rawSseSha256").GetString());
        Assert.Equal(documentId, row.GetProperty("DocumentId").GetString());
        Assert.Equal(anchor, row.GetProperty("Anchor").GetString());
        var issued = row.TryGetProperty("issuedOccurrences", out var issuedValue)
            ? issuedValue.EnumerateArray().Select(value => value.GetString()!).ToArray()
            : [];
        return new(row.GetProperty("providerBodySha256").GetString()!, row.GetProperty("userMessageSha256").GetString()!,
            row.GetProperty("finishReason").GetString()!, row.TryGetProperty("transportError", out var error) && error.ValueKind != JsonValueKind.Null ? error.GetString() : null,
            issued, response,
            row.GetProperty("rawResponseSha256").GetString()!, row.GetProperty("rawSseSha256").GetString()!,
            ReadNullableInt(row, "reasoningTokens"), ReadNullableInt(row, "promptTokens"), ReadNullableInt(row, "completionTokens"));
    }

    private static IReadOnlyDictionary<string, string> BuildOccurrenceAliases(Source source)
    {
        var sourceHash = CanonicalSemanticSourceHash.Compute(TestRepository.Path(source.PdfPath));
        var plan = PdfCandidateAuthorityQualificationAdapter.PrepareFromSnapshot(TestRepository.Path($"{SnapshotRoot}/{sourceHash}.json"), source.DocumentId);
        using var capture = JsonDocument.Parse(File.ReadAllBytes(TestRepository.Path($"{Root}/{source.F1Path}")));
        var row = source.F1Shape switch
        {
            F1Shape.RawCapture => capture.RootElement,
            F1Shape.ResultRow => capture.RootElement.GetProperty("row"),
            F1Shape.ResultRows => capture.RootElement.GetProperty("rows").EnumerateArray().Single(value => value.GetProperty("documentId").GetString() == source.DocumentId),
            _ => throw new InvalidDataException("unknown F1 capture shape"),
        };
        var pack = plan.Packs.Single(value => value.PackId == row.GetProperty("packId").GetString());
        var correspondences = source.UsesCorrespondences ? BuildCorrespondences(pack) : new Dictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>>(StringComparer.Ordinal);
        var prepared = PdfTotalOccurrenceRoleQualificationAdapter.PrepareFunctionMembershipF1(plan, pack, correspondences);
        Assert.Equal(96, prepared.Request.Occurrences.Count);
        return prepared.Request.Occurrences.ToDictionary(value => value.Id, value => value.Atom.Alias, StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>> BuildCorrespondences(PdfCandidateAuthorityPreparedPack pack)
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

    private static GoldDocument LoadGold(Source source)
    {
        var entry = FrozenHistoryGold.Entry(source.DocumentId);
        FrozenHistoryGold.RequireCapability(source.DocumentId, GoldCapability.Occurrence);
        using var document = FrozenHistoryGold.Resolve(source.DocumentId);
        Assert.Equal(entry.SourceSha256, document.RootElement.GetProperty("source").GetProperty("sourceSha256").GetString());
        var extents = document.RootElement.GetProperty("semantic").GetProperty("claims").EnumerateArray()
            .Select(claim => claim.GetProperty("sourceParts").EnumerateArray().Select(part => part.GetProperty("sourceAlias").GetString()!).ToArray()).ToArray();
        return new(entry.GoldSha256, entry.SourceSha256, extents);
    }

    private static bool TryParse(string raw, string anchor, IReadOnlyList<string> issued, out Decision decision)
    {
        decision = new([], null, "");
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 || !root.TryGetProperty("decisions", out var decisions) || decisions.ValueKind != JsonValueKind.Array || decisions.GetArrayLength() != 1) return false;
            var row = decisions[0];
            if (row.ValueKind != JsonValueKind.Object || row.EnumerateObject().Count() != 5 || row.GetProperty("anchor").GetString() != anchor) return false;
            var members = row.GetProperty("headingMembers").EnumerateArray().Select(value => value.GetString()).ToArray();
            if (members.Length == 0 || members.Any(value => value is null) || !members.SequenceEqual(issued.Take(members.Length), StringComparer.Ordinal) || row.GetProperty("endOccurrence").GetString() != members[^1]) return false;
            var outside = row.GetProperty("firstOutsideOccurrence");
            var role = row.GetProperty("firstOutsideRole").GetString();
            var valid = members.Length == issued.Count
                ? outside.ValueKind == JsonValueKind.Null && role == "NO_VISIBLE_SUCCESSOR"
                : outside.ValueKind == JsonValueKind.String && outside.GetString() == issued[members.Length] && role is "NEW_HEADING" or "BODY_CONTENT" or "PAGE_FURNITURE" or "TABLE_OR_STRUCTURED_CONTENT" or "OTHER_NON_HEADING";
            if (!valid) return false;
            decision = new(members.Select(value => value!).ToArray(), outside.ValueKind == JsonValueKind.Null ? null : outside.GetString(), role!);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentOutOfRangeException) { return false; }
    }

    private static string Classify(IReadOnlyList<string> predicted, IReadOnlyList<string> expected)
    {
        if (predicted.SequenceEqual(expected, StringComparer.Ordinal)) return "EXACT";
        if (predicted.Count < expected.Count && predicted.SequenceEqual(expected.Take(predicted.Count), StringComparer.Ordinal)) return "UNDEREXTENT";
        if (predicted.Count > expected.Count && expected.SequenceEqual(predicted.Take(expected.Count), StringComparer.Ordinal)) return "OVEREXTENT";
        return "WRONG_PARTS";
    }

    private static int? ReadNullableInt(JsonElement row, string property) => row.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : null;
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));

    private enum F1Shape { RawCapture, ResultRow, ResultRows }
    private sealed record Source(string DocumentId, string PdfPath, string F1Path, F1Shape F1Shape, bool UsesCorrespondences);
    private sealed record GoldDocument(string GoldSha256, string SourceSha256, string[][] Extents);
    private sealed record Capture(string ProviderBodySha256, string UserMessageSha256, string FinishReason, string? TransportError,
        string[] IssuedOccurrences, string RawResponse, string RawResponseSha256, string RawSseSha256, int? ReasoningTokens, int? PromptTokens, int? CompletionTokens);
    private sealed record Decision(string[] Members, string? FirstOutsideOccurrence, string FirstOutsideRole);
    private sealed record ArmScore(bool LedgerValid, string Outcome, string? PredictedEnd, string? FirstOutsideOccurrence, int OverrunDistance,
        bool FirstOutsideExact, int GoldPartCount = 0);
    private sealed record PairRow(string DocumentId, string PackId, string Anchor, string AnchorAlias, string[]? GoldExtent,
        ArmScore V1, ArmScore V2, string Transition, string V1ProviderBodySha256, string V2ProviderBodySha256,
        string V1FinishReason, string V2FinishReason,
        string V1RawResponseSha256, string V2RawResponseSha256, string V1RawSseSha256, string V2RawSseSha256,
        int? V1ReasoningTokens, int? V2ReasoningTokens, int? V1PromptTokens, int? V2PromptTokens, int? V1CompletionTokens, int? V2CompletionTokens);
}
