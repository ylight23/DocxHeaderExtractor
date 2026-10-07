using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.V5Qualification;

namespace DocxHeaderExtractor.Tests;

/// <summary>Offline paired exact-boundary score after both evidence arms and raw hashes were frozen.</summary>
public sealed class V5P6TH2CEvidencePairedGoldScoreTests
{
    private const string Root = "artifacts/v5-p6t-function-membership";
    private const string SnapshotRoot = "eval/a99-closed-loop/pdf-canonical-source-v1";
    private const string PreflightRoot = Root + "/p6th2c-evidence-complete-preflight";
    private const string CaptureRoot = Root + "/p6th2c-evidence-complete-capture-20261005";
    private const string RetryRoot = Root + "/p6th2c-evidence-complete-retry-20261005";
    private const string OutputRoot = Root + "/p6th2c-evidence-paired-gold-score";

    private static readonly P6TH2CEndPointerCanary.Source[] Sources =
    [
        new("SRC-089", "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf", "p6tf1-preflight/retry-src089-result.v1.json", P6TH2CEndPointerCanary.F1Kind.ResultRow, true),
        new("SRC-041", "todo10_8/heading_corpus_100/03_tai_chinh_ke_toan/041_IBRD_Financial_Statements_June_2025.pdf", "p6te-src041-e-challenge/f1.raw-capture.v1.json", P6TH2CEndPointerCanary.F1Kind.RawCapture, false),
        new("SRC-095", "todo10_8/heading_corpus_100/07_system_generated/095_RFC9114_HTTP_3.pdf", "p6tf1-preflight/result.v1.json", P6TH2CEndPointerCanary.F1Kind.ResultRows, true),
        new("DOC-0252", "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf", "p6te-doc0252-e-challenge/f1.raw-capture.v1.json", P6TH2CEndPointerCanary.F1Kind.RawCapture, false),
        new("DOC-0256", "todo10_8/heading_corpus_100/05_bien_ban_hop/076_ICP_IACG08_Minutes_2023.pdf", "p6te-doc0256-e-challenge/f1.raw-capture.v1.json", P6TH2CEndPointerCanary.F1Kind.RawCapture, false),
    ];

    private sealed record GoldDocument(string GoldSha256, string SourceSha256, string[][] Extents);
    private sealed record Decision(string[] Members, string EndOccurrence, string? FirstOutsideOccurrence, string FirstOutsideRole);
    private sealed record ArmScore(string Outcome, bool ContractValid, string? PredictedEndOccurrence,
        string? PredictedEndAlias, string? FirstOutsideOccurrence, string? FirstOutsideRole,
        bool? FirstOutsideExact, int? SignedBoundaryDistance, int? AbsoluteBoundaryDistance,
        int? OverrunDistance, int GoldPartCount);
    private sealed record PairScore(string DocumentId, string PackId, string Anchor, string AnchorAlias,
        string[] GoldExtent, ArmScore A, ArmScore B, string PairedTransition,
        bool BUsedAuthorizedRetry, string? BPrimaryTransportError,
        string ProviderBodySha256A, string ProviderBodySha256B, int? ReasoningTokensA, int? ReasoningTokensB,
        int? PromptTokensA, int? PromptTokensB, int? CompletionTokensA, int? CompletionTokensB);

    [Fact]
    public void Evidence_complete_A_B_are_scored_paired_after_raw_capture_freeze()
    {
        var repo = TestRepository.Root();
        var preflightPath = TestRepository.Path(PreflightRoot + "/h2c-evidence-complete-preflight.v1.json");
        var executionPath = TestRepository.Path(PreflightRoot + "/execution-manifest.v1.json");
        var freezePath = TestRepository.Path(CaptureRoot + "/capture-freeze.v1.json");
        var resultPath = TestRepository.Path(CaptureRoot + "/result.v1.json");
        var preflightBytes = File.ReadAllBytes(preflightPath);
        var executionBytes = File.ReadAllBytes(executionPath);
        var freezeBytes = File.ReadAllBytes(freezePath);
        var resultBytes = File.ReadAllBytes(resultPath);
        using var preflight = JsonDocument.Parse(preflightBytes);
        using var execution = JsonDocument.Parse(executionBytes);
        using var freeze = JsonDocument.Parse(freezeBytes);
        using var result = JsonDocument.Parse(resultBytes);
        Assert.Equal("RAW_HASH_VERIFIED_61_RESPONSES_1_UPSTREAM_429_GOLD_STILL_CLOSED", freeze.RootElement.GetProperty("status").GetString());
        Assert.Equal(Hash(preflightBytes), execution.RootElement.GetProperty("preflightSha256").GetString());
        Assert.Equal(Hash(executionBytes), freeze.RootElement.GetProperty("executionManifestSha256").GetString());
        Assert.Equal(Hash(resultBytes), freeze.RootElement.GetProperty("resultSha256").GetString());
        Assert.Equal(62, execution.RootElement.GetProperty("requests").GetArrayLength());
        Assert.Equal(62, result.RootElement.GetProperty("providerCallsAttempted").GetInt32());
        Assert.Equal(0, result.RootElement.GetProperty("retry").GetInt32());
        Assert.False(result.RootElement.GetProperty("goldRead").GetBoolean());

        var plannedForArchiveCheck = execution.RootElement.GetProperty("requests").EnumerateArray().ToArray();
        var rawArchivePaths = plannedForArchiveCheck.Select(plan =>
        {
            var arm = plan.GetProperty("Arm").GetString();
            var documentId = plan.GetProperty("DocumentId").GetString();
            var anchor = plan.GetProperty("Anchor").GetString();
            return TestRepository.Path($"{CaptureRoot}/{(arm == "A" ? "arm-a" : "arm-b")}/{documentId}_{anchor}.raw-capture.v1.json");
        }).ToArray();
        var retryRawArchivePath = TestRepository.Path(RetryRoot + "/ARM_B_DOC-0252_O66.retry1.raw-capture.v1.json");
        if (rawArchivePaths.Any(path => !File.Exists(path)) || !File.Exists(retryRawArchivePath))
        {
            AssertSanitizedScoreArtifact(preflightBytes, executionBytes, freezeBytes, resultBytes);
            return;
        }

        // Validate capture/file/provider hashes from the committed raw-only freeze before resolving Gold.
        var frozenRaw = freeze.RootElement.GetProperty("armA").GetProperty("rawFiles").EnumerateArray()
            .Concat(freeze.RootElement.GetProperty("armB").GetProperty("rawFiles").EnumerateArray()).ToArray();
        Assert.Equal(62, frozenRaw.Length);
        var rawByKey = new Dictionary<string, JsonDocument>(StringComparer.Ordinal);
        var executionRows = execution.RootElement.GetProperty("requests").EnumerateArray().ToArray();
        var resultRows = result.RootElement.GetProperty("rows").EnumerateArray().ToArray();
        for (var index = 0; index < 62; index++)
        {
            var plan = executionRows[index];
            var summary = resultRows[index];
            var arm = plan.GetProperty("Arm").GetString()!;
            var docId = plan.GetProperty("DocumentId").GetString()!;
            var packId = plan.GetProperty("PackId").GetString()!;
            var anchor = plan.GetProperty("Anchor").GetString()!;
            var rawPath = TestRepository.Path($"{CaptureRoot}/{(arm == "A" ? "arm-a" : "arm-b")}/{docId}_{anchor}.raw-capture.v1.json");
            var rawBytes = File.ReadAllBytes(rawPath);
            var rawFileHash = Hash(rawBytes);
            var freezeRow = frozenRaw.Single(row => row.GetProperty("CallOrdinal").GetInt32() == index + 1);
            Assert.Equal(rawFileHash, freezeRow.GetProperty("RawFileSha256").GetString());
            var raw = JsonDocument.Parse(rawBytes);
            rawByKey.Add($"{arm}|{docId}|{packId}|{anchor}", raw);
            var root = raw.RootElement;
            Assert.Equal(arm, root.GetProperty("arm").GetString());
            Assert.Equal(docId, root.GetProperty("DocumentId").GetString());
            Assert.Equal(packId, root.GetProperty("PackId").GetString());
            Assert.Equal(anchor, root.GetProperty("Anchor").GetString());
            Assert.Equal(plan.GetProperty("SourceSha256").GetString(), root.GetProperty("SourceSha256").GetString());
            Assert.Equal(plan.GetProperty("SourceUniverseSha256").GetString(), root.GetProperty("SourceUniverseSha256").GetString());
            Assert.Equal(plan.GetProperty("providerBodySha256").GetString(), root.GetProperty("providerBodySha256").GetString());
            Assert.Equal(plan.GetProperty("userMessageSha256").GetString(), root.GetProperty("userMessageSha256").GetString());
            Assert.Equal(plan.GetProperty("systemPromptSha256").GetString(), root.GetProperty("systemPromptSha256").GetString());
            Assert.Equal(summary.GetProperty("contractStatus").GetString(), root.GetProperty("contractStatus").GetString());
            Assert.Equal(summary.GetProperty("finishReason").ValueKind == JsonValueKind.String ? summary.GetProperty("finishReason").GetString() : null,
                root.GetProperty("finishReason").ValueKind == JsonValueKind.String ? root.GetProperty("finishReason").GetString() : null);
        }

        // One separately authorized exact-body retry resolves the sole primary NO_RESPONSE.
        var retryResultPath = TestRepository.Path(RetryRoot + "/result.v1.json");
        var retryRawPath = TestRepository.Path(RetryRoot + "/ARM_B_DOC-0252_O66.retry1.raw-capture.v1.json");
        var retryReservationPath = TestRepository.Path(RetryRoot + "/execution-reservation.v1.json");
        Assert.True(File.Exists(retryResultPath) && File.Exists(retryRawPath) && File.Exists(retryReservationPath));
        var retryResultBytes = File.ReadAllBytes(retryResultPath);
        var retryRawBytes = File.ReadAllBytes(retryRawPath);
        var retryReservationBytes = File.ReadAllBytes(retryReservationPath);
        using var retryResult = JsonDocument.Parse(retryResultBytes);
        using var retryRaw = JsonDocument.Parse(retryRawBytes);
        using var retryReservation = JsonDocument.Parse(retryReservationBytes);
        var retryResultRoot = retryResult.RootElement;
        var retryRawRoot = retryRaw.RootElement;
        Assert.Equal("RETRY_RESPONSE_CONTRACT_VALID", retryResultRoot.GetProperty("status").GetString());
        Assert.Equal(62, retryResultRoot.GetProperty("primaryCalls").GetInt32());
        Assert.Equal(1, retryResultRoot.GetProperty("retryCalls").GetInt32());
        Assert.Equal(63, retryResultRoot.GetProperty("totalCalls").GetInt32());
        Assert.True(retryResultRoot.GetProperty("contractValid").GetBoolean());
        Assert.False(retryResultRoot.GetProperty("goldRead").GetBoolean());
        Assert.Equal(Hash(retryRawBytes), retryResultRoot.GetProperty("rawCaptureSha256").GetString());
        Assert.Equal(Hash(freezeBytes), retryReservation.RootElement.GetProperty("originalCaptureFreezeSha256").GetString());
        Assert.Equal(Hash(executionBytes), retryReservation.RootElement.GetProperty("executionManifestSha256").GetString());
        Assert.Equal("B", retryRawRoot.GetProperty("arm").GetString());
        Assert.Equal("DOC-0252", retryRawRoot.GetProperty("documentId").GetString());
        Assert.Equal("O66", retryRawRoot.GetProperty("anchor").GetString());
        Assert.Equal("VALID", retryRawRoot.GetProperty("contractStatus").GetString());
        Assert.Equal("stop", retryRawRoot.GetProperty("finishReason").GetString());
        Assert.Equal(0, retryRawRoot.GetProperty("transportRetryCount").GetInt32());
        Assert.Equal(Hash(retryRawRoot.GetProperty("rawResponse").GetString()!), retryRawRoot.GetProperty("rawResponseSha256").GetString());
        Assert.Equal(Hash(retryRawRoot.GetProperty("rawSse").GetString()!), retryRawRoot.GetProperty("rawSseSha256").GetString());
        Assert.Equal(Hash(File.ReadAllBytes(TestRepository.Path(CaptureRoot + "/arm-b/DOC-0252_O66.raw-capture.v1.json"))),
            retryRawRoot.GetProperty("originalPrimaryRawFileSha256").GetString());

        var requests = P6TH2CEndPointerCanary.BuildAllForTreatment(repo, "V2");
        Assert.Equal(31, requests.Count);
        var requestsByKey = requests.ToDictionary(value => $"{value.Source.DocumentId}|{value.PackId}|{value.Anchor}", StringComparer.Ordinal);
        var retryBase = requests.Single(value => value.Source.DocumentId == "DOC-0252" && value.Anchor == "O66");
        var rebuiltRetry = V5P6TH2CEvidenceCompletePreflightTests.BuildArmBRequest(repo, retryBase);
        var frozenRetryPlan = executionRows.Single(row => row.GetProperty("Arm").GetString() == "B" &&
            row.GetProperty("DocumentId").GetString() == "DOC-0252" && row.GetProperty("Anchor").GetString() == "O66");
        Assert.Equal(frozenRetryPlan.GetProperty("providerBodySha256").GetString(), rebuiltRetry.ProviderBodySha256);
        Assert.Equal(frozenRetryPlan.GetProperty("userMessageSha256").GetString(), rebuiltRetry.UserMessageSha256);
        Assert.Equal(frozenRetryPlan.GetProperty("systemPromptSha256").GetString(), Hash(rebuiltRetry.SystemPrompt));
        Assert.Equal(rebuiltRetry.ProviderBodySha256, retryRawRoot.GetProperty("providerBodySha256").GetString());
        Assert.Equal(rebuiltRetry.UserMessageSha256, retryRawRoot.GetProperty("userMessageSha256").GetString());
        Assert.True(V5P6TH2CEvidenceCompletePreflightTests.TryParseEndPointer(retryRawRoot.GetProperty("rawResponse").GetString()!,
            rebuiltRetry.OccurrenceHandles, rebuiltRetry.Anchor));
        var aliasMaps = Sources.ToDictionary(source => source.DocumentId, BuildOccurrenceAliases, StringComparer.Ordinal);
        // Gold access begins only after the raw response files and their capture-freeze hashes above pass.
        var gold = Sources.ToDictionary(source => source.DocumentId, LoadGold, StringComparer.Ordinal);
        var pairs = new List<PairScore>(31);
        foreach (var pair in executionRows.Chunk(2))
        {
            Assert.Equal(2, pair.Length);
            var aPlan = pair[0]; var bPlan = pair[1];
            Assert.Equal("A", aPlan.GetProperty("Arm").GetString());
            Assert.Equal("B", bPlan.GetProperty("Arm").GetString());
            var documentId = aPlan.GetProperty("DocumentId").GetString()!;
            var packId = aPlan.GetProperty("PackId").GetString()!;
            var anchor = aPlan.GetProperty("Anchor").GetString()!;
            Assert.Equal(documentId, bPlan.GetProperty("DocumentId").GetString());
            Assert.Equal(packId, bPlan.GetProperty("PackId").GetString());
            Assert.Equal(anchor, bPlan.GetProperty("Anchor").GetString());
            var key = $"{documentId}|{packId}|{anchor}";
            var request = requestsByKey[key];
            var issued = request.IssuedOccurrences;
            Assert.Equal(aPlan.GetProperty("occurrenceHandlesSha256").GetString(), Hash(string.Join("\n", issued) + "\n"));
            var anchorAlias = request.AnchorAlias;
            var expectedGold = gold[documentId].Extents.SingleOrDefault(extent => extent.Length > 0 && extent[0] == anchorAlias);
            if (expectedGold is not null)
            {
                Assert.Equal(anchorAlias, expectedGold[0]);
                Assert.Equal(expectedGold, issued.Take(expectedGold.Length).Select(id => aliasMaps[documentId][id]).ToArray());
            }
            var aRaw = rawByKey[$"A|{key}"].RootElement;
            var bPrimaryRaw = rawByKey[$"B|{key}"].RootElement;
            var bUsedRetry = documentId == "DOC-0252" && anchor == "O66";
            var bRaw = bUsedRetry ? retryRawRoot : bPrimaryRaw;
            var aDecision = aRaw.GetProperty("contractStatus").GetString() == "VALID" && TryParse(aRaw.GetProperty("rawResponse").GetString()!, anchor, issued, out var parsedA) ? parsedA : null;
            var bDecision = bRaw.GetProperty("contractStatus").GetString() == "VALID" && TryParse(bRaw.GetProperty("rawResponse").GetString()!, anchor, issued, out var parsedB) ? parsedB : null;
            var aScore = Score(aDecision, expectedGold, issued, aliasMaps[documentId]);
            var bScore = Score(bDecision, expectedGold, issued, aliasMaps[documentId]);
            pairs.Add(new PairScore(documentId, packId, anchor, anchorAlias, expectedGold?.ToArray() ?? [], aScore, bScore,
                Transition(aScore, bScore), bUsedRetry,
                bPrimaryRaw.GetProperty("transportError").ValueKind == JsonValueKind.String ? bPrimaryRaw.GetProperty("transportError").GetString() : null,
                aRaw.GetProperty("providerBodySha256").GetString()!, bRaw.GetProperty("providerBodySha256").GetString()!,
                NullableInt(aRaw, "reasoningTokens"), NullableInt(bRaw, "reasoningTokens"), NullableInt(aRaw, "promptTokens"), NullableInt(bRaw, "promptTokens"),
                NullableInt(aRaw, "completionTokens"), NullableInt(bRaw, "completionTokens")));
        }

        Assert.Equal(31, pairs.Count);
        var trueAnchors = pairs.Where(row => row.GoldExtent.Length > 0).ToArray();
        var falseAnchors = pairs.Where(row => row.GoldExtent.Length == 0).ToArray();
        Assert.Equal(27, trueAnchors.Length);
        Assert.Equal(4, falseAnchors.Length);
        var aggregateA = Aggregate(trueAnchors.Select(row => row.A).ToArray());
        var aggregateB = Aggregate(trueAnchors.Select(row => row.B).ToArray());
        var transitions = trueAnchors.GroupBy(row => row.PairedTransition, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        foreach (var name in new[] { "A_WRONG_TO_B_RIGHT", "A_RIGHT_TO_B_WRONG", "BOTH_RIGHT", "BOTH_WRONG" }) transitions.TryAdd(name, 0);
        var discordant = transitions["A_WRONG_TO_B_RIGHT"] + transitions["A_RIGHT_TO_B_WRONG"];

        FreezeArtifact.AssertJson(OutputRoot, "h2c-evidence-paired-gold-score.v1.json", new
        {
            schemaVersion = "v5-p6th2c-evidence-paired-gold-score-v1",
            status = "PAIRED_GOLD_SCORE_RAW_FROZEN_ONE_AUTHORIZED_SAME_BODY_RETRY",
            authority = new
            {
                preflightSha256 = Hash(preflightBytes),
                executionManifestSha256 = Hash(executionBytes),
                captureFreezeSha256 = Hash(freezeBytes),
                resultSha256 = Hash(resultBytes),
                retryReservationSha256 = Hash(retryReservationBytes),
                retryRawCaptureSha256 = Hash(retryRawBytes),
                retryResultSha256 = Hash(retryResultBytes),
                goldAuthorities = Sources.Select(source => new { documentId = source.DocumentId, canonicalGoldPath = FrozenHistoryGold.Entry(source.DocumentId).CanonicalGoldPath,
                    goldSha256 = gold[source.DocumentId].GoldSha256, sourceSha256 = gold[source.DocumentId].SourceSha256 }).ToArray(),
                goldMutation = "NONE",
                providerCallsDuringAudit = 0,
                runtimeChanged = false,
            },
            experiment = new
            {
                arms = new[] { "A_CURRENT_H2C_EVIDENCE_PLUS_COORDINATE_OUTPUT_CLARIFICATION", "B_A_PLUS_NEUTRAL_GEOMETRY_BLOCK_ID_RICH_TYPOGRAPHY_AND_GAP_FACTS" },
                onlyA_BDifference = "MODEL_VISIBLE_EVIDENCE_PROJECTION",
                notByteIdenticalToHistoricalV2 = true,
                developmentOnly = true,
                heldOutGeneralization = "NOT_ESTABLISHED",
                componentCausality = "NOT_IDENTIFIABLE_FROM_COMPOSITE_EVIDENCE_ARM",
            },
            captureIntegrity = new
            {
                primaryCalls = 62,
                callsPerArm = 31,
                primaryValidContractA = 31,
                primaryValidContractB = 30,
                authorizedRetryCalls = 1,
                totalProviderCalls = 63,
                retryTarget = "ARM_B_DOC-0252_O66",
                retryBodyByteEquivalent = true,
                retryResponseContractValid = true,
                scoredValidContractA = pairs.Count(row => row.A.ContractValid),
                scoredValidContractB = pairs.Count(row => row.B.ContractValid),
                primaryUpstreamTransportFailures = 1,
                retryUpstreamTransportFailures = 0,
                upstream429 = "ARM_B_DOC-0252_O66",
                transportRetries = 0,
                repair = false,
                fallback = false,
            },
            cohort = new { preregisteredAnchors = pairs.Count, trueGoldAnchors = trueAnchors.Length, falseG2AAnchors = falseAnchors.Length,
                falseAnchorPolicy = "ALL_31_RETAINED_IN_CAPTURE; SCORED_ONLY_AS_DIAGNOSTICS; EXCLUDED_FROM_TRUE_HEADING_DENOMINATORS" },
            score = new { A = aggregateA, B = aggregateB },
            pairedContingency = new
            {
                aWrongToBCorrect = transitions["A_WRONG_TO_B_RIGHT"],
                aCorrectToBWrong = transitions["A_RIGHT_TO_B_WRONG"],
                bothCorrect = transitions["BOTH_RIGHT"],
                bothWrong = transitions["BOTH_WRONG"],
                discordantPairs = discordant,
                mcnemarExactTwoSidedP = McNemarExactTwoSided(transitions["A_RIGHT_TO_B_WRONG"], transitions["A_WRONG_TO_B_RIGHT"]),
            },
            falseAnchorDiagnostics = falseAnchors.Select(row => new { row.DocumentId, row.PackId, row.Anchor, row.AnchorAlias, armA = row.A, armB = row.B }).ToArray(),
            retryPolicy = "ONE_EXACT_SAME_BODY_RETRY_FOR_THE_ONLY_PRIMARY_NO_RESPONSE; USE_VALID_RETRY_RESPONSE_FOR_B_SCORE; PRESERVE_PRIMARY_429_AND_RAW_CAPTURE_FREEZE",
            firstOutsideRoleScoring = "ROLE_IS_RECORDED_BUT_ACCURACY_NOT_EVALUABLE_WITHOUT_GOLD_ROLE_LABELS; IMMEDIATE_SUCCESSOR_IDENTITY_IS_SCORED",
            rows = pairs,
            interpretation = new
            {
                exactBoundary = "predicted contiguous source extent equals all Gold sourceParts in exact order",
                signedDistance = "predicted member count minus Gold part count; positive over, negative under",
                firstOutsideExact = "predicted immediate successor occurrence identity equals the Gold next issued occurrence or null at request end",
                maxClaim = "EVIDENCE_COMPLETE_PROJECTION_EFFECT_ONLY; no geometry/typography/block-ID component attribution or held-out generalization",
            },
        });
    }

    [Fact]
    public void Sanitized_score_receipt_is_self_consistent_without_raw_provider_archive()
    {
        var preflightBytes = File.ReadAllBytes(TestRepository.Path(PreflightRoot + "/h2c-evidence-complete-preflight.v1.json"));
        var executionBytes = File.ReadAllBytes(TestRepository.Path(PreflightRoot + "/execution-manifest.v1.json"));
        var freezeBytes = File.ReadAllBytes(TestRepository.Path(CaptureRoot + "/capture-freeze.v1.json"));
        var resultBytes = File.ReadAllBytes(TestRepository.Path(CaptureRoot + "/result.v1.json"));
        AssertSanitizedScoreArtifact(preflightBytes, executionBytes, freezeBytes, resultBytes);
    }

    private static void AssertSanitizedScoreArtifact(byte[] preflightBytes, byte[] executionBytes,
        byte[] freezeBytes, byte[] resultBytes)
    {
        var artifactPath = TestRepository.Path(OutputRoot + "/h2c-evidence-paired-gold-score.v1.json");
        var retryResultPath = TestRepository.Path(RetryRoot + "/result.v1.json");
        var retryReservationPath = TestRepository.Path(RetryRoot + "/execution-reservation.v1.json");
        Assert.True(File.Exists(artifactPath) && File.Exists(retryResultPath) && File.Exists(retryReservationPath));
        var retryResultBytes = File.ReadAllBytes(retryResultPath);
        var retryReservationBytes = File.ReadAllBytes(retryReservationPath);
        using var artifact = JsonDocument.Parse(File.ReadAllBytes(artifactPath));
        using var retryResult = JsonDocument.Parse(retryResultBytes);
        using var retryReservation = JsonDocument.Parse(retryReservationBytes);
        var root = artifact.RootElement;
        var authority = root.GetProperty("authority");
        Assert.Equal("PAIRED_GOLD_SCORE_RAW_FROZEN_ONE_AUTHORIZED_SAME_BODY_RETRY", root.GetProperty("status").GetString());
        Assert.Equal(Hash(preflightBytes), authority.GetProperty("preflightSha256").GetString());
        Assert.Equal(Hash(executionBytes), authority.GetProperty("executionManifestSha256").GetString());
        Assert.Equal(Hash(freezeBytes), authority.GetProperty("captureFreezeSha256").GetString());
        Assert.Equal(Hash(resultBytes), authority.GetProperty("resultSha256").GetString());
        Assert.Equal(Hash(retryResultBytes), authority.GetProperty("retryResultSha256").GetString());
        Assert.Equal(Hash(retryReservationBytes), authority.GetProperty("retryReservationSha256").GetString());
        Assert.Equal("RETRY_RESPONSE_CONTRACT_VALID", retryResult.RootElement.GetProperty("status").GetString());
        Assert.Equal(63, retryResult.RootElement.GetProperty("totalCalls").GetInt32());
        Assert.True(retryResult.RootElement.GetProperty("contractValid").GetBoolean());

        var rows = root.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(31, rows.Length);
        Assert.Equal(27, rows.Count(row => row.GetProperty("GoldExtent").GetArrayLength() > 0));
        Assert.Equal(4, rows.Count(row => row.GetProperty("GoldExtent").GetArrayLength() == 0));
        var retryScoredRow = Assert.Single(rows, row => row.GetProperty("BUsedAuthorizedRetry").GetBoolean());
        Assert.Equal("DOC-0252", retryScoredRow.GetProperty("DocumentId").GetString());
        Assert.Equal("O66", retryScoredRow.GetProperty("Anchor").GetString());
        foreach (var goldAuthority in authority.GetProperty("goldAuthorities").EnumerateArray())
        {
            var entry = FrozenHistoryGold.Entry(goldAuthority.GetProperty("documentId").GetString()!);
            Assert.Equal(entry.GoldSha256, goldAuthority.GetProperty("goldSha256").GetString());
            Assert.Equal(entry.SourceSha256, goldAuthority.GetProperty("sourceSha256").GetString());
        }
        Assert.Equal(0, root.GetProperty("authority").GetProperty("providerCallsDuringAudit").GetInt32());
        Assert.Equal("NONE", root.GetProperty("authority").GetProperty("goldMutation").GetString());
        Assert.False(root.GetProperty("authority").GetProperty("runtimeChanged").GetBoolean());

        var trueRows = rows.Where(row => row.GetProperty("GoldExtent").GetArrayLength() > 0).ToArray();
        AssertAggregate(root.GetProperty("score").GetProperty("A"), trueRows, "A");
        AssertAggregate(root.GetProperty("score").GetProperty("B"), trueRows, "B");
        var transitions = trueRows.GroupBy(row => row.GetProperty("PairedTransition").GetString()!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var paired = root.GetProperty("pairedContingency");
        Assert.Equal(transitions.GetValueOrDefault("A_WRONG_TO_B_RIGHT"), paired.GetProperty("aWrongToBCorrect").GetInt32());
        Assert.Equal(transitions.GetValueOrDefault("A_RIGHT_TO_B_WRONG"), paired.GetProperty("aCorrectToBWrong").GetInt32());
        Assert.Equal(transitions.GetValueOrDefault("BOTH_RIGHT"), paired.GetProperty("bothCorrect").GetInt32());
        Assert.Equal(transitions.GetValueOrDefault("BOTH_WRONG"), paired.GetProperty("bothWrong").GetInt32());
    }

    private static void AssertAggregate(JsonElement aggregate, JsonElement[] trueRows, string arm)
    {
        var outcomes = trueRows.Select(row => row.GetProperty(arm).GetProperty("Outcome").GetString()).ToArray();
        Assert.Equal(27, aggregate.GetProperty("denominator").GetInt32());
        Assert.Equal(outcomes.Count(value => value == "EXACT"), aggregate.GetProperty("exact").GetInt32());
        Assert.Equal(outcomes.Count(value => value == "OVEREXTENT"), aggregate.GetProperty("overextent").GetInt32());
        Assert.Equal(outcomes.Count(value => value == "UNDEREXTENT"), aggregate.GetProperty("underextent").GetInt32());
        Assert.Equal(outcomes.Count(value => value == "WRONG_PARTS"), aggregate.GetProperty("wrongParts").GetInt32());
        Assert.Equal(trueRows.Count(row => row.GetProperty(arm).GetProperty("Outcome").GetString() == "EXACT" && row.GetProperty("GoldExtent").GetArrayLength() == 1),
            aggregate.GetProperty("singletonExact").GetInt32());
        Assert.Equal(trueRows.Count(row => row.GetProperty("GoldExtent").GetArrayLength() == 1), aggregate.GetProperty("singletonN").GetInt32());
        Assert.Equal(trueRows.Count(row => row.GetProperty(arm).GetProperty("Outcome").GetString() == "EXACT" && row.GetProperty("GoldExtent").GetArrayLength() > 1),
            aggregate.GetProperty("multipartExact").GetInt32());
        Assert.Equal(trueRows.Count(row => row.GetProperty("GoldExtent").GetArrayLength() > 1), aggregate.GetProperty("multipartN").GetInt32());
        Assert.Equal(trueRows.Count(row => row.GetProperty(arm).GetProperty("FirstOutsideExact").ValueKind == JsonValueKind.True),
            aggregate.GetProperty("firstOutsideExact").GetInt32());
        Assert.Equal(trueRows.Count(row => row.GetProperty(arm).GetProperty("FirstOutsideExact").ValueKind is JsonValueKind.True or JsonValueKind.False),
            aggregate.GetProperty("firstOutsideN").GetInt32());
    }

    private static ArmScore Score(Decision? decision, string[]? expectedGold, IReadOnlyList<string> issued,
        IReadOnlyDictionary<string, string> aliases)
    {
        if (expectedGold is null) return new("FALSE_G2A_ANCHOR", decision is not null, null, null, decision?.FirstOutsideOccurrence,
            decision?.FirstOutsideRole, null, null, null, null, 0);
        if (decision is null) return new("NO_RESPONSE_OR_INVALID_CONTRACT", false, null, null, null, null, false, null, null, null, expectedGold.Length);
        var predictedAliases = decision.Members.Select(id => aliases[id]).ToArray();
        var outcome = Classify(predictedAliases, expectedGold);
        var signed = predictedAliases.Length - expectedGold.Length;
        var expectedOutside = expectedGold.Length < issued.Count ? issued[expectedGold.Length] : null;
        return new(outcome, true, decision.EndOccurrence, aliases[decision.EndOccurrence], decision.FirstOutsideOccurrence,
            decision.FirstOutsideRole, decision.FirstOutsideOccurrence == expectedOutside, signed, Math.Abs(signed), Math.Max(0, signed), expectedGold.Length);
    }

    private static object Aggregate(IReadOnlyList<ArmScore> scores)
    {
        var overrun = scores.Where(score => score.Outcome == "OVEREXTENT").Select(score => score.OverrunDistance!.Value).ToArray();
        var distances = scores.Where(score => score.SignedBoundaryDistance.HasValue).Select(score => score.SignedBoundaryDistance!.Value).ToArray();
        return new
        {
            denominator = scores.Count,
            exact = scores.Count(score => score.Outcome == "EXACT"),
            overextent = scores.Count(score => score.Outcome == "OVEREXTENT"),
            underextent = scores.Count(score => score.Outcome == "UNDEREXTENT"),
            wrongParts = scores.Count(score => score.Outcome == "WRONG_PARTS"),
            missingOrInvalidResponse = scores.Count(score => !score.ContractValid),
            singletonExact = scores.Count(score => score.Outcome == "EXACT" && score.GoldPartCount == 1),
            singletonN = scores.Count(score => score.GoldPartCount == 1),
            multipartExact = scores.Count(score => score.Outcome == "EXACT" && score.GoldPartCount > 1),
            multipartN = scores.Count(score => score.GoldPartCount > 1),
            firstOutsideExact = scores.Count(score => score.FirstOutsideExact == true),
            firstOutsideN = scores.Count(score => score.FirstOutsideExact.HasValue),
            signedBoundaryDistance = new { sum = distances.Sum(), mean = distances.Length == 0 ? 0 : Math.Round(distances.Average(), 3) },
            absoluteBoundaryDistance = new { sum = scores.Where(score => score.AbsoluteBoundaryDistance.HasValue).Sum(score => score.AbsoluteBoundaryDistance!.Value),
                mean = distances.Length == 0 ? 0 : Math.Round(scores.Where(score => score.AbsoluteBoundaryDistance.HasValue).Average(score => score.AbsoluteBoundaryDistance!.Value), 3) },
            overrunDistance = new { total = overrun.Sum(), meanAmongOverextents = overrun.Length == 0 ? 0 : Math.Round(overrun.Average(), 3), max = overrun.Length == 0 ? 0 : overrun.Max() },
        };
    }

    private static string Transition(ArmScore a, ArmScore b)
    {
        var aRight = a.Outcome == "EXACT"; var bRight = b.Outcome == "EXACT";
        return (aRight, bRight) switch
        {
            (false, true) => "A_WRONG_TO_B_RIGHT",
            (true, false) => "A_RIGHT_TO_B_WRONG",
            (true, true) => "BOTH_RIGHT",
            _ => "BOTH_WRONG",
        };
    }

    private static string Classify(IReadOnlyList<string> predicted, IReadOnlyList<string> expected)
    {
        if (predicted.SequenceEqual(expected, StringComparer.Ordinal)) return "EXACT";
        if (predicted.Count < expected.Count && predicted.SequenceEqual(expected.Take(predicted.Count), StringComparer.Ordinal)) return "UNDEREXTENT";
        if (predicted.Count > expected.Count && expected.SequenceEqual(predicted.Take(expected.Count), StringComparer.Ordinal)) return "OVEREXTENT";
        return "WRONG_PARTS";
    }

    private static GoldDocument LoadGold(P6TH2CEndPointerCanary.Source source)
    {
        var entry = FrozenHistoryGold.Entry(source.DocumentId);
        FrozenHistoryGold.RequireCapability(source.DocumentId, GoldCapability.Occurrence);
        using var document = FrozenHistoryGold.Resolve(source.DocumentId);
        Assert.Equal(entry.SourceSha256, document.RootElement.GetProperty("source").GetProperty("sourceSha256").GetString());
        var extents = document.RootElement.GetProperty("semantic").GetProperty("claims").EnumerateArray()
            .Select(claim => claim.GetProperty("sourceParts").EnumerateArray().Select(part => part.GetProperty("sourceAlias").GetString()!).ToArray()).ToArray();
        return new(entry.GoldSha256, entry.SourceSha256, extents);
    }

    private static IReadOnlyDictionary<string, string> BuildOccurrenceAliases(P6TH2CEndPointerCanary.Source source)
    {
        var sourceHash = CanonicalSemanticSourceHash.Compute(TestRepository.Path(source.PdfPath));
        var plan = PdfCandidateAuthorityQualificationAdapter.PrepareFromSnapshot(TestRepository.Path($"{SnapshotRoot}/{sourceHash}.json"), source.DocumentId);
        using var capture = JsonDocument.Parse(File.ReadAllBytes(TestRepository.Path($"{Root}/{source.F1Path}")));
        var f1Row = source.Kind switch
        {
            P6TH2CEndPointerCanary.F1Kind.RawCapture => capture.RootElement,
            P6TH2CEndPointerCanary.F1Kind.ResultRow => capture.RootElement.GetProperty("row"),
            P6TH2CEndPointerCanary.F1Kind.ResultRows => capture.RootElement.GetProperty("rows").EnumerateArray().Single(value => value.GetProperty("documentId").GetString() == source.DocumentId),
            _ => throw new InvalidDataException("h2c-evidence-score-f1-kind-invalid"),
        };
        var pack = plan.Packs.Single(value => value.PackId == f1Row.GetProperty("packId").GetString());
        var correspondences = source.F1UsedCorrespondences ? BuildCorrespondences(pack) : new Dictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>>(StringComparer.Ordinal);
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
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentOutOfRangeException) { return false; }
    }

    private static int? NullableInt(JsonElement row, string property) => row.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : null;

    private static double McNemarExactTwoSided(int aRightBWrong, int aWrongBRight)
    {
        var n = aRightBWrong + aWrongBRight;
        if (n == 0) return 1;
        var tailK = Math.Min(aRightBWrong, aWrongBRight);
        var probability = Math.Pow(0.5, n);
        var term = probability;
        var cumulative = term;
        for (var k = 1; k <= tailK; k++)
        {
            term *= (double)(n - k + 1) / k;
            cumulative += term;
        }
        return Math.Min(1, 2 * cumulative);
    }

    private static string Hash(string value) => Hash(Encoding.UTF8.GetBytes(value));
    private static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
}
