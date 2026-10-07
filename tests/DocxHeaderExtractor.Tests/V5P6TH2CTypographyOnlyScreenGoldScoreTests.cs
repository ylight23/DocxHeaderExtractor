using System.Security.Cryptography;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.V5Qualification;
using UglyToad.PdfPig;

namespace DocxHeaderExtractor.Tests;

/// <summary>Post-capture, provider-free Gold score for the four-anchor typography-only diagnostic screen.</summary>
public sealed class V5P6TH2CTypographyOnlyScreenGoldScoreTests
{
    private const string Root = "artifacts/v5-p6t-function-membership";
    private const string PreflightRoot = Root + "/p6th2c-typography-only-screen-preflight";
    private const string CaptureRoot = Root + "/p6th2c-typography-only-screen-capture-20261005";
    private const string HistoricalPreflightRoot = Root + "/p6th2c-evidence-complete-preflight";
    private const string HistoricalCaptureRoot = Root + "/p6th2c-evidence-complete-capture-20261005";
    private const string OutputRoot = Root + "/p6th2c-typography-only-screen-gold-score";
    private const string TaxonomyCommit = "be73ac1";

    private sealed record Target(string DocumentId, string Anchor);
    private sealed record Decision(string[] Members, string EndOccurrence, string? FirstOutsideOccurrence, string FirstOutsideRole);
    private sealed record Score(string ExtentOutcome, bool EndOccurrenceExact, bool? FirstOutsideOccurrenceExact,
        int SignedBoundaryDistance, int AbsoluteBoundaryDistance, string PredictedEndOccurrence,
        string? PredictedFirstOutsideOccurrence, string FirstOutsideRole);

    private static readonly Target[] Targets =
    [
        new("SRC-089", "O17"), new("SRC-089", "O19"), new("SRC-041", "O4"), new("DOC-0256", "O1"),
    ];

    [Fact]
    public void Typography_only_screen_is_scored_after_immutable_capture_freeze_and_then_canonical_gold()
    {
        if (FrozenHistoryReplayPolicy.RichGeometry("SRC-089") == HistoricalReplayStatus.FrozenEvidenceOnly)
        {
            FrozenHistoryReplayPolicy.AssertFrozenEvidenceOnly("SRC-089", nameof(V5P6TH2CTypographyOnlyScreenGoldScoreTests));
            return;
        }

        var repo = TestRepository.Root();
        var rawPaths = Targets.Select(target => TestRepository.Path($"{CaptureRoot}/{target.DocumentId}_{target.Anchor}.raw-capture.v1.json"))
            .Concat(Targets.Select(target => TestRepository.Path($"{HistoricalCaptureRoot}/arm-a/{target.DocumentId}_{target.Anchor}.raw-capture.v1.json"))).ToArray();
        if (rawPaths.Any(path => !File.Exists(path)))
        {
            AssertSanitizedScoreArtifact();
            return;
        }

        var preflightBytes = File.ReadAllBytes(TestRepository.Path(PreflightRoot + "/typography-only-screen-preflight.v1.json"));
        var manifestBytes = File.ReadAllBytes(TestRepository.Path(PreflightRoot + "/execution-manifest.v1.json"));
        var freezeBytes = File.ReadAllBytes(TestRepository.Path(CaptureRoot + "/capture-freeze.v1.json"));
        var historicalPreflightBytes = File.ReadAllBytes(TestRepository.Path(HistoricalPreflightRoot + "/h2c-evidence-complete-preflight.v1.json"));
        var historicalFreezeBytes = File.ReadAllBytes(TestRepository.Path(HistoricalCaptureRoot + "/capture-freeze.v1.json"));
        using var manifest = JsonDocument.Parse(manifestBytes);
        using var captureFreeze = JsonDocument.Parse(freezeBytes);
        using var historicalPreflight = JsonDocument.Parse(historicalPreflightBytes);
        using var historicalFreeze = JsonDocument.Parse(historicalFreezeBytes);
        Assert.Equal("RAW_HASH_VERIFIED_FOUR_CONTRACT_VALID_RESPONSES_GOLD_STILL_CLOSED", captureFreeze.RootElement.GetProperty("status").GetString());
        Assert.Equal(Hash(preflightBytes), captureFreeze.RootElement.GetProperty("preflightSha256").GetString());
        Assert.Equal(Hash(manifestBytes), captureFreeze.RootElement.GetProperty("executionManifestSha256").GetString());
        Assert.Equal("RAW_HASH_VERIFIED_61_RESPONSES_1_UPSTREAM_429_GOLD_STILL_CLOSED", historicalFreeze.RootElement.GetProperty("status").GetString());

        var requests = P6TH2CEndPointerCanary.BuildAllForTreatment(repo, "V2")
            .Where(request => Targets.Any(target => target.DocumentId == request.Source.DocumentId && target.Anchor == request.Anchor))
            .OrderBy(request => Array.FindIndex(Targets, target => target.DocumentId == request.Source.DocumentId && target.Anchor == request.Anchor)).ToArray();
        Assert.Equal(4, requests.Length);
        var plans = manifest.RootElement.GetProperty("requests").EnumerateArray().ToArray();
        Assert.Equal(4, plans.Length);
        var historicalRows = historicalPreflight.RootElement.GetProperty("requests").EnumerateArray()
            .ToDictionary(row => Key(row.GetProperty("documentId").GetString()!, row.GetProperty("anchor").GetString()!), StringComparer.Ordinal);
        var historicalFrozen = historicalFreeze.RootElement.GetProperty("armA").GetProperty("rawFiles").EnumerateArray()
            .ToDictionary(row => Key(row.GetProperty("DocumentId").GetString()!, row.GetProperty("Anchor").GetString()!), StringComparer.Ordinal);
        var rows = new List<object>(4);

        // All raw bytes, manifest/hash bindings, and contract parsing are verified before Gold is opened.
        var prepared = new List<(P6TH2CEndPointerCanary.Request Request, JsonElement CurrentRaw, JsonElement HistoricalRaw)>();
        for (var index = 0; index < requests.Length; index++)
        {
            var request = requests[index];
            var key = Key(request.Source.DocumentId, request.Anchor);
            var currentPath = TestRepository.Path($"{CaptureRoot}/{request.Source.DocumentId}_{request.Anchor}.raw-capture.v1.json");
            var historicalPath = TestRepository.Path($"{HistoricalCaptureRoot}/arm-a/{request.Source.DocumentId}_{request.Anchor}.raw-capture.v1.json");
            using var currentDocument = JsonDocument.Parse(File.ReadAllBytes(currentPath));
            using var historicalDocument = JsonDocument.Parse(File.ReadAllBytes(historicalPath));
            var current = currentDocument.RootElement.Clone();
            var historical = historicalDocument.RootElement.Clone();
            var plan = plans[index];
            var planRow = plan.GetProperty("row");
            var treatment = planRow.GetProperty("typographyOnlyTreatment");
            var rebuiltTreatment = V5P6TH2CEvidenceCompletePreflightTests.BuildTypographyOnlyRequest(repo, request);
            var rebuiltHistorical = V5P6TH2CEvidenceCompletePreflightTests.BuildArmARequest(request);
            var historicalPlan = historicalRows[key];
            var historicalReceipt = historicalFrozen[key];
            Assert.Equal(request.Source.DocumentId, current.GetProperty("DocumentId").GetString());
            Assert.Equal(request.Anchor, current.GetProperty("Anchor").GetString());
            Assert.Equal("VALID", current.GetProperty("contractStatus").GetString());
            Assert.Equal("stop", current.GetProperty("finishReason").GetString());
            Assert.Equal(0, current.GetProperty("retryCount").GetInt32());
            Assert.False(current.GetProperty("goldReadDuringCapture").GetBoolean());
            Assert.Equal(treatment.GetProperty("providerBodySha256").GetString(), current.GetProperty("providerBodySha256").GetString());
            Assert.Equal(treatment.GetProperty("userMessageSha256").GetString(), current.GetProperty("userMessageSha256").GetString());
            Assert.Equal(rebuiltTreatment.ProviderBodySha256, current.GetProperty("providerBodySha256").GetString());
            Assert.Equal(rebuiltTreatment.UserMessageSha256, current.GetProperty("userMessageSha256").GetString());
            Assert.Equal(Hash(current.GetProperty("rawSse").GetString()!), current.GetProperty("rawSseSha256").GetString());
            Assert.Equal(Hash(current.GetProperty("rawResponse").GetString()!), current.GetProperty("rawResponseSha256").GetString());
            Assert.True(TryParse(current.GetProperty("rawResponse").GetString()!, request.Anchor, request.IssuedOccurrences, out _));

            Assert.Equal("VALID", historical.GetProperty("contractStatus").GetString());
            Assert.Equal("stop", historical.GetProperty("finishReason").GetString());
            Assert.Equal(0, historical.GetProperty("retryCount").GetInt32());
            Assert.False(historical.GetProperty("goldReadDuringCapture").GetBoolean());
            Assert.Equal(historicalPlan.GetProperty("armA").GetProperty("providerBodySha256").GetString(), historical.GetProperty("providerBodySha256").GetString());
            Assert.Equal(historicalPlan.GetProperty("armA").GetProperty("userMessageSha256").GetString(), historical.GetProperty("userMessageSha256").GetString());
            Assert.Equal(rebuiltHistorical.ProviderBodySha256, historical.GetProperty("providerBodySha256").GetString());
            Assert.Equal(rebuiltHistorical.UserMessageSha256, historical.GetProperty("userMessageSha256").GetString());
            Assert.Equal(historicalReceipt.GetProperty("RawFileSha256").GetString(), Hash(File.ReadAllBytes(historicalPath)));
            Assert.Equal(Hash(historical.GetProperty("rawSse").GetString()!), historical.GetProperty("rawSseSha256").GetString());
            Assert.Equal(Hash(historical.GetProperty("rawResponse").GetString()!), historical.GetProperty("rawResponseSha256").GetString());
            Assert.True(TryParse(historical.GetProperty("rawResponse").GetString()!, request.Anchor, request.IssuedOccurrences, out _));
            prepared.Add((request, current, historical));
        }

        // Gold access begins here, after both frozen response authorities have passed validation.
        foreach (var item in prepared)
        {
            var aliases = OccurrenceAliases(item.Request);
            var gold = LoadGold(item.Request.Source.DocumentId);
            var expected = gold.Extents.Single(extent => extent.Length > 0 && extent[0] == item.Request.AnchorAlias);
            Assert.Equal(expected, item.Request.IssuedOccurrences.Take(expected.Length).Select(id => aliases[id]).ToArray());
            Assert.True(TryParse(item.HistoricalRaw.GetProperty("rawResponse").GetString()!, item.Request.Anchor, item.Request.IssuedOccurrences, out var historicalDecision));
            Assert.True(TryParse(item.CurrentRaw.GetProperty("rawResponse").GetString()!, item.Request.Anchor, item.Request.IssuedOccurrences, out var typographyDecision));
            var historicalScore = ScoreDecision(historicalDecision, expected, item.Request.IssuedOccurrences, aliases);
            var typographyScore = ScoreDecision(typographyDecision, expected, item.Request.IssuedOccurrences, aliases);
            rows.Add(new
            {
                documentId = item.Request.Source.DocumentId,
                packId = item.Request.PackId,
                anchor = item.Request.Anchor,
                anchorAlias = item.Request.AnchorAlias,
                sourceSha256 = item.Request.SourceSha256,
                canonicalGoldPath = FrozenHistoryGold.Entry(item.Request.Source.DocumentId).CanonicalGoldPath,
                canonicalGoldSha256 = gold.GoldSha256,
                goldExtent = expected,
                historicalA = historicalScore,
                typographyOnly = typographyScore,
                transition = Transition(historicalScore, typographyScore),
            });
        }

        var typedRows = rows.Select(row => JsonSerializer.SerializeToElement(row)).ToArray();
        FreezeArtifact.AssertJson(OutputRoot, "h2c-typography-only-screen-gold-score.v1.json", new
        {
            schemaVersion = "v5-p6th2c-typography-only-screen-gold-score-v1",
            status = "POST_HOC_DIAGNOSTIC_GOLD_SCORE_AFTER_FROZEN_FOUR_CALL_CAPTURE",
            authority = new
            {
                preflightCommit = "148a3de",
                captureFreezeCommit = "698f270",
                taxonomyCommit = TaxonomyCommit,
                historicalControlCommit = "dfe96aa",
                preflightSha256 = Hash(preflightBytes),
                executionManifestSha256 = Hash(manifestBytes),
                captureFreezeSha256 = Hash(freezeBytes),
                historicalEvidencePreflightSha256 = Hash(historicalPreflightBytes),
                historicalCaptureFreezeSha256 = Hash(historicalFreezeBytes),
                gold = "CANONICAL_READ_ONLY_AFTER_RAW_HASH_FREEZE",
                goldMutation = "NONE",
                providerCallsDuringAudit = 0,
                runtimeChanged = false,
            },
            experiment = new
            {
                comparisonType = "POST_HOC_DIAGNOSTIC_REPLICATION_AGAINST_FROZEN_HISTORICAL_CONTROL",
                developmentOnly = true,
                postHocSelectedCohort = true,
                runtimePromotion = "FORBIDDEN",
                notPaired = true,
                notMatched = true,
                notCausal = true,
                falseAnchorPolicy = "NONE_IN_FOUR_TARGETS; ALL_FOUR_ARE_SCORED_AGAINST_A_GOLD_EXTENT",
                firstOutsideRole = new { recorded = true, scored = false, reason = "NO_FROZEN_GOLD_ROLE_AUTHORITY" },
            },
            scoreDefinition = new
            {
                extentOutcome = "EXACT / OVEREXTENT / UNDEREXTENT / WRONG_PARTS",
                endOccurrenceExact = "predicted end handle equals the final Gold source part mapped into this issued request",
                firstOutsideOccurrenceExact = "predicted first outside handle equals the immediate issued successor of the final Gold part",
                signedBoundaryDistance = "predicted member count minus Gold part count; negative=underextent, positive=overextent",
                absoluteBoundaryDistance = "absolute signed boundary distance",
            },
            preregisteredDecisionGuide = new
            {
                onlyO4Recovers = "TYPOGRAPHY_SIGNAL_SUPPORTED_FOR_ONE_OBSERVED_SUBCLASS; NOT_GENERAL_H2C_IMPROVEMENT",
                o4DoesNotRecover = "COMPOSITE_B_RECOVERY_NOT_ATTRIBUTABLE_TO_TYPOGRAPHY_ALONE",
                o19WrongButDistanceShrinks = "SEVERITY_SIGNAL_ONLY_NOT_EXACT_BOUNDARY_SOLUTION",
                unexpectedO17OrO19OrO1Recovery = "FORENSIC_REQUIRED_BEFORE_INTERPRETATION",
                multipleUnexpectedRecoveries = "INVESTIGATE_MODEL_VARIABILITY_AND_RAW_TYPOGRAPHY_FACTS; NO_GENERALIZATION",
                runtimePromotion = "FORBIDDEN_FROM_THIS_N4_POST_HOC_DIAGNOSTIC",
            },
            summary = new
            {
                historicalAExact = typedRows.Count(row => row.GetProperty("historicalA").GetProperty("ExtentOutcome").GetString() == "EXACT"),
                typographyOnlyExact = typedRows.Count(row => row.GetProperty("typographyOnly").GetProperty("ExtentOutcome").GetString() == "EXACT"),
                historicalWrongToTypographyExact = typedRows.Count(row => row.GetProperty("transition").GetString() == "WRONG_TO_RIGHT"),
                historicalExactToTypographyWrong = typedRows.Count(row => row.GetProperty("transition").GetString() == "RIGHT_TO_WRONG"),
                bothWrong = typedRows.Count(row => row.GetProperty("transition").GetString() == "BOTH_WRONG"),
                bothRight = typedRows.Count(row => row.GetProperty("transition").GetString() == "BOTH_RIGHT"),
            },
            rows,
        });
    }

    private static void AssertSanitizedScoreArtifact()
    {
        var path = TestRepository.Path(OutputRoot + "/h2c-typography-only-screen-gold-score.v1.json");
        Assert.True(File.Exists(path), "h2c-typography-only-score-requires-local-raw-archive-or-frozen-sanitized-artifact");
        using var score = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = score.RootElement;
        Assert.Equal("POST_HOC_DIAGNOSTIC_GOLD_SCORE_AFTER_FROZEN_FOUR_CALL_CAPTURE", root.GetProperty("status").GetString());
        Assert.Equal(4, root.GetProperty("rows").GetArrayLength());
        Assert.Equal(0, root.GetProperty("authority").GetProperty("providerCallsDuringAudit").GetInt32());
        Assert.Equal("NONE", root.GetProperty("authority").GetProperty("goldMutation").GetString());
        Assert.False(root.GetProperty("authority").GetProperty("runtimeChanged").GetBoolean());
        Assert.True(root.GetProperty("experiment").GetProperty("developmentOnly").GetBoolean());
        Assert.True(root.GetProperty("experiment").GetProperty("postHocSelectedCohort").GetBoolean());
        Assert.Equal("FORBIDDEN", root.GetProperty("experiment").GetProperty("runtimePromotion").GetString());
    }

    private static IReadOnlyDictionary<string, string> OccurrenceAliases(P6TH2CEndPointerCanary.Request request)
    {
        var pdfPath = TestRepository.Path(request.Source.PdfPath);
        IReadOnlyList<PdfLine> lines;
        using (var pdf = PdfDocument.Open(pdfPath)) lines = PdfLineExtraction.ExtractLines(pdf);
        var sourceSha = CanonicalSemanticSourceHash.Compute(pdfPath);
        Assert.Equal(request.SourceSha256, sourceSha);
        var authority = PdfSourceAdapter.Build(lines, sourceSha);
        Assert.Equal(request.SourceUniverseSha256, authority.SourceAliasUniverseHash);
        var start = Array.FindIndex(authority.Atoms.ToArray(), atom => atom.Alias == request.AnchorAlias);
        Assert.True(start >= 0);
        return request.IssuedOccurrences.Select((id, offset) => new { id, atom = authority.Atoms[start + offset] })
            .ToDictionary(value => value.id, value => value.atom.Alias, StringComparer.Ordinal);
    }

    private sealed record GoldDocument(string GoldSha256, IReadOnlyList<string[]> Extents);
    private static GoldDocument LoadGold(string documentId)
    {
        var entry = FrozenHistoryGold.Entry(documentId);
        FrozenHistoryGold.RequireCapability(documentId, GoldCapability.Occurrence);
        using var document = FrozenHistoryGold.Resolve(documentId);
        Assert.Equal(entry.SourceSha256, document.RootElement.GetProperty("source").GetProperty("sourceSha256").GetString());
        var extents = document.RootElement.GetProperty("semantic").GetProperty("claims").EnumerateArray()
            .Select(claim => claim.GetProperty("sourceParts").EnumerateArray().Select(part => part.GetProperty("sourceAlias").GetString()!).ToArray()).ToArray();
        return new GoldDocument(entry.GoldSha256, extents);
    }

    private static Score ScoreDecision(Decision decision, IReadOnlyList<string> gold, IReadOnlyList<string> issued,
        IReadOnlyDictionary<string, string> aliases)
    {
        var predicted = decision.Members.Select(id => aliases[id]).ToArray();
        var outcome = predicted.SequenceEqual(gold, StringComparer.Ordinal) ? "EXACT" :
            predicted.Length < gold.Count && predicted.SequenceEqual(gold.Take(predicted.Length), StringComparer.Ordinal) ? "UNDEREXTENT" :
            predicted.Length > gold.Count && gold.SequenceEqual(predicted.Take(gold.Count), StringComparer.Ordinal) ? "OVEREXTENT" : "WRONG_PARTS";
        var expectedEnd = issued[gold.Count - 1];
        var expectedOutside = gold.Count < issued.Count ? issued[gold.Count] : null;
        var signed = predicted.Length - gold.Count;
        return new Score(outcome, decision.EndOccurrence == expectedEnd, decision.FirstOutsideOccurrence == expectedOutside,
            signed, Math.Abs(signed), decision.EndOccurrence, decision.FirstOutsideOccurrence, decision.FirstOutsideRole);
    }

    private static string Transition(Score historical, Score treatment) => (historical.ExtentOutcome == "EXACT", treatment.ExtentOutcome == "EXACT") switch
    {
        (false, true) => "WRONG_TO_RIGHT", (true, false) => "RIGHT_TO_WRONG", (true, true) => "BOTH_RIGHT", _ => "BOTH_WRONG",
    };

    private static bool TryParse(string raw, string anchor, IReadOnlyList<string> issued, out Decision decision)
    {
        decision = new([], "", null, "");
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
            decision = new(members.Select(value => value!).ToArray(), row.GetProperty("endOccurrence").GetString()!, outside.ValueKind == JsonValueKind.Null ? null : outside.GetString(), role!);
            return true;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentOutOfRangeException) { return false; }
    }

    private static string Key(string documentId, string anchor) => documentId + "|" + anchor;
    private static string Hash(string value) => Hash(Encoding.UTF8.GetBytes(value));
    private static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
}
