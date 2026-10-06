using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Core;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Tests;

/// <summary>Provider-free G2 preflight. It freezes eligible local candidate menus, not model results.</summary>
public sealed class V5P6TG2ExactExtentResolverPreflightTests
{
    private const string SnapshotRoot = "eval/a99-closed-loop/pdf-canonical-source-v1";
    private const string F1Root = "artifacts/v5-p6t-function-membership/p6tf1-preflight";
    private const string G1Path = "artifacts/v5-p6t-function-membership/p6tg1-candidate-projection/function-conditioned-candidate-projection.v1.json";
    private const string OutputRoot = "artifacts/v5-p6t-function-membership/p6tg2-exact-extent-preflight";
    private const string CaptureRoot = "artifacts/v5-p6t-function-membership/p6tg2-canary-20261004";
    private const string GoldAuditRoot = "artifacts/v5-p6t-function-membership/p6tg2-canary-gold-audit";
    private const string AnchorExistencePreflightRoot = "artifacts/v5-p6t-function-membership/p6tg2a-anchor-existence-preflight";
    private const string AnchorExistenceCaptureRoot = "artifacts/v5-p6t-function-membership/p6tg2a-anchor-existence-canary-20261004";
    private const string ExactExtentPreflightRoot = "artifacts/v5-p6t-function-membership/p6tg2b-exact-extent-preflight";
    private const string ExactExtentCaptureRoot = "artifacts/v5-p6t-function-membership/p6tg2b-exact-extent-canary-20261004";
    private const string ExactExtentAuditRoot = "artifacts/v5-p6t-function-membership/p6tg2b-exact-extent-gold-audit";
    private const string ExactExtentOrderPreflightRoot = "artifacts/v5-p6t-function-membership/p6tg2c-candidate-order-preflight";
    private const string ExactExtentOrderCaptureRoot = "artifacts/v5-p6t-function-membership/p6tg2c-candidate-order-canary-20261004";
    private const string ExactExtentOrderAuditRoot = "artifacts/v5-p6t-function-membership/p6tg2c-candidate-order-gold-audit";
    private const string ExactExtentPermutationPreflightRoot = "artifacts/v5-p6t-function-membership/p6tg2d-permutation-preflight";
    private const string ExactExtentPermutationCaptureRoot = "artifacts/v5-p6t-function-membership/p6tg2d-permutation-canary-20261004";
    private const string ExactExtentPermutationAuditRoot = "artifacts/v5-p6t-function-membership/p6tg2d-permutation-gold-audit";
    private const string ExactExtentWholeMovePreflightRoot = "artifacts/v5-p6t-function-membership/p6tg2e-whole-move-preflight";
    private const string ExactExtentWholeMoveCaptureRoot = "artifacts/v5-p6t-function-membership/p6tg2e-whole-move-canary-20261004";
    private const string ExactExtentMenuForensicRoot = "artifacts/v5-p6t-function-membership/p6tg2-menu-forensic";
    private const string IndependentJudgmentPreflightRoot = "artifacts/v5-p6t-function-membership/p6th1-independent-candidate-preflight";
    private const string IndependentJudgmentCaptureRoot = "artifacts/v5-p6t-function-membership/p6th1-independent-candidate-canary-20261004";
    private const string IndependentJudgmentAggregationRoot = "artifacts/v5-p6t-function-membership/p6th1-independent-candidate-aggregation";
    private const string IndependentJudgmentGoldForensicRoot = "artifacts/v5-p6t-function-membership/p6th1-independent-candidate-gold-forensic";
    private const string ContinuationBoundaryPreflightRoot = "artifacts/v5-p6t-function-membership/p6th2-function-conditioned-continuation-preflight";
    private const string ContinuationBoundaryCaptureRoot = "artifacts/v5-p6t-function-membership/p6th2-function-conditioned-continuation-canary-20261004";
    private const string ContinuationBoundaryGoldAuditRoot = "artifacts/v5-p6t-function-membership/p6th2-function-conditioned-continuation-gold-audit";
    private const string ContinuationCompositionAuditRoot = "artifacts/v5-p6t-function-membership/p6th21-anchor-continuation-composition-audit";
    private const string CrossDocumentShapeAuditRoot = "artifacts/v5-p6t-function-membership/p6th3-cross-document-shape-audit";
    private const string Gold089Path = "eval/a99-closed-loop/gold/SRC-089.gold.json";
    private const string Gold095Path = "eval/a99-closed-loop/gold/SRC-095.gold.json";
    private const string Review095Path = "eval/a99-closed-loop/source-review-v1/SRC-095/review-items.json";
    private const string RunVariable = "A99_RUN_P6TG2_CANARY";
    private const string RunAnchorExistenceVariable = "A99_RUN_P6TG2A_CANARY";
    private const string RunExactExtentVariable = "A99_RUN_P6TG2B_CANARY";
    private const string RunExactExtentOrderVariable = "A99_RUN_P6TG2C_CANARY";
    private const string RunExactExtentPermutationVariable = "A99_RUN_P6TG2D_CANARY";
    private const string RunExactExtentWholeMoveVariable = "A99_RUN_P6TG2E_CANARY";
    private const string RunIndependentJudgmentVariable = "A99_RUN_P6TH1_CANARY";
    private const string RunContinuationBoundaryVariable = "A99_RUN_P6TH2_CANARY";
    private const string Protocol = "v5-function-conditioned-exact-extent-resolver-preflight-1";
    private const string IndependentJudgmentProtocol = "v5-independent-exact-extent-judgment-1";
    private const string ContinuationBoundaryProtocol = "v5-function-conditioned-continuation-boundary-1";
    private static readonly (string Id, string Pdf)[] Documents =
    [
        ("SRC-089", SourcePdfCorpus.Src089),
        ("SRC-095", SourcePdfCorpus.Src095),
    ];

    private sealed record PreparedDocument(
        string DocumentId,
        PdfCandidateAuthorityDocumentPlan Plan,
        PdfCandidateAuthorityPreparedPack SourcePack,
        PdfFunctionMembershipPreparedPackF1 F1Pack,
        IReadOnlyDictionary<string, string> FunctionByAlias,
        IReadOnlyDictionary<string, string> OccurrenceByAlias,
        IReadOnlyDictionary<string, ProjectionClass> CandidateClasses);

    private enum ProjectionClass { ALL_ESTABLISHES, MIXED, NO_ESTABLISHES }

    [Fact]
    public void P6TG2_preflight_freezes_local_function_conditioned_extent_menus_and_excludes_representation_only_candidates()
    {
        using var f1Primary = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{F1Root}/result.v1.json")));
        using var f1Retry = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{F1Root}/retry-src089-result.v1.json")));
        using var g1 = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(G1Path)));
        using var anchorAudit = JsonDocument.Parse(File.ReadAllText(TestRepository.Path("artifacts/v5-p6t-total-occurrence-role/p6tb-anchor-role-audit/anchor-role-audit.v1.json")));

        var g1Root = g1.RootElement;
        Assert.Equal("v5-p6tg1-function-conditioned-candidate-projection-v1", g1Root.GetProperty("schemaVersion").GetString());
        Assert.Equal(0, g1Root.GetProperty("execution").GetProperty("providerCalls").GetInt32());
        Assert.Equal("NONE", g1Root.GetProperty("execution").GetProperty("goldMutation").GetString());
        Assert.Equal("UNCHANGED", g1Root.GetProperty("execution").GetProperty("sharedRuntime").GetString());

        var p089 = Prepare("SRC-089", SourcePdfCorpus.Src089, f1Retry.RootElement.GetProperty("row"), g1Root.GetProperty("src089"));
        var p095 = Prepare("SRC-095", SourcePdfCorpus.Src095,
            f1Primary.RootElement.GetProperty("rows").EnumerateArray().Single(row => row.GetProperty("documentId").GetString() == "SRC-095"),
            g1Root.GetProperty("src095"));
        var tocAliases = anchorAudit.RootElement.GetProperty("src095").GetProperty("rows").EnumerateArray()
            .Select(row => row.GetProperty("occurrence").GetString()!)
            .Select(id => p095.F1Pack.Request.Occurrences.Single(item => item.Id == id).Atom.Alias)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(53, tocAliases.Count);
        Assert.All(tocAliases, alias => Assert.Equal("REPRESENTS_STRUCTURE", p095.FunctionByAlias[alias]));
        var tocOccurrenceIds = tocAliases.Select(alias => p095.OccurrenceByAlias[alias]).ToHashSet(StringComparer.Ordinal);

        var src089 = Compose(p089, new HashSet<string>(StringComparer.Ordinal));
        var src095 = Compose(p095, tocAliases);
        Assert.DoesNotContain(src095.OccurrenceGroups.SelectMany(group => group.CandidateIds), id =>
            p095.SourcePack.Universe.Candidates.Single(candidate => candidate.Id == id).Endpoint.Parts.Any(part => tocAliases.Contains(part.Alias)));
        Assert.DoesNotContain(tocOccurrenceIds, id => src095.UserMessage.Contains($"\"{id}\"", StringComparison.Ordinal));
        Assert.All(src089.OccurrenceGroups.Concat(src095.OccurrenceGroups), group => Assert.NotEmpty(group.CandidateIds));

        var gold089 = g1Root.GetProperty("src089").GetProperty("goldExtents").EnumerateArray().ToArray();
        Assert.Equal(6, gold089.Length);
        var goldProbeRows = gold089.Select(gold =>
        {
            var identity = gold.GetProperty("goldIdentity").GetString()!;
            var candidate = p089.SourcePack.Universe.Candidates.Single(value => value.SpanIdentity == identity);
            var group = src089.OccurrenceGroups.Single(value => value.PrimaryAlias == candidate.Endpoint.Parts[0].Alias);
            Assert.Contains(candidate.Id, group.CandidateIds);
            return new
            {
                primary = group.PrimaryOccurrence,
                primaryAlias = group.PrimaryAlias,
                exactCandidate = candidate.Id,
                exactCandidateKind = candidate.Kind.ToString(),
                eligibleOptionsAtPrimary = group.CandidateIds.Count,
                exactCandidateIncluded = true,
            };
        }).ToArray();

        var frontMatterRows = new[] { "L0002:S0", "L0002:S1", "L0003:S1" }.Select(alias =>
        {
            var group = src089.OccurrenceGroups.Single(value => value.PrimaryAlias == alias);
            var g1TouchedCount = g1Root.GetProperty("src089").GetProperty("falseEstablishAtoms").EnumerateArray()
                .Single(value => value.GetProperty("alias").GetString() == alias)
                .GetProperty("eligibleCandidateCountTouchingAtom").GetInt32();
            return new { primary = group.PrimaryOccurrence, primaryAlias = alias, noStructuralExtentIsAllowed = true,
                eligibleCandidatesSharingThisPrimary = group.CandidateIds.Count,
                g1EligibleCandidatesTouchingAtom = g1TouchedCount, candidateIds = group.CandidateIds };
        }).ToArray();
        var g1FrontMatterCandidateIds = g1Root.GetProperty("src089").GetProperty("frontMatterSurvivingCandidates").EnumerateArray()
            .Select(candidate => candidate.GetProperty("candidateId").GetString()!).ToHashSet(StringComparer.Ordinal);
        var g2FrontMatterCandidateIds = frontMatterRows.SelectMany(row => row.candidateIds).ToHashSet(StringComparer.Ordinal);
        Assert.True(g1FrontMatterCandidateIds.SetEquals(g2FrontMatterCandidateIds), "G1/G2 distinct front-matter candidate union mismatch");

        var reviewedToc = g1Root.GetProperty("src095").GetProperty("tocRows").EnumerateArray().ToArray();
        Assert.Equal(53, reviewedToc.Length);
        Assert.Equal(0, g1Root.GetProperty("answers").GetProperty("src095EligibleCandidateLeakageCount").GetInt32());

        FreezeArtifact.AssertJson(OutputRoot, "exact-extent-resolver-preflight.v1.json", new
        {
            schemaVersion = "v5-p6tg2-exact-extent-resolver-preflight-v1",
            status = "PREPARED_NOT_AUTHORIZED",
            protocolVersion = Protocol,
            treatment = new
            {
                model = "qwen/qwen3.7-flash",
                provider = "alibaba",
                reasoning = new { enabled = true, effort = "OMITTED" },
                promptAxis = "LOCAL_EXACT_EXTENT_SELECTION_OR_NO_STRUCTURAL_EXTENT",
                noPromptTuning = true,
                noGoldInModelInput = true,
            },
            outputContract = new
            {
                shape = "{\"decisions\":[{\"primary\":\"O27\",\"candidate\":\"C123\"},{\"primary\":\"O28\",\"candidate\":\"NO_STRUCTURAL_EXTENT\"}]}",
                oneDecisionPerIssuedPrimary = true,
                candidateMustBeIssuedForThatPrimary = true,
                noStructuralExtentAllowed = true,
                modelAuthoredTextOrCoordinates = false,
                exactIdentityResolution = "harness maps selected C# to frozen P6S endpoint identity",
                invalidDecisionPolicy = "decision-local quarantine; never repair or infer candidate",
            },
            inputInvariants = new
            {
                candidates = "ALL_ESTABLISHES only; alternatives share the same primary O#",
                sourceEvidence = "candidate exact text plus ordered source-part text and read-only function label",
                localContext = "at most nearest one preceding and nearest one following owned occurrence, read-only; representation-only occurrences omitted",
                contextOnlySelectable = false,
                tocReviewedAliasesInSrc095Input = 0,
                representationOnlyCandidatesInEitherInput = 0,
                relationsIncluded = false,
                goldExtentOrGoldLabelInModelInput = false,
            },
            sourceAuthority = new
            {
                g1ArtifactSha256 = Hashing.Sha256(File.ReadAllText(TestRepository.Path(G1Path))),
                src089CandidateUniverseFingerprint = p089.SourcePack.Universe.Fingerprint,
                src095CandidateUniverseFingerprint = p095.SourcePack.Universe.Fingerprint,
                src089FunctionRequestSha256 = p089.F1Pack.Request.UserMessageSha256,
                src095FunctionRequestSha256 = p095.F1Pack.Request.UserMessageSha256,
            },
            probes = new
            {
                src089ReviewedGoldExtents = goldProbeRows,
                src089FalseEstablishFrontMatter = frontMatterRows,
                src095ReviewedToc = new
                {
                    count = reviewedToc.Length,
                    appearsAsPrimary = reviewedToc.Count(row => src095.OccurrenceGroups.Any(group => group.PrimaryOccurrence == row.GetProperty("occurrence").GetString())),
                    selectableCandidateLeakage = 0,
                reviewedTocSourceIdentitiesAbsentFromSelectableCandidatesAndContext = true,
                },
            },
            callPlan = new[] { Describe(src089), Describe(src095) },
            budget = new
            {
                maximumAuthorizedProviderCalls = 0,
                providerCalls = 0,
                retries = 0,
                repairs = 0,
                fallbacks = 0,
                goldMutation = "NONE",
                runtimeChanged = false,
            },
            conclusion = "PROVIDER_FREE_G2_PREFLIGHT_FROZEN; EXACT_EXTENT_RESOLVER_NOT_EXECUTED; AWAIT_SEPARATE_PROVIDER_AUTHORIZATION",
        });
    }

    [Fact]
    public async Task Run_exactly_one_frozen_whole_move_call_only_when_explicitly_enabled_for_P6TG2E()
    {
        if (Environment.GetEnvironmentVariable(RunExactExtentWholeMoveVariable) is not ("1" or "true" or "TRUE")) return;

        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        Assert.False(string.IsNullOrWhiteSpace(apiKey), "OPENROUTER_API_KEY is required for the explicitly enabled P6T-G2E canary.");
        var capturePath = TestRepository.Path(ExactExtentWholeMoveCaptureRoot);
        Assert.False(Directory.Exists(capturePath), "P6T-G2E capture root already exists; automatic resume or resend is forbidden.");

        using var f1Retry = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{F1Root}/retry-src089-result.v1.json")));
        using var g1 = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(G1Path)));
        using var g2aRaw = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{AnchorExistenceCaptureRoot}/SRC-089.raw-capture.v1.json")));
        using var preflight = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{ExactExtentWholeMovePreflightRoot}/whole-move-preflight.v1.json")));
        var prepared = Prepare("SRC-089", SourcePdfCorpus.Src089, f1Retry.RootElement.GetProperty("row"), g1.RootElement.GetProperty("src089"));
        var anchorRequest = ComposeAnchorExistence(prepared);
        var anchorLedger = ParseAnchorLedger(anchorRequest, g2aRaw.RootElement.GetProperty("rawResponse").GetString()!);
        var hasPrimaries = anchorLedger.Decisions.Select(item => JsonDocument.Parse(JsonSerializer.Serialize(item)))
            .Where(item => item.RootElement.GetProperty("anchor").GetString() == "HAS_STRUCTURAL_EXTENT")
            .Select(item => item.RootElement.GetProperty("primary").GetString()!).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(6, hasPrimaries.Count);
        var baseline = ComposeExactExtentFromHas(prepared, hasPrimaries);
        var request = ComposeExactExtentWithWholeMoved(prepared, baseline);
        var callPlan = preflight.RootElement.GetProperty("arm");
        Assert.Equal(request.MessageHash, callPlan.GetProperty("userMessageSha256").GetString());
        Assert.Equal(request.MessageBytes, callPlan.GetProperty("userMessageUtf8Bytes").GetInt32());
        Assert.Equal(request.ProviderHash, callPlan.GetProperty("providerBodySha256").GetString());
        Assert.Equal(request.ProviderBytes, callPlan.GetProperty("providerBodyBytes").GetInt32());

        Directory.CreateDirectory(capturePath);
        WriteNew(Path.Combine(capturePath, "execution-reservation.v1.json"), new
        {
            schemaVersion = "v5-p6tg2e-execution-reservation-v1",
            status = "ONE_PRIMARY_SLOT_RESERVED",
            documentId = request.DocumentId,
            providerRequestHash = request.ProviderHash,
            providerRequestBytes = request.ProviderBytes,
            providerCallsBeforeSend = 0,
            retriesAllowed = 0,
            repairsAllowed = false,
            fallbacksAllowed = false,
            goldRead = false,
        });

        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var provider = new OpenRouterQualificationTransport(http, new RemoteInferenceOptions
        {
            ApiKey = apiKey!,
            Model = "qwen/qwen3.7-flash",
            OpenRouterProviderRoute = "Alibaba",
            TransientRequestRetries = 0,
            MaxParallelRequests = 1,
            ProviderTransportTimeoutSeconds = 300,
            RequireZeroDataRetention = false,
        });

        try
        {
            var observation = await provider.ExecuteObservedAsync(request.ProviderBody, request.MaxCompletionTokens,
                request.SystemPrompt, request.UserMessage, CancellationToken.None);
            Assert.Equal(0, observation.RetryCount);
            var rawCapture = new
            {
                schemaVersion = "v5-p6tg2e-raw-provider-capture-v1",
                documentId = request.DocumentId,
                packId = "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_001",
                provider = "OpenRouter",
                model = "qwen/qwen3.7-flash",
                providerRoute = "Alibaba",
                reasoningRequested = true,
                reasoningEffort = "OMITTED",
                providerRequestHash = request.ProviderHash,
                providerRequestBytes = request.ProviderBytes,
                semanticRequestHash = request.MessageHash,
                finishReason = observation.FinishReason,
                usage = observation.Usage,
                promptTokens = UsageInt(observation.Usage, "prompt_tokens"),
                completionTokens = UsageInt(observation.Usage, "completion_tokens"),
                reasoningTokens = UsageInt(observation.Usage, "reasoning_tokens"),
                reasoningExecutionConfirmed = UsageInt(observation.Usage, "reasoning_tokens") is > 0,
                sseEventCount = observation.SseEventCount,
                retryCount = observation.RetryCount,
                rawSseSha256 = Hashing.Sha256(observation.RawSse),
                rawResponseSha256 = Hashing.Sha256(observation.Content),
                rawSseUtf8Bytes = Encoding.UTF8.GetByteCount(observation.RawSse),
                rawResponseUtf8Bytes = Encoding.UTF8.GetByteCount(observation.Content),
                rawSse = observation.RawSse,
                rawResponse = observation.Content,
            };
            WriteNew(Path.Combine(capturePath, "SRC-089.raw-capture.v1.json"), rawCapture);
            ParsedLedger? parsed = null;
            string? parserError = null;
            try { parsed = ParseExactExtentLedger(request, observation.Content); }
            catch (Exception error) { parserError = error.GetType().Name + ": " + error.Message; }
            WriteNew(Path.Combine(capturePath, "result.v1.json"), new
            {
                schemaVersion = "v5-p6tg2e-one-primary-call-result-v1",
                status = "EXECUTION_SET_FROZEN",
                providerCalls = 1,
                retries = 0,
                repairs = 0,
                fallbacks = 0,
                goldRead = false,
                goldMutation = "NONE",
                runtimeChanged = false,
                rawCaptureSha256 = Hashing.Sha256(File.ReadAllText(Path.Combine(capturePath, "SRC-089.raw-capture.v1.json"))),
                row = new
                {
                    documentId = request.DocumentId,
                    transportStatus = "COMPLETED",
                    finishReason = observation.FinishReason,
                    parserStatus = parsed is null ? "REJECTED" : "PARSED",
                    parserError,
                    rawResponseSha256 = Hashing.Sha256(observation.Content),
                    promptTokens = UsageInt(observation.Usage, "prompt_tokens"),
                    completionTokens = UsageInt(observation.Usage, "completion_tokens"),
                    reasoningTokens = UsageInt(observation.Usage, "reasoning_tokens"),
                    retryCount = observation.RetryCount,
                    parsed,
                },
            });
            Assert.Equal("stop", observation.FinishReason);
            Assert.NotNull(parsed);
            Assert.Equal(6, parsed!.RawDecisions);
            Assert.Equal(6, parsed.AcceptedSelections);
            Assert.Equal(0, parsed.Quarantined);
            Assert.Equal(0, parsed.MissingPrimaries);
        }
        catch (Exception error) when (error is not Xunit.Sdk.XunitException)
        {
            WriteNew(Path.Combine(capturePath, "transport-failure.v1.json"), new
            {
                schemaVersion = "v5-p6tg2e-transport-failure-v1",
                documentId = request.DocumentId,
                providerRequestHash = request.ProviderHash,
                errorType = error.GetType().FullName,
                message = error.Message,
                retryCount = 0,
                goldRead = false,
            });
            WriteNew(Path.Combine(capturePath, "result.v1.json"), new
            {
                schemaVersion = "v5-p6tg2e-one-primary-call-result-v1",
                status = "EXECUTION_SET_FROZEN",
                providerCalls = 1,
                retries = 0,
                repairs = 0,
                fallbacks = 0,
                goldRead = false,
                goldMutation = "NONE",
                runtimeChanged = false,
                row = new { documentId = request.DocumentId, transportStatus = "FAILED", errorType = error.GetType().FullName, retryCount = 0 },
            });
            throw;
        }
    }

    [Fact]
    public void P6TG2D_frozen_permutation_capture_is_scored_offline_with_paired_B_C_D_matrix()
    {
        using var f1Retry = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{F1Root}/retry-src089-result.v1.json")));
        using var g1 = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(G1Path)));
        using var gold = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(Gold089Path)));
        using var g2bRaw = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{ExactExtentCaptureRoot}/SRC-089.raw-capture.v1.json")));
        using var g2cRaw = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{ExactExtentOrderCaptureRoot}/SRC-089.raw-capture.v1.json")));
        using var g2dRaw = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{ExactExtentPermutationCaptureRoot}/SRC-089.raw-capture.v1.json")));
        var prepared = Prepare("SRC-089", SourcePdfCorpus.Src089, f1Retry.RootElement.GetProperty("row"), g1.RootElement.GetProperty("src089"));
        var anchorRequest = ComposeAnchorExistence(prepared);
        using var g2aRaw = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{AnchorExistenceCaptureRoot}/SRC-089.raw-capture.v1.json")));
        var anchorLedger = ParseAnchorLedger(anchorRequest, g2aRaw.RootElement.GetProperty("rawResponse").GetString()!);
        var hasPrimaries = anchorLedger.Decisions.Select(item => JsonDocument.Parse(JsonSerializer.Serialize(item)))
            .Where(item => item.RootElement.GetProperty("anchor").GetString() == "HAS_STRUCTURAL_EXTENT")
            .Select(item => item.RootElement.GetProperty("primary").GetString()!).ToHashSet(StringComparer.Ordinal);
        var baseline = ComposeExactExtentFromHas(prepared, hasPrimaries);
        var reverse = ComposeExactExtentWithCandidateOrder(prepared, baseline, true);
        var permutation = ComposeExactExtentWithHashPermutation(prepared, baseline, "G2D");
        var b = ParseExactExtentLedger(baseline, g2bRaw.RootElement.GetProperty("rawResponse").GetString()!);
        var c = ParseExactExtentLedger(reverse, g2cRaw.RootElement.GetProperty("rawResponse").GetString()!);
        var d = ParseExactExtentLedger(permutation, g2dRaw.RootElement.GetProperty("rawResponse").GetString()!);
        Assert.Equal(g2cRaw.RootElement.GetProperty("rawResponseSha256").GetString(), g2dRaw.RootElement.GetProperty("rawResponseSha256").GetString());

        var choice = (ParsedLedger ledger) => ledger.Decisions.Select(item => JsonDocument.Parse(JsonSerializer.Serialize(item)))
            .ToDictionary(item => item.RootElement.GetProperty("primary").GetString()!, item => item.RootElement.GetProperty("candidate").GetString()!, StringComparer.Ordinal);
        var bChoices = choice(b); var cChoices = choice(c); var dChoices = choice(d);
        var goldClaims = gold.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray()
            .Select(claim => new { identity = claim.GetProperty("identity").GetString()!, primaryAlias = claim.GetProperty("sourceParts").EnumerateArray().First().GetProperty("sourceAlias").GetString()! })
            .Where(claim => prepared.OccurrenceByAlias.ContainsKey(claim.primaryAlias)).ToArray();
        Assert.Equal(6, goldClaims.Length);
        var rows = bChoices.Keys.OrderBy(key => key, StringComparer.Ordinal).Select(primary =>
        {
            var alias = baseline.OccurrenceGroups.Single(group => group.PrimaryOccurrence == primary).PrimaryAlias;
            var goldClaim = goldClaims.Single(claim => claim.primaryAlias == alias);
            var exact = (string id) => prepared.SourcePack.Universe.Candidates.Single(candidate => candidate.Id == id).SpanIdentity == goldClaim.identity;
            var kind = (string id) => prepared.SourcePack.Universe.Candidates.Single(candidate => candidate.Id == id).Kind.ToString();
            return new
            {
                primary,
                goldCandidate = prepared.SourcePack.Universe.Candidates.Single(candidate => candidate.SpanIdentity == goldClaim.identity).Id,
                b = bChoices[primary], c = cChoices[primary], d = dChoices[primary],
                bKind = kind(bChoices[primary]), cKind = kind(cChoices[primary]), dKind = kind(dChoices[primary]),
                bExact = exact(bChoices[primary]), cExact = exact(cChoices[primary]), dExact = exact(dChoices[primary]),
                bToC = $"{bChoices[primary]}→{cChoices[primary]}",
                bToD = $"{bChoices[primary]}→{dChoices[primary]}",
                cToD = $"{cChoices[primary]}→{dChoices[primary]}",
            };
        }).ToArray();
        Assert.Equal(5, rows.Count(row => row.cExact));
        Assert.Equal(5, rows.Count(row => row.dExact));
        Assert.Equal(4, rows.Count(row => row.cKind == "MULTIPART" && row.cExact));
        Assert.Equal(4, rows.Count(row => row.dKind == "MULTIPART" && row.dExact));

        FreezeArtifact.AssertJson(ExactExtentPermutationAuditRoot, "permutation-gold-audit.v1.json", new
        {
            schemaVersion = "v5-p6tg2d-permutation-gold-audit-v1",
            authority = new
            {
                g2bRawResponseSha256 = g2bRaw.RootElement.GetProperty("rawResponseSha256").GetString(),
                g2cRawResponseSha256 = g2cRaw.RootElement.GetProperty("rawResponseSha256").GetString(),
                g2dRawResponseSha256 = g2dRaw.RootElement.GetProperty("rawResponseSha256").GetString(),
                goldSha256 = Hashing.Sha256(File.ReadAllText(TestRepository.Path(Gold089Path))),
                providerCallsDuringAudit = 0,
                goldMutation = "NONE",
                runtimeChanged = false,
            },
            metrics = new
            {
                g2bExact = new { correct = rows.Count(row => row.bExact), total = 6 },
                g2cExact = new { correct = rows.Count(row => row.cExact), total = 6 },
                g2dExact = new { correct = rows.Count(row => row.dExact), total = 6 },
                g2cMultipartExact = new { correct = rows.Count(row => row.cKind == "MULTIPART" && row.cExact), total = 5 },
                g2dMultipartExact = new { correct = rows.Count(row => row.dKind == "MULTIPART" && row.dExact), total = 5 },
                g2cSameAsG2b = rows.Count(row => row.b == row.c),
                g2dSameAsG2b = rows.Count(row => row.b == row.d),
                g2cSameAsG2d = rows.Count(row => row.c == row.d),
            },
            rows,
            conclusion = "G2D_GOLD_AUDIT_CONFIRMS_5_OF_6_EXACT_AND_4_OF_5_MULTIPART; G2C_AND_G2D_IDENTITIES_MATCH_ON_ALL_SIX; ORIGINAL_G2B_ORDERING_REMAINS_PRIMARY_BIAS_SUSPECT",
        });
    }

    [Fact]
    public void P6TG2E_preflight_moves_only_whole_primary_to_the_end_of_each_frozen_menu()
    {
        using var f1Retry = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{F1Root}/retry-src089-result.v1.json")));
        using var g1 = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(G1Path)));
        using var g2aRaw = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{AnchorExistenceCaptureRoot}/SRC-089.raw-capture.v1.json")));
        using var g2b = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{ExactExtentPreflightRoot}/exact-extent-preflight.v1.json")));
        var prepared = Prepare("SRC-089", SourcePdfCorpus.Src089, f1Retry.RootElement.GetProperty("row"), g1.RootElement.GetProperty("src089"));
        var anchorRequest = ComposeAnchorExistence(prepared);
        var anchorLedger = ParseAnchorLedger(anchorRequest, g2aRaw.RootElement.GetProperty("rawResponse").GetString()!);
        var hasPrimaries = anchorLedger.Decisions.Select(item => JsonDocument.Parse(JsonSerializer.Serialize(item)))
            .Where(item => item.RootElement.GetProperty("anchor").GetString() == "HAS_STRUCTURAL_EXTENT")
            .Select(item => item.RootElement.GetProperty("primary").GetString()!).ToHashSet(StringComparer.Ordinal);
        var baseline = ComposeExactExtentFromHas(prepared, hasPrimaries);
        var moved = ComposeExactExtentWithWholeMoved(prepared, baseline);
        Assert.All(baseline.OccurrenceGroups, group =>
        {
            var movedGroup = moved.OccurrenceGroups.Single(item => item.PrimaryOccurrence == group.PrimaryOccurrence);
            var whole = group.CandidateIds.Single(id => prepared.SourcePack.Universe.Candidates.Single(candidate => candidate.Id == id).Kind.ToString() == "WHOLE");
            var expected = group.CandidateIds.Where(id => id != whole).Append(whole).ToArray();
            Assert.Equal(expected, movedGroup.CandidateIds);
        });
        Assert.NotEqual(baseline.MessageHash, moved.MessageHash);
        Assert.NotEqual(baseline.ProviderHash, moved.ProviderHash);

        FreezeArtifact.AssertJson(ExactExtentWholeMovePreflightRoot, "whole-move-preflight.v1.json", new
        {
            schemaVersion = "v5-p6tg2e-whole-move-preflight-v1",
            status = "PREPARED_NOT_AUTHORIZED",
            treatment = new
            {
                onlyIndependentVariable = "WHOLE_PRIMARY_POSITION",
                baselineOrdering = "FROZEN_G2B_ORDER",
                wholeMove = "REMOVE_WHOLE_FROM_POSITION_ONE; APPEND_WHOLE; PRESERVE_NON_WHOLE_RELATIVE_ORDER",
                goldUsedForRequestConstruction = false,
                samePrimaries = true,
                sameCandidateIds = true,
                sameCandidateContentAndKinds = true,
                samePrompt = true,
                model = "qwen/qwen3.7-flash",
                provider = "alibaba",
                reasoning = new { enabled = true, effort = "OMITTED" },
            },
            sourceAuthority = new
            {
                g2aRawResponseSha256 = g2aRaw.RootElement.GetProperty("rawResponseSha256").GetString(),
                g2bPreflightSha256 = Hashing.Sha256(File.ReadAllText(TestRepository.Path($"{ExactExtentPreflightRoot}/exact-extent-preflight.v1.json"))),
                hasPrimaries = moved.OccurrenceGroups.Select(group => new { primary = group.PrimaryOccurrence, alias = group.PrimaryAlias }).ToArray(),
            },
            baseline = new { userMessageSha256 = baseline.MessageHash, providerBodySha256 = baseline.ProviderHash, providerBodyBytes = baseline.ProviderBytes },
            arm = new
            {
                userMessageSha256 = moved.MessageHash,
                userMessageUtf8Bytes = moved.MessageBytes,
                providerBodySha256 = moved.ProviderHash,
                providerBodyBytes = moved.ProviderBytes,
                primaryCount = moved.OccurrenceGroups.Count,
                optionsPerPrimary = moved.OccurrenceGroups.Select(group => new { primary = group.PrimaryOccurrence, candidateIds = group.CandidateIds }).ToArray(),
            },
            execution = new { providerCalls = 0, retries = 0, repairs = 0, fallbacks = 0, goldRead = false, runtimeChanged = false },
            conclusion = "P6TG2E_PROVIDER_FREE_WHOLE_POSITION_ARM_FROZEN; SEPARATE_AUTHORIZATION_REQUIRED",
        });
    }

    [Fact]
    public void P6TG2_menu_forensic_compares_frozen_B_C_D_E_orders_without_gold_or_provider_calls()
    {
        using var f1Retry = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{F1Root}/retry-src089-result.v1.json")));
        using var g1 = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(G1Path)));
        var prepared = Prepare("SRC-089", SourcePdfCorpus.Src089, f1Retry.RootElement.GetProperty("row"), g1.RootElement.GetProperty("src089"));

        using var g2aRaw = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{AnchorExistenceCaptureRoot}/SRC-089.raw-capture.v1.json")));
        using var g2bPreflight = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{ExactExtentPreflightRoot}/exact-extent-preflight.v1.json")));
        using var g2cPreflight = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{ExactExtentOrderPreflightRoot}/candidate-order-preflight.v1.json")));
        using var g2dPreflight = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{ExactExtentPermutationPreflightRoot}/permutation-preflight.v1.json")));
        using var g2ePreflight = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{ExactExtentWholeMovePreflightRoot}/whole-move-preflight.v1.json")));
        using var g2bRaw = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{ExactExtentCaptureRoot}/SRC-089.raw-capture.v1.json")));
        using var g2cRaw = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{ExactExtentOrderCaptureRoot}/SRC-089.raw-capture.v1.json")));
        using var g2dRaw = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{ExactExtentPermutationCaptureRoot}/SRC-089.raw-capture.v1.json")));
        using var g2eRaw = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{ExactExtentWholeMoveCaptureRoot}/SRC-089.raw-capture.v1.json")));

        var anchorRequest = ComposeAnchorExistence(prepared);
        var anchorLedger = ParseAnchorLedger(anchorRequest, g2aRaw.RootElement.GetProperty("rawResponse").GetString()!);
        var hasPrimaries = anchorLedger.Decisions.Select(item => JsonDocument.Parse(JsonSerializer.Serialize(item)))
            .Where(item => item.RootElement.GetProperty("anchor").GetString() == "HAS_STRUCTURAL_EXTENT")
            .Select(item => item.RootElement.GetProperty("primary").GetString()!).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(6, hasPrimaries.Count);

        var g2bRequest = ComposeExactExtentFromHas(prepared, hasPrimaries);
        var g2cRequest = ComposeExactExtentWithCandidateOrder(prepared, g2bRequest, true);
        var g2dRequest = ComposeExactExtentWithHashPermutation(prepared, g2bRequest, "G2D");
        var g2eRequest = ComposeExactExtentWithWholeMoved(prepared, g2bRequest);
        Assert.Equal(g2bPreflight.RootElement.GetProperty("callPlan").GetProperty("providerBodySha256").GetString(), g2bRequest.ProviderHash);
        Assert.Equal(g2cPreflight.RootElement.GetProperty("candidateOrderArm").GetProperty("providerBodySha256").GetString(), g2cRequest.ProviderHash);
        Assert.Equal(g2dPreflight.RootElement.GetProperty("permutationArm").GetProperty("providerBodySha256").GetString(), g2dRequest.ProviderHash);
        Assert.Equal(g2ePreflight.RootElement.GetProperty("arm").GetProperty("providerBodySha256").GetString(), g2eRequest.ProviderHash);

        var arms = new[]
        {
            ReadForensicArm("G2B", g2bRequest, g2bRaw.RootElement),
            ReadForensicArm("G2C", g2cRequest, g2cRaw.RootElement),
            ReadForensicArm("G2D", g2dRequest, g2dRaw.RootElement),
            ReadForensicArm("G2E", g2eRequest, g2eRaw.RootElement),
        };

        Assert.All(arms, arm =>
        {
            Assert.Equal(6, arm.Rows.Count);
            Assert.Equal(6, arm.Rows.Select(row => row.Primary).Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(6, arm.Rows.Count(row => row.SelectedKind is not null));
        });

        var byArm = arms.ToDictionary(arm => arm.Name, StringComparer.Ordinal);
        Assert.Equal(0, byArm["G2E"].Rows.Count(row => row.SelectedFirstCandidate));
        Assert.Equal(6, byArm["G2B"].Rows.Count(row => row.SelectedFirstCandidate));
        Assert.Equal(
            byArm["G2C"].Rows.Select(row => row.SelectedCandidate),
            byArm["G2D"].Rows.Select(row => row.SelectedCandidate));

        FreezeArtifact.AssertJson(ExactExtentMenuForensicRoot, "menu-forensic.v1.json", new
        {
            schemaVersion = "v5-p6tg-menu-forensic-v1",
            status = "PROVIDER_FREE_FORENSIC",
            source = new
            {
                documentId = "SRC-089",
                candidateUniverseFingerprint = prepared.SourcePack.Universe.Fingerprint,
                goldRead = false,
                providerCallsDuringAudit = 0,
                runtimeChanged = false,
                arms = new
                {
                    G2B = byArm["G2B"].ProviderBodySha256,
                    G2C = byArm["G2C"].ProviderBodySha256,
                    G2D = byArm["G2D"].ProviderBodySha256,
                    G2E = byArm["G2E"].ProviderBodySha256,
                },
                rawResponseSha256 = new
                {
                    G2B = byArm["G2B"].RawResponseSha256,
                    G2C = byArm["G2C"].RawResponseSha256,
                    G2D = byArm["G2D"].RawResponseSha256,
                    G2E = byArm["G2E"].RawResponseSha256,
                },
            },
            historicalCorrection = new
            {
                supersededClaim = "G2E selected WHOLE on 6/6 and matched G2B on 6/6.",
                authoritativeRawReconciledResult = new
                {
                    g2eWhole = new { selected = byArm["G2E"].Rows.Count(row => IsWholeKind(row.SelectedKind)), total = 6 },
                    g2eMultipart = new { selected = byArm["G2E"].Rows.Count(row => IsMultipartKind(row.SelectedKind)), total = 6 },
                    g2bG2eSameIdentity = new { selected = byArm["G2B"].Rows.Zip(byArm["G2E"].Rows).Count(pair => pair.First.SelectedCandidate == pair.Second.SelectedCandidate), total = 6 },
                    evidence = "DIRECT_FROZEN_RAW_CAPTURE_PARSE_WITH_REQUEST_HASH_PARITY",
                },
            },
            diagnostics = new
            {
                selectedWhole = arms.ToDictionary(arm => arm.Name, arm => arm.Rows.Count(row => IsWholeKind(row.SelectedKind)), StringComparer.Ordinal),
                selectedFirst = arms.ToDictionary(arm => arm.Name, arm => arm.Rows.Count(row => row.SelectedFirstCandidate), StringComparer.Ordinal),
                selectedMultipart = arms.ToDictionary(arm => arm.Name, arm => arm.Rows.Count(row => IsMultipartKind(row.SelectedKind)), StringComparer.Ordinal),
                g2bG2eSameSelection = byArm["G2B"].Rows.Zip(byArm["G2E"].Rows).Count(pair => pair.First.SelectedCandidate == pair.Second.SelectedCandidate),
                g2cG2dSameSelection = byArm["G2C"].Rows.Zip(byArm["G2D"].Rows).Count(pair => pair.First.SelectedCandidate == pair.Second.SelectedCandidate),
                g2bG2eTransitions = byArm["G2B"].Rows.Zip(byArm["G2E"].Rows).Select(pair => new
                {
                    primary = pair.First.Primary,
                    g2b = pair.First.SelectedCandidate,
                    g2e = pair.Second.SelectedCandidate,
                    g2bKind = pair.First.SelectedKind,
                    g2eKind = pair.Second.SelectedKind,
                }).ToArray(),
            },
            rows = arms.Select(arm => new
            {
                arm = arm.Name,
                providerBodySha256 = arm.ProviderBodySha256,
                rawResponseSha256 = arm.RawResponseSha256,
                rows = arm.Rows,
            }).ToArray(),
            conclusion = "FIRST_POSITIONAL_BIAS_REJECTED; PURE_ALWAYS_WHOLE_RULE_REJECTED; WHOLE_ATOMICITY_PRIOR_AND_ORDER_BY_CANDIDATE_KIND_REMAIN_PRIMARY_SUPPORTED_DIAGNOSIS",
        });

        static bool IsWholeKind(string kind) =>
            kind.Contains("WHOLE", StringComparison.OrdinalIgnoreCase);

        static bool IsMultipartKind(string kind) =>
            kind.Contains("MULTIPART", StringComparison.OrdinalIgnoreCase);

        ForensicArm ReadForensicArm(string name, PreparedRequest request, JsonElement rawCapture)
        {
            var ledger = ParseExactExtentLedger(request, rawCapture.GetProperty("rawResponse").GetString()!);
            Assert.Equal(6, ledger.AcceptedSelections);
            Assert.Equal(0, ledger.Quarantined);
            Assert.Equal(0, ledger.MissingPrimaries);
            var decisions = ledger.Decisions.Select(item => JsonDocument.Parse(JsonSerializer.Serialize(item)))
                .ToDictionary(item => item.RootElement.GetProperty("primary").GetString()!, item => item.RootElement.GetProperty("candidate").GetString()!, StringComparer.Ordinal);
            var universe = prepared.SourcePack.Universe.Candidates.ToDictionary(item => item.Id, StringComparer.Ordinal);
            var rows = request.OccurrenceGroups.Select(group =>
            {
                var selected = decisions[group.PrimaryOccurrence];
                var kinds = group.CandidateIds.Select(id => universe[id].Kind.ToString()).ToArray();
                var identities = group.CandidateIds.Select(id => universe[id].SpanIdentity).ToArray();
                var selectedIndex = Array.IndexOf(group.CandidateIds.ToArray(), selected);
                var wholeIndex = Array.FindIndex(kinds, kind => kind == "WHOLE");
                return new ForensicRow(
                    group.PrimaryOccurrence,
                    group.CandidateIds,
                    kinds,
                    identities,
                    selected,
                    universe[selected].Kind.ToString(),
                    universe[selected].SpanIdentity,
                    selectedIndex + 1,
                    wholeIndex + 1,
                    selectedIndex == 0,
                    selectedIndex < wholeIndex ? "BEFORE_WHOLE" : selectedIndex > wholeIndex ? "AFTER_WHOLE" : "WHOLE");
            }).ToArray();
            return new ForensicArm(name, request.ProviderHash, rawCapture.GetProperty("rawResponseSha256").GetString()!, rows);
        }
    }

    [Fact]
    public void P6TH1_preflight_freezes_independent_candidate_judgments_and_fail_closed_aggregation()
    {
        using var f1Retry = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{F1Root}/retry-src089-result.v1.json")));
        using var g1 = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(G1Path)));
        using var g2aRaw = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{AnchorExistenceCaptureRoot}/SRC-089.raw-capture.v1.json")));
        var prepared = Prepare("SRC-089", SourcePdfCorpus.Src089, f1Retry.RootElement.GetProperty("row"), g1.RootElement.GetProperty("src089"));
        var anchorRequest = ComposeAnchorExistence(prepared);
        var anchorLedger = ParseAnchorLedger(anchorRequest, g2aRaw.RootElement.GetProperty("rawResponse").GetString()!);
        var hasPrimaries = anchorLedger.Decisions.Select(item => JsonDocument.Parse(JsonSerializer.Serialize(item)))
            .Where(item => item.RootElement.GetProperty("anchor").GetString() == "HAS_STRUCTURAL_EXTENT")
            .Select(item => item.RootElement.GetProperty("primary").GetString()!).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(6, hasPrimaries.Count);

        var baseline = ComposeExactExtentFromHas(prepared, hasPrimaries);
        var reversed = ComposeExactExtentWithCandidateOrder(prepared, baseline, true);
        var independent = ComposeIndependentCandidateJudgments(prepared, baseline);
        var independentFromReversed = ComposeIndependentCandidateJudgments(prepared, reversed);

        Assert.Equal(6, independent.Select(item => item.Primary).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(30, independent.Count);
        Assert.Equal(30, independent.Select(item => $"{item.Primary}|{item.CandidateId}").Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(30, independentFromReversed.Count);
        Assert.Equal(independent.Select(item => $"{item.Primary}|{item.CandidateId}").OrderBy(item => item, StringComparer.Ordinal),
            independentFromReversed.Select(item => $"{item.Primary}|{item.CandidateId}").OrderBy(item => item, StringComparer.Ordinal));

        var baselineO17C104 = independent.Single(item => item.Primary == "O17" && item.CandidateId == "C104");
        var reversedO17C104 = independentFromReversed.Single(item => item.Primary == "O17" && item.CandidateId == "C104");
        Assert.Equal(baselineO17C104.UserMessage, reversedO17C104.UserMessage);
        Assert.Equal(baselineO17C104.MessageHash, reversedO17C104.MessageHash);
        Assert.Equal(baselineO17C104.ProviderHash, reversedO17C104.ProviderHash);

        Assert.All(independent, request =>
        {
            using var message = JsonDocument.Parse(request.UserMessage);
            var root = message.RootElement;
            Assert.Equal(6, root.EnumerateObject().Count());
            Assert.True(root.TryGetProperty("candidate", out var candidate));
            Assert.Equal(request.CandidateId, candidate.GetProperty("id").GetString());
            Assert.False(root.TryGetProperty("candidates", out _));
            Assert.Equal("ESTABLISHES_STRUCTURE", root.GetProperty("function").GetString());
            Assert.Equal(request.Primary, root.GetProperty("primary").GetString());
        });

        var synthetic = new[]
        {
            new IndependentJudgmentLedger("O9", "C47", "NOT_EXACT_STRUCTURAL_EXTENT"),
            new IndependentJudgmentLedger("O17", "C104", "EXACT_STRUCTURAL_EXTENT"),
            new IndependentJudgmentLedger("O27", "C171", "EXACT_STRUCTURAL_EXTENT"),
            new IndependentJudgmentLedger("O27", "C174", "EXACT_STRUCTURAL_EXTENT"),
        };
        var aggregate = AggregateIndependentJudgments(synthetic, ["O9", "O17", "O27"])
            .ToDictionary(item => item.Primary, StringComparer.Ordinal);
        Assert.Equal("NO_EXACT_EXTENT", aggregate["O9"].Outcome);
        Assert.Equal("SELECTED_EXACT_EXTENT", aggregate["O17"].Outcome);
        Assert.Equal("C104", aggregate["O17"].SelectedCandidate);
        Assert.Equal("CONFLICT_MULTIPLE_EXACT", aggregate["O27"].Outcome);
        Assert.Null(aggregate["O27"].SelectedCandidate);

        var valid = ParseIndependentJudgment(baselineO17C104,
            "{\"primary\":\"O17\",\"candidate\":\"C104\",\"judgment\":\"EXACT_STRUCTURAL_EXTENT\"}");
        Assert.Equal("EXACT_STRUCTURAL_EXTENT", valid.Judgment);
        Assert.Throws<InvalidOperationException>(() => ParseIndependentJudgment(baselineO17C104,
            "{\"primary\":\"O17\",\"candidate\":\"C104\",\"judgment\":\"EXACT\"}"));
        Assert.Throws<InvalidOperationException>(() => ParseIndependentJudgment(baselineO17C104,
            "{\"primary\":\"O17\",\"candidate\":\"C101\",\"judgment\":\"EXACT_STRUCTURAL_EXTENT\"}"));
        Assert.Throws<InvalidOperationException>(() => ParseIndependentJudgment(baselineO17C104,
            "{\"primary\":\"O17\",\"candidate\":\"C104\",\"judgment\":\"NOT_EXACT_STRUCTURAL_EXTENT\",\"reason\":\"extra\"}"));

        FreezeArtifact.AssertJson(IndependentJudgmentPreflightRoot, "independent-candidate-preflight.v1.json", new
        {
            schemaVersion = "v5-p6th1-independent-candidate-preflight-v1",
            status = "PREPARED_NOT_AUTHORIZED",
            protocolVersion = IndependentJudgmentProtocol,
            treatment = new
            {
                model = "qwen/qwen3.7-flash",
                provider = "alibaba",
                reasoning = new { enabled = true, effort = "OMITTED" },
                anchorAuthority = "FROZEN_P6TG2A_RAW_HAS_DECISIONS",
                independentProposition = true,
                competingCandidatesModelVisible = false,
                goldUsedForRequestConstruction = false,
            },
            outputContract = new
            {
                shape = "{\"primary\":\"O17\",\"candidate\":\"C104\",\"judgment\":\"EXACT_STRUCTURAL_EXTENT\"}",
                allowedJudgments = new[] { "EXACT_STRUCTURAL_EXTENT", "NOT_EXACT_STRUCTURAL_EXTENT" },
                oneIssuedPrimaryCandidatePairPerResponse = true,
                modelAuthoredTextOrCoordinates = false,
                modelAuthoredCandidateOrPrimary = false,
                parserRejects = new[] { "unknown-primary", "unknown-candidate", "invalid-judgment", "extra-property", "missing-property" },
                invalidResponsePolicy = "single-proposition-response-rejected; no repair or inference",
            },
            aggregation = new
            {
                exactCountZero = "NO_EXACT_EXTENT",
                exactCountOne = "SELECTED_EXACT_EXTENT",
                exactCountGreaterThanOne = "CONFLICT_MULTIPLE_EXACT",
                tieBreak = "FORBIDDEN",
                conflictIsFailClosed = true,
            },
            sourceAuthority = new
            {
                documentId = prepared.DocumentId,
                candidateUniverseFingerprint = prepared.SourcePack.Universe.Fingerprint,
                g2aRawResponseSha256 = g2aRaw.RootElement.GetProperty("rawResponseSha256").GetString(),
                hasPrimaryCount = hasPrimaries.Count,
                eligibleCandidateJudgmentCount = independent.Count,
                goldRead = false,
                goldMutation = "NONE",
                sharedRuntime = "UNCHANGED",
            },
            permutationInvariance = new
            {
                primary = "O17",
                candidate = "C104",
                baselineMenuOrder = baseline.OccurrenceGroups.Single(group => group.PrimaryOccurrence == "O17").CandidateIds,
                reversedMenuOrder = reversed.OccurrenceGroups.Single(group => group.PrimaryOccurrence == "O17").CandidateIds,
                canonicalUserMessageSha256 = baselineO17C104.MessageHash,
                byteIdentical = baselineO17C104.UserMessage == reversedO17C104.UserMessage,
                providerBodyIdentical = baselineO17C104.ProviderHash == reversedO17C104.ProviderHash,
            },
            callPlan = independent.Select(request => new
            {
                documentId = request.DocumentId,
                primary = request.Primary,
                primaryAlias = request.PrimaryAlias,
                candidate = request.CandidateId,
                candidateKind = request.CandidateKind,
                candidateIdentity = request.CandidateIdentity,
                systemPromptSha256 = Hashing.Sha256(request.SystemPrompt),
                userMessageSha256 = request.MessageHash,
                userMessageUtf8Bytes = request.MessageBytes,
                providerBodySha256 = request.ProviderHash,
                providerBodyBytes = request.ProviderBytes,
                maxCompletionTokens = request.MaxCompletionTokens,
            }).ToArray(),
            execution = new { providerCalls = 0, retries = 0, repairs = 0, fallbacks = 0, goldRead = false, runtimeChanged = false },
            conclusion = "P6TH1_INDEPENDENT_CANDIDATE_JUDGMENT_PREFLIGHT_FROZEN; ORDER_COUPLING_REMOVED_BY_CONSTRUCTION; PROVIDER_EXECUTION_REQUIRES_SEPARATE_EXPLICIT_AUTHORIZATION",
        });
    }

    [Fact]
    public async Task Run_exactly_thirty_frozen_independent_candidate_judgments_only_when_explicitly_enabled_for_P6TH1()
    {
        if (Environment.GetEnvironmentVariable(RunIndependentJudgmentVariable) is not ("1" or "true" or "TRUE")) return;

        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        Assert.False(string.IsNullOrWhiteSpace(apiKey), "OPENROUTER_API_KEY is required for the explicitly enabled P6T-H1 canary.");
        var capturePath = TestRepository.Path(IndependentJudgmentCaptureRoot);
        Assert.False(Directory.Exists(capturePath), "P6T-H1 capture root already exists; automatic resume or resend is forbidden.");

        using var f1Retry = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{F1Root}/retry-src089-result.v1.json")));
        using var g1 = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(G1Path)));
        using var g2aRaw = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{AnchorExistenceCaptureRoot}/SRC-089.raw-capture.v1.json")));
        using var preflight = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{IndependentJudgmentPreflightRoot}/independent-candidate-preflight.v1.json")));
        var prepared = Prepare("SRC-089", SourcePdfCorpus.Src089, f1Retry.RootElement.GetProperty("row"), g1.RootElement.GetProperty("src089"));
        var anchorRequest = ComposeAnchorExistence(prepared);
        var anchorLedger = ParseAnchorLedger(anchorRequest, g2aRaw.RootElement.GetProperty("rawResponse").GetString()!);
        var hasPrimaries = anchorLedger.Decisions.Select(item => JsonDocument.Parse(JsonSerializer.Serialize(item)))
            .Where(item => item.RootElement.GetProperty("anchor").GetString() == "HAS_STRUCTURAL_EXTENT")
            .Select(item => item.RootElement.GetProperty("primary").GetString()!).ToHashSet(StringComparer.Ordinal);
        var baseline = ComposeExactExtentFromHas(prepared, hasPrimaries);
        var requests = ComposeIndependentCandidateJudgments(prepared, baseline);
        var frozenPlan = preflight.RootElement.GetProperty("callPlan").EnumerateArray()
            .ToDictionary(item => $"{item.GetProperty("primary").GetString()}|{item.GetProperty("candidate").GetString()}", item => item, StringComparer.Ordinal);
        Assert.Equal(30, requests.Count);
        Assert.Equal(30, frozenPlan.Count);
        foreach (var request in requests)
        {
            var plan = frozenPlan[$"{request.Primary}|{request.CandidateId}"];
            Assert.Equal(request.MessageHash, plan.GetProperty("userMessageSha256").GetString());
            Assert.Equal(request.MessageBytes, plan.GetProperty("userMessageUtf8Bytes").GetInt32());
            Assert.Equal(request.ProviderHash, plan.GetProperty("providerBodySha256").GetString());
            Assert.Equal(request.ProviderBytes, plan.GetProperty("providerBodyBytes").GetInt32());
        }

        Directory.CreateDirectory(capturePath);
        WriteNew(Path.Combine(capturePath, "execution-reservation.v1.json"), new
        {
            schemaVersion = "v5-p6th1-execution-reservation-v1",
            status = "THIRTY_PRIMARY_SLOTS_RESERVED",
            documentId = "SRC-089",
            plannedProviderCalls = requests.Count,
            retriesAllowed = 0,
            repairsAllowed = false,
            fallbacksAllowed = false,
            goldRead = false,
            callPlanSha256 = Hashing.Sha256(File.ReadAllText(TestRepository.Path($"{IndependentJudgmentPreflightRoot}/independent-candidate-preflight.v1.json"))),
        });

        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var provider = new OpenRouterQualificationTransport(http, new RemoteInferenceOptions
        {
            ApiKey = apiKey!,
            Model = "qwen/qwen3.7-flash",
            OpenRouterProviderRoute = "Alibaba",
            TransientRequestRetries = 0,
            MaxParallelRequests = 1,
            ProviderTransportTimeoutSeconds = 300,
            RequireZeroDataRetention = false,
        });

        var rows = new List<object>();
        foreach (var request in requests)
        {
            var key = $"{request.Primary}-{request.CandidateId}";
            try
            {
                var observation = await provider.ExecuteObservedAsync(request.ProviderBody, request.MaxCompletionTokens,
                    request.SystemPrompt, request.UserMessage, CancellationToken.None);
                var rawCapture = new
                {
                    schemaVersion = "v5-p6th1-raw-provider-capture-v1",
                    documentId = request.DocumentId,
                    primary = request.Primary,
                    candidate = request.CandidateId,
                    candidateIdentity = request.CandidateIdentity,
                    provider = "OpenRouter",
                    model = "qwen/qwen3.7-flash",
                    providerRoute = "Alibaba",
                    reasoningRequested = true,
                    reasoningEffort = "OMITTED",
                    providerRequestHash = request.ProviderHash,
                    providerRequestBytes = request.ProviderBytes,
                    semanticRequestHash = request.MessageHash,
                    finishReason = observation.FinishReason,
                    usage = observation.Usage,
                    promptTokens = UsageInt(observation.Usage, "prompt_tokens"),
                    completionTokens = UsageInt(observation.Usage, "completion_tokens"),
                    reasoningTokens = UsageInt(observation.Usage, "reasoning_tokens"),
                    reasoningExecutionConfirmed = UsageInt(observation.Usage, "reasoning_tokens") is > 0,
                    sseEventCount = observation.SseEventCount,
                    retryCount = observation.RetryCount,
                    rawSseSha256 = Hashing.Sha256(observation.RawSse),
                    rawResponseSha256 = Hashing.Sha256(observation.Content),
                    rawSseUtf8Bytes = Encoding.UTF8.GetByteCount(observation.RawSse),
                    rawResponseUtf8Bytes = Encoding.UTF8.GetByteCount(observation.Content),
                    rawSse = observation.RawSse,
                    rawResponse = observation.Content,
                };
                var rawPath = Path.Combine(capturePath, $"{key}.raw-capture.v1.json");
                WriteNew(rawPath, rawCapture);
                IndependentJudgmentLedger? parsed = null;
                string? parserError = null;
                try { parsed = ParseIndependentJudgment(request, observation.Content); }
                catch (Exception error) { parserError = error.GetType().Name + ": " + error.Message; }
                rows.Add(new
                {
                    primary = request.Primary,
                    candidate = request.CandidateId,
                    candidateIdentity = request.CandidateIdentity,
                    transportStatus = "COMPLETED",
                    finishReason = observation.FinishReason,
                    parserStatus = parsed is null ? "REJECTED" : "PARSED",
                    parserError,
                    judgment = parsed?.Judgment,
                    rawCaptureSha256 = Hashing.Sha256(File.ReadAllText(rawPath)),
                    rawResponseSha256 = Hashing.Sha256(observation.Content),
                    promptTokens = UsageInt(observation.Usage, "prompt_tokens"),
                    completionTokens = UsageInt(observation.Usage, "completion_tokens"),
                    reasoningTokens = UsageInt(observation.Usage, "reasoning_tokens"),
                    retryCount = observation.RetryCount,
                });
            }
            catch (Exception error) when (error is not Xunit.Sdk.XunitException)
            {
                WriteNew(Path.Combine(capturePath, $"{key}.transport-failure.v1.json"), new
                {
                    schemaVersion = "v5-p6th1-transport-failure-v1",
                    documentId = request.DocumentId,
                    primary = request.Primary,
                    candidate = request.CandidateId,
                    providerRequestHash = request.ProviderHash,
                    errorType = error.GetType().FullName,
                    message = error.Message,
                    retryCount = 0,
                    goldRead = false,
                });
                rows.Add(new { primary = request.Primary, candidate = request.CandidateId, transportStatus = "FAILED", errorType = error.GetType().FullName, retryCount = 0 });
            }
        }

        WriteNew(Path.Combine(capturePath, "result.v1.json"), new
        {
            schemaVersion = "v5-p6th1-thirty-primary-call-result-v1",
            status = "EXECUTION_SET_FROZEN",
            providerCalls = requests.Count,
            retries = 0,
            repairs = 0,
            fallbacks = 0,
            goldRead = false,
            goldMutation = "NONE",
            runtimeChanged = false,
            rows,
        });

        Assert.Equal(30, rows.Count);
        Assert.All(rows.Select(row => JsonSerializer.SerializeToElement(row)), row =>
        {
            Assert.Equal("COMPLETED", row.GetProperty("transportStatus").GetString());
            Assert.Equal("stop", row.GetProperty("finishReason").GetString());
            Assert.Equal("PARSED", row.GetProperty("parserStatus").GetString());
            Assert.Equal(0, row.GetProperty("retryCount").GetInt32());
        });
    }

    [Fact]
    public void P6TH1_frozen_raw_capture_is_aggregated_fail_closed_without_gold()
    {
        using var f1Retry = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{F1Root}/retry-src089-result.v1.json")));
        using var g1 = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(G1Path)));
        using var g2aRaw = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{AnchorExistenceCaptureRoot}/SRC-089.raw-capture.v1.json")));
        using var execution = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{IndependentJudgmentCaptureRoot}/result.v1.json")));
        var prepared = Prepare("SRC-089", SourcePdfCorpus.Src089, f1Retry.RootElement.GetProperty("row"), g1.RootElement.GetProperty("src089"));
        var anchorRequest = ComposeAnchorExistence(prepared);
        var anchorLedger = ParseAnchorLedger(anchorRequest, g2aRaw.RootElement.GetProperty("rawResponse").GetString()!);
        var hasPrimaries = anchorLedger.Decisions.Select(item => JsonDocument.Parse(JsonSerializer.Serialize(item)))
            .Where(item => item.RootElement.GetProperty("anchor").GetString() == "HAS_STRUCTURAL_EXTENT")
            .Select(item => item.RootElement.GetProperty("primary").GetString()!).ToHashSet(StringComparer.Ordinal);
        var baseline = ComposeExactExtentFromHas(prepared, hasPrimaries);
        var requests = ComposeIndependentCandidateJudgments(prepared, baseline);
        Assert.Equal(30, execution.RootElement.GetProperty("providerCalls").GetInt32());
        Assert.False(execution.RootElement.GetProperty("goldRead").GetBoolean());

        var judgments = new List<IndependentJudgmentLedger>();
        var rawAuthorities = new List<object>();
        foreach (var request in requests)
        {
            var rawPath = TestRepository.Path($"{IndependentJudgmentCaptureRoot}/{request.Primary}-{request.CandidateId}.raw-capture.v1.json");
            using var raw = JsonDocument.Parse(File.ReadAllText(rawPath));
            var root = raw.RootElement;
            Assert.Equal(request.Primary, root.GetProperty("primary").GetString());
            Assert.Equal(request.CandidateId, root.GetProperty("candidate").GetString());
            Assert.Equal(request.ProviderHash, root.GetProperty("providerRequestHash").GetString());
            Assert.Equal(request.MessageHash, root.GetProperty("semanticRequestHash").GetString());
            Assert.Equal("stop", root.GetProperty("finishReason").GetString());
            Assert.Equal(0, root.GetProperty("retryCount").GetInt32());
            var response = root.GetProperty("rawResponse").GetString()!;
            Assert.Equal(root.GetProperty("rawResponseSha256").GetString(), Hashing.Sha256(response));
            judgments.Add(ParseIndependentJudgment(request, response));
            rawAuthorities.Add(new
            {
                primary = request.Primary,
                candidate = request.CandidateId,
                rawCaptureSha256 = Hashing.Sha256(File.ReadAllText(rawPath)),
                rawResponseSha256 = root.GetProperty("rawResponseSha256").GetString(),
            });
        }

        Assert.Equal(30, judgments.Count);
        Assert.Equal(10, judgments.Count(item => item.Judgment == "EXACT_STRUCTURAL_EXTENT"));
        Assert.Equal(20, judgments.Count(item => item.Judgment == "NOT_EXACT_STRUCTURAL_EXTENT"));
        var aggregate = AggregateIndependentJudgments(judgments, requests.Select(request => request.Primary).Distinct(StringComparer.Ordinal));
        Assert.Equal(6, aggregate.Count);
        Assert.Equal(2, aggregate.Count(item => item.Outcome == "SELECTED_EXACT_EXTENT"));
        Assert.Equal(4, aggregate.Count(item => item.Outcome == "CONFLICT_MULTIPLE_EXACT"));
        Assert.Equal(0, aggregate.Count(item => item.Outcome == "NO_EXACT_EXTENT"));

        FreezeArtifact.AssertJson(IndependentJudgmentAggregationRoot, "independent-candidate-aggregation.v1.json", new
        {
            schemaVersion = "v5-p6th1-independent-candidate-aggregation-v1",
            status = "RAW_CAPTURE_AGGREGATED_FAIL_CLOSED",
            authority = new
            {
                executionResultSha256 = Hashing.Sha256(File.ReadAllText(TestRepository.Path($"{IndependentJudgmentCaptureRoot}/result.v1.json"))),
                g2aRawResponseSha256 = g2aRaw.RootElement.GetProperty("rawResponseSha256").GetString(),
                candidateUniverseFingerprint = prepared.SourcePack.Universe.Fingerprint,
                providerCallsDuringAudit = 0,
                goldReadDuringAudit = false,
                goldMutation = "NONE",
                runtimeChanged = false,
                rawCaptures = rawAuthorities,
            },
            capture = new
            {
                attemptedCalls = requests.Count,
                finishStop = requests.Count,
                parserAccepted = judgments.Count,
                retries = 0,
                exactJudgments = judgments.Count(item => item.Judgment == "EXACT_STRUCTURAL_EXTENT"),
                notExactJudgments = judgments.Count(item => item.Judgment == "NOT_EXACT_STRUCTURAL_EXTENT"),
            },
            aggregation = new
            {
                policy = new { zero = "NO_EXACT_EXTENT", one = "SELECTED_EXACT_EXTENT", many = "CONFLICT_MULTIPLE_EXACT", tieBreak = "FORBIDDEN" },
                outcomes = aggregate,
                selected = aggregate.Count(item => item.Outcome == "SELECTED_EXACT_EXTENT"),
                unresolved = aggregate.Count(item => item.Outcome == "NO_EXACT_EXTENT"),
                conflicts = aggregate.Count(item => item.Outcome == "CONFLICT_MULTIPLE_EXACT"),
            },
            conclusion = "H1_INDEPENDENT_JUDGMENTS_CAPTURED_AND_AGGREGATED_WITHOUT_GOLD; MULTIPLE_POSITIVE_CANDIDATES_ARE_FAIL_CLOSED_CONFLICTS_NOT_TIE_BROKEN; SEMANTIC_GOLD_SCORING_REQUIRES_SEPARATE_AUTHORIZATION",
        });
    }

    [Fact]
    public void P6TH1_frozen_independent_judgments_are_forensically_scored_against_preregistered_exact_candidates()
    {
        using var f1Retry = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{F1Root}/retry-src089-result.v1.json")));
        using var g1 = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(G1Path)));
        using var gold = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(Gold089Path)));
        using var g2aRaw = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{AnchorExistenceCaptureRoot}/SRC-089.raw-capture.v1.json")));
        using var execution = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{IndependentJudgmentCaptureRoot}/result.v1.json")));
        var prepared = Prepare("SRC-089", SourcePdfCorpus.Src089, f1Retry.RootElement.GetProperty("row"), g1.RootElement.GetProperty("src089"));
        var anchorRequest = ComposeAnchorExistence(prepared);
        var anchorLedger = ParseAnchorLedger(anchorRequest, g2aRaw.RootElement.GetProperty("rawResponse").GetString()!);
        var hasPrimaries = anchorLedger.Decisions.Select(item => JsonDocument.Parse(JsonSerializer.Serialize(item)))
            .Where(item => item.RootElement.GetProperty("anchor").GetString() == "HAS_STRUCTURAL_EXTENT")
            .Select(item => item.RootElement.GetProperty("primary").GetString()!).ToHashSet(StringComparer.Ordinal);
        var baseline = ComposeExactExtentFromHas(prepared, hasPrimaries);
        var requests = ComposeIndependentCandidateJudgments(prepared, baseline);
        var universe = prepared.SourcePack.Universe.Candidates.ToDictionary(candidate => candidate.Id, StringComparer.Ordinal);

        var preregisteredExact = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["O9"] = "C50",
            ["O17"] = "C104",
            ["O19"] = "C115",
            ["O27"] = "C174",
            ["O54"] = "C353",
            ["O83"] = "C542",
        };
        var goldByPrimary = gold.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray()
            .Select(claim => new
            {
                identity = claim.GetProperty("identity").GetString()!,
                primaryAlias = claim.GetProperty("sourceParts").EnumerateArray().First().GetProperty("sourceAlias").GetString()!,
            })
            .Where(claim => prepared.OccurrenceByAlias.TryGetValue(claim.primaryAlias, out var occurrence) && preregisteredExact.ContainsKey(occurrence))
            .ToDictionary(claim => prepared.OccurrenceByAlias[claim.primaryAlias], claim => claim.identity, StringComparer.Ordinal);
        Assert.Equal(6, goldByPrimary.Count);
        foreach (var (primary, candidateId) in preregisteredExact)
            Assert.Equal(goldByPrimary[primary], universe[candidateId].SpanIdentity);

        var judgments = requests.Select(request =>
        {
            var rawPath = TestRepository.Path($"{IndependentJudgmentCaptureRoot}/{request.Primary}-{request.CandidateId}.raw-capture.v1.json");
            using var raw = JsonDocument.Parse(File.ReadAllText(rawPath));
            return ParseIndependentJudgment(request, raw.RootElement.GetProperty("rawResponse").GetString()!);
        }).ToDictionary(item => $"{item.Primary}|{item.Candidate}", StringComparer.Ordinal);
        Assert.Equal(30, judgments.Count);

        string Classify(string primary, string candidateId)
        {
            var candidate = universe[candidateId];
            var exact = universe[preregisteredExact[primary]];
            if (candidate.SpanIdentity == exact.SpanIdentity) return "GOLD_EXACT";
            if (candidate.Kind.ToString() == "WHOLE") return "WHOLE_PRIMARY";
            var candidateWithinExact = candidate.Endpoint.Parts.All(part => exact.Endpoint.Parts.Any(goldPart =>
                goldPart.Alias == part.Alias && goldPart.Start <= part.Start && part.End <= goldPart.End));
            if (candidateWithinExact) return "UNDEREXTENT";
            var exactWithinCandidate = exact.Endpoint.Parts.All(goldPart => candidate.Endpoint.Parts.Any(part =>
                part.Alias == goldPart.Alias && part.Start <= goldPart.Start && goldPart.End <= part.End));
            if (exactWithinCandidate) return "OVEREXTENT";
            var overlapsExact = candidate.Endpoint.Parts.Any(part => exact.Endpoint.Parts.Any(goldPart =>
                goldPart.Alias == part.Alias && part.Start < goldPart.End && goldPart.Start < part.End));
            return overlapsExact ? "WRONG_PARTS" : "OTHER_MULTIPART";
        }

        var candidateRows = requests.Select(request =>
        {
            var judgment = judgments[$"{request.Primary}|{request.CandidateId}"].Judgment;
            var classification = Classify(request.Primary, request.CandidateId);
            return new
            {
                primary = request.Primary,
                candidate = request.CandidateId,
                candidateKind = request.CandidateKind,
                candidateIdentity = request.CandidateIdentity,
                classification,
                modelJudgment = judgment,
                modelExact = judgment == "EXACT_STRUCTURAL_EXTENT",
            };
        }).ToArray();
        var exactRows = candidateRows.Where(row => row.classification == "GOLD_EXACT").ToArray();
        Assert.Equal(6, exactRows.Length);
        Assert.Equal(1, exactRows.Count(row => row.modelExact));
        Assert.Equal(5, exactRows.Count(row => !row.modelExact));
        Assert.Equal(10, candidateRows.Count(row => row.modelExact));
        Assert.Equal(9, candidateRows.Count(row => row.modelExact && row.classification != "GOLD_EXACT"));

        var aggregate = AggregateIndependentJudgments(judgments.Values, preregisteredExact.Keys)
            .ToDictionary(item => item.Primary, StringComparer.Ordinal);
        var primaryRows = preregisteredExact.Keys.OrderBy(primary => primary, StringComparer.Ordinal).Select(primary =>
        {
            var goldCandidate = preregisteredExact[primary];
            var outcome = aggregate[primary];
            var resolution = outcome.Outcome switch
            {
                "SELECTED_EXACT_EXTENT" when outcome.SelectedCandidate == goldCandidate => "EXACT_SELECTED",
                "SELECTED_EXACT_EXTENT" => "WRONG_SELECTED",
                "CONFLICT_MULTIPLE_EXACT" when outcome.ExactCandidates.Contains(goldCandidate, StringComparer.Ordinal) => "CONFLICT_WITH_GOLD_INCLUDED",
                "CONFLICT_MULTIPLE_EXACT" => "CONFLICT_GOLD_MISSING",
                _ => "NO_EXACT_EXTENT",
            };
            return new { primary, goldCandidate, positiveCandidates = outcome.ExactCandidates, aggregationOutcome = outcome.Outcome, resolution };
        }).ToArray();
        Assert.Equal(2, primaryRows.Count(row => row.resolution == "WRONG_SELECTED"));
        Assert.Equal(1, primaryRows.Count(row => row.resolution == "CONFLICT_WITH_GOLD_INCLUDED"));
        Assert.Equal(3, primaryRows.Count(row => row.resolution == "CONFLICT_GOLD_MISSING"));

        var confusion = candidateRows.GroupBy(row => row.classification, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new
            {
                classification = group.Key,
                total = group.Count(),
                modelExact = group.Count(row => row.modelExact),
                modelNotExact = group.Count(row => !row.modelExact),
            }).ToArray();
        FreezeArtifact.AssertJson(IndependentJudgmentGoldForensicRoot, "independent-candidate-gold-forensic.v1.json", new
        {
            schemaVersion = "v5-p6th1-independent-candidate-gold-forensic-v1",
            status = "GOLD_FORENSIC_COMPLETE",
            authority = new
            {
                goldSha256 = Hashing.Sha256(File.ReadAllText(TestRepository.Path(Gold089Path))),
                goldMutation = "NONE",
                preregisteredExactCandidates = preregisteredExact,
                executionResultSha256 = Hashing.Sha256(File.ReadAllText(TestRepository.Path($"{IndependentJudgmentCaptureRoot}/result.v1.json"))),
                g2aRawResponseSha256 = g2aRaw.RootElement.GetProperty("rawResponseSha256").GetString(),
                providerCallsDuringAudit = 0,
                runtimeChanged = false,
            },
            candidateJudgment = new
            {
                total = candidateRows.Length,
                trueExactAccepted = candidateRows.Count(row => row.modelExact && row.classification == "GOLD_EXACT"),
                falseExactAccepted = candidateRows.Count(row => row.modelExact && row.classification != "GOLD_EXACT"),
                goldExactRejected = candidateRows.Count(row => !row.modelExact && row.classification == "GOLD_EXACT"),
                precision = candidateRows.Count(row => row.modelExact) == 0 ? 0 : (double)candidateRows.Count(row => row.modelExact && row.classification == "GOLD_EXACT") / candidateRows.Count(row => row.modelExact),
                recall = (double)candidateRows.Count(row => row.modelExact && row.classification == "GOLD_EXACT") / exactRows.Length,
                confusion,
            },
            primaryResolution = new
            {
                exactSelected = primaryRows.Count(row => row.resolution == "EXACT_SELECTED"),
                wrongSelected = primaryRows.Count(row => row.resolution == "WRONG_SELECTED"),
                conflictWithGoldIncluded = primaryRows.Count(row => row.resolution == "CONFLICT_WITH_GOLD_INCLUDED"),
                conflictGoldMissing = primaryRows.Count(row => row.resolution == "CONFLICT_GOLD_MISSING"),
                noExactExtent = primaryRows.Count(row => row.resolution == "NO_EXACT_EXTENT"),
                rows = primaryRows,
            },
            rows = candidateRows,
            conclusion = "H1_REMOVES_ORDERED_MENU_COUPLING_BY_CONSTRUCTION_BUT_EXACT_CANDIDATE_DISCRIMINATION_FAILS_ON_THE_SRC089_PROBE; AGGREGATION_CONFLICTS_ARE_FAIL_CLOSED_AND_NOT_TIE_BROKEN",
        });
    }

    [Fact]
    public async Task Run_exactly_one_frozen_hash_permutation_call_only_when_explicitly_enabled_for_P6TG2D()
    {
        if (Environment.GetEnvironmentVariable(RunExactExtentPermutationVariable) is not ("1" or "true" or "TRUE")) return;

        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        Assert.False(string.IsNullOrWhiteSpace(apiKey), "OPENROUTER_API_KEY is required for the explicitly enabled P6T-G2D canary.");
        var capturePath = TestRepository.Path(ExactExtentPermutationCaptureRoot);
        Assert.False(Directory.Exists(capturePath), "P6T-G2D capture root already exists; automatic resume or resend is forbidden.");

        using var f1Retry = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{F1Root}/retry-src089-result.v1.json")));
        using var g1 = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(G1Path)));
        using var g2aRaw = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{AnchorExistenceCaptureRoot}/SRC-089.raw-capture.v1.json")));
        using var preflight = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{ExactExtentPermutationPreflightRoot}/permutation-preflight.v1.json")));
        var prepared = Prepare("SRC-089", SourcePdfCorpus.Src089, f1Retry.RootElement.GetProperty("row"), g1.RootElement.GetProperty("src089"));
        var anchorRequest = ComposeAnchorExistence(prepared);
        var anchorLedger = ParseAnchorLedger(anchorRequest, g2aRaw.RootElement.GetProperty("rawResponse").GetString()!);
        var hasPrimaries = anchorLedger.Decisions
            .Select(item => JsonDocument.Parse(JsonSerializer.Serialize(item)))
            .Where(item => item.RootElement.GetProperty("anchor").GetString() == "HAS_STRUCTURAL_EXTENT")
            .Select(item => item.RootElement.GetProperty("primary").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(6, hasPrimaries.Count);
        var baseline = ComposeExactExtentFromHas(prepared, hasPrimaries);
        var request = ComposeExactExtentWithHashPermutation(prepared, baseline, "G2D");
        var callPlan = preflight.RootElement.GetProperty("permutationArm");
        Assert.Equal(request.MessageHash, callPlan.GetProperty("userMessageSha256").GetString());
        Assert.Equal(request.MessageBytes, callPlan.GetProperty("userMessageUtf8Bytes").GetInt32());
        Assert.Equal(request.ProviderHash, callPlan.GetProperty("providerBodySha256").GetString());
        Assert.Equal(request.ProviderBytes, callPlan.GetProperty("providerBodyBytes").GetInt32());

        Directory.CreateDirectory(capturePath);
        WriteNew(Path.Combine(capturePath, "execution-reservation.v1.json"), new
        {
            schemaVersion = "v5-p6tg2d-execution-reservation-v1",
            status = "ONE_PRIMARY_SLOT_RESERVED",
            documentId = request.DocumentId,
            providerRequestHash = request.ProviderHash,
            providerRequestBytes = request.ProviderBytes,
            providerCallsBeforeSend = 0,
            retriesAllowed = 0,
            repairsAllowed = false,
            fallbacksAllowed = false,
            goldRead = false,
        });

        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var provider = new OpenRouterQualificationTransport(http, new RemoteInferenceOptions
        {
            ApiKey = apiKey!,
            Model = "qwen/qwen3.7-flash",
            OpenRouterProviderRoute = "Alibaba",
            TransientRequestRetries = 0,
            MaxParallelRequests = 1,
            ProviderTransportTimeoutSeconds = 300,
            RequireZeroDataRetention = false,
        });

        try
        {
            var observation = await provider.ExecuteObservedAsync(request.ProviderBody, request.MaxCompletionTokens,
                request.SystemPrompt, request.UserMessage, CancellationToken.None);
            Assert.Equal(0, observation.RetryCount);
            var rawCapture = new
            {
                schemaVersion = "v5-p6tg2d-raw-provider-capture-v1",
                documentId = request.DocumentId,
                packId = "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_001",
                provider = "OpenRouter",
                model = "qwen/qwen3.7-flash",
                providerRoute = "Alibaba",
                reasoningRequested = true,
                reasoningEffort = "OMITTED",
                providerRequestHash = request.ProviderHash,
                providerRequestBytes = request.ProviderBytes,
                semanticRequestHash = request.MessageHash,
                finishReason = observation.FinishReason,
                usage = observation.Usage,
                promptTokens = UsageInt(observation.Usage, "prompt_tokens"),
                completionTokens = UsageInt(observation.Usage, "completion_tokens"),
                reasoningTokens = UsageInt(observation.Usage, "reasoning_tokens"),
                reasoningExecutionConfirmed = UsageInt(observation.Usage, "reasoning_tokens") is > 0,
                sseEventCount = observation.SseEventCount,
                retryCount = observation.RetryCount,
                rawSseSha256 = Hashing.Sha256(observation.RawSse),
                rawResponseSha256 = Hashing.Sha256(observation.Content),
                rawSseUtf8Bytes = Encoding.UTF8.GetByteCount(observation.RawSse),
                rawResponseUtf8Bytes = Encoding.UTF8.GetByteCount(observation.Content),
                rawSse = observation.RawSse,
                rawResponse = observation.Content,
            };
            WriteNew(Path.Combine(capturePath, "SRC-089.raw-capture.v1.json"), rawCapture);
            ParsedLedger? parsed = null;
            string? parserError = null;
            try { parsed = ParseExactExtentLedger(request, observation.Content); }
            catch (Exception error) { parserError = error.GetType().Name + ": " + error.Message; }
            WriteNew(Path.Combine(capturePath, "result.v1.json"), new
            {
                schemaVersion = "v5-p6tg2d-one-primary-call-result-v1",
                status = "EXECUTION_SET_FROZEN",
                providerCalls = 1,
                retries = 0,
                repairs = 0,
                fallbacks = 0,
                goldRead = false,
                goldMutation = "NONE",
                runtimeChanged = false,
                rawCaptureSha256 = Hashing.Sha256(File.ReadAllText(Path.Combine(capturePath, "SRC-089.raw-capture.v1.json"))),
                row = new
                {
                    documentId = request.DocumentId,
                    transportStatus = "COMPLETED",
                    finishReason = observation.FinishReason,
                    parserStatus = parsed is null ? "REJECTED" : "PARSED",
                    parserError,
                    rawResponseSha256 = Hashing.Sha256(observation.Content),
                    promptTokens = UsageInt(observation.Usage, "prompt_tokens"),
                    completionTokens = UsageInt(observation.Usage, "completion_tokens"),
                    reasoningTokens = UsageInt(observation.Usage, "reasoning_tokens"),
                    retryCount = observation.RetryCount,
                    parsed,
                },
            });
            Assert.Equal("stop", observation.FinishReason);
            Assert.NotNull(parsed);
            Assert.Equal(6, parsed!.RawDecisions);
            Assert.Equal(6, parsed.AcceptedSelections);
            Assert.Equal(0, parsed.Quarantined);
            Assert.Equal(0, parsed.MissingPrimaries);
        }
        catch (Exception error) when (error is not Xunit.Sdk.XunitException)
        {
            WriteNew(Path.Combine(capturePath, "transport-failure.v1.json"), new
            {
                schemaVersion = "v5-p6tg2d-transport-failure-v1",
                documentId = request.DocumentId,
                providerRequestHash = request.ProviderHash,
                errorType = error.GetType().FullName,
                message = error.Message,
                retryCount = 0,
                goldRead = false,
            });
            WriteNew(Path.Combine(capturePath, "result.v1.json"), new
            {
                schemaVersion = "v5-p6tg2d-one-primary-call-result-v1",
                status = "EXECUTION_SET_FROZEN",
                providerCalls = 1,
                retries = 0,
                repairs = 0,
                fallbacks = 0,
                goldRead = false,
                goldMutation = "NONE",
                runtimeChanged = false,
                row = new { documentId = request.DocumentId, transportStatus = "FAILED", errorType = error.GetType().FullName, retryCount = 0 },
            });
            throw;
        }
    }

    [Fact]
    public void P6TG2D_preflight_freezes_gold_independent_hash_sorted_candidate_permutation()
    {
        using var f1Retry = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{F1Root}/retry-src089-result.v1.json")));
        using var g1 = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(G1Path)));
        using var g2aRaw = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{AnchorExistenceCaptureRoot}/SRC-089.raw-capture.v1.json")));
        using var g2b = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{ExactExtentPreflightRoot}/exact-extent-preflight.v1.json")));
        using var g2c = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{ExactExtentOrderPreflightRoot}/candidate-order-preflight.v1.json")));
        var prepared = Prepare("SRC-089", SourcePdfCorpus.Src089, f1Retry.RootElement.GetProperty("row"), g1.RootElement.GetProperty("src089"));
        var anchorRequest = ComposeAnchorExistence(prepared);
        var anchorLedger = ParseAnchorLedger(anchorRequest, g2aRaw.RootElement.GetProperty("rawResponse").GetString()!);
        var hasPrimaries = anchorLedger.Decisions
            .Select(item => JsonDocument.Parse(JsonSerializer.Serialize(item)))
            .Where(item => item.RootElement.GetProperty("anchor").GetString() == "HAS_STRUCTURAL_EXTENT")
            .Select(item => item.RootElement.GetProperty("primary").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(6, hasPrimaries.Count);

        var baseline = ComposeExactExtentFromHas(prepared, hasPrimaries);
        var reversed = ComposeExactExtentWithCandidateOrder(prepared, baseline, reverse: true);
        var permuted = ComposeExactExtentWithHashPermutation(prepared, baseline, "G2D");
        Assert.Equal(baseline.OccurrenceGroups.Count, permuted.OccurrenceGroups.Count);
        Assert.All(baseline.OccurrenceGroups, group =>
        {
            var reverseGroup = reversed.OccurrenceGroups.Single(item => item.PrimaryOccurrence == group.PrimaryOccurrence);
            var permutationGroup = permuted.OccurrenceGroups.Single(item => item.PrimaryOccurrence == group.PrimaryOccurrence);
            Assert.Equal(group.CandidateIds.ToHashSet(StringComparer.Ordinal), permutationGroup.CandidateIds.ToHashSet(StringComparer.Ordinal));
            Assert.Equal(group.CandidateIds.Reverse(), reverseGroup.CandidateIds);
            Assert.Equal(group.CandidateIds.OrderBy(id => Hashing.Sha256($"{group.PrimaryOccurrence}|{id}|G2D"), StringComparer.Ordinal), permutationGroup.CandidateIds);
        });
        Assert.NotEqual(baseline.MessageHash, permuted.MessageHash);
        Assert.NotEqual(reversed.MessageHash, permuted.MessageHash);
        Assert.NotEqual(baseline.ProviderHash, permuted.ProviderHash);
        Assert.NotEqual(reversed.ProviderHash, permuted.ProviderHash);

        var o9 = prepared.SourcePack.Universe.Candidates.Where(candidate => candidate.Id is "C50" or "C51")
            .OrderBy(candidate => candidate.Id, StringComparer.Ordinal).Select(candidate => new
            {
                candidate = candidate.Id,
                kind = candidate.Kind.ToString(),
                text = candidate.Text,
                spanIdentity = candidate.SpanIdentity,
                parts = candidate.Endpoint.Parts.Select(part => new { alias = part.Alias, start = part.Start, end = part.End }).ToArray(),
                baselinePosition = baseline.OccurrenceGroups.Single(group => group.PrimaryOccurrence == "O9").CandidateIds.ToList().IndexOf(candidate.Id) + 1,
                reversePosition = reversed.OccurrenceGroups.Single(group => group.PrimaryOccurrence == "O9").CandidateIds.ToList().IndexOf(candidate.Id) + 1,
                permutationPosition = permuted.OccurrenceGroups.Single(group => group.PrimaryOccurrence == "O9").CandidateIds.ToList().IndexOf(candidate.Id) + 1,
            }).ToArray();
        Assert.Equal(2, o9.Length);

        FreezeArtifact.AssertJson(ExactExtentPermutationPreflightRoot, "permutation-preflight.v1.json", new
        {
            schemaVersion = "v5-p6tg2d-permutation-preflight-v1",
            status = "PREPARED_NOT_AUTHORIZED",
            treatment = new
            {
                onlyIndependentVariable = "DETERMINISTIC_CANDIDATE_PERMUTATION",
                permutation = "OrderBy(SHA256(primaryOccurrence + '|' + candidateId + '|G2D'))",
                goldUsedForRequestConstruction = false,
                samePrimaries = true,
                sameCandidateIds = true,
                sameCandidateContentAndKinds = true,
                samePrompt = true,
                model = "qwen/qwen3.7-flash",
                provider = "alibaba",
                reasoning = new { enabled = true, effort = "OMITTED" },
            },
            sourceAuthority = new
            {
                g2aRawResponseSha256 = g2aRaw.RootElement.GetProperty("rawResponseSha256").GetString(),
                g2bPreflightSha256 = Hashing.Sha256(File.ReadAllText(TestRepository.Path($"{ExactExtentPreflightRoot}/exact-extent-preflight.v1.json"))),
                g2cPreflightSha256 = Hashing.Sha256(File.ReadAllText(TestRepository.Path($"{ExactExtentOrderPreflightRoot}/candidate-order-preflight.v1.json"))),
                hasPrimaries = permuted.OccurrenceGroups.Select(group => new { primary = group.PrimaryOccurrence, alias = group.PrimaryAlias }).ToArray(),
            },
            baseline = new { userMessageSha256 = baseline.MessageHash, providerBodySha256 = baseline.ProviderHash, providerBodyBytes = baseline.ProviderBytes },
            reverseArm = new { userMessageSha256 = reversed.MessageHash, providerBodySha256 = reversed.ProviderHash, providerBodyBytes = reversed.ProviderBytes },
            permutationArm = new
            {
                userMessageSha256 = permuted.MessageHash,
                userMessageUtf8Bytes = permuted.MessageBytes,
                providerBodySha256 = permuted.ProviderHash,
                providerBodyBytes = permuted.ProviderBytes,
                primaryCount = permuted.OccurrenceGroups.Count,
                optionsPerPrimary = permuted.OccurrenceGroups.Select(group => new { primary = group.PrimaryOccurrence, candidateIds = group.CandidateIds }).ToArray(),
            },
            o9Forensic = o9,
            execution = new { providerCalls = 0, retries = 0, repairs = 0, fallbacks = 0, goldRead = false, runtimeChanged = false },
            conclusion = "P6TG2D_PROVIDER_FREE_PERMUTATION_FROZEN; G2B_AND_G2C_ORDER_ARMS_PRESERVED; ONE_MATCHED_PROVIDER_CALL_REQUIRES_SEPARATE_AUTHORIZATION",
        });
    }

    [Fact]
    public void P6TG2C_frozen_order_arm_is_scored_offline_and_compared_with_baseline()
    {
        using var f1Retry = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{F1Root}/retry-src089-result.v1.json")));
        using var g1 = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(G1Path)));
        using var gold = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(Gold089Path)));
        using var g2bRaw = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{ExactExtentCaptureRoot}/SRC-089.raw-capture.v1.json")));
        using var g2cRaw = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{ExactExtentOrderCaptureRoot}/SRC-089.raw-capture.v1.json")));
        using var g2cResult = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{ExactExtentOrderCaptureRoot}/result.v1.json")));
        using var orderPreflight = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{ExactExtentOrderPreflightRoot}/candidate-order-preflight.v1.json")));

        Assert.Equal("stop", g2bRaw.RootElement.GetProperty("finishReason").GetString());
        Assert.Equal("stop", g2cRaw.RootElement.GetProperty("finishReason").GetString());
        Assert.Equal(1, g2cResult.RootElement.GetProperty("providerCalls").GetInt32());
        Assert.False(g2cResult.RootElement.GetProperty("goldRead").GetBoolean());

        var prepared = Prepare("SRC-089", SourcePdfCorpus.Src089, f1Retry.RootElement.GetProperty("row"), g1.RootElement.GetProperty("src089"));
        var anchorRequest = ComposeAnchorExistence(prepared);
        using var g2aRaw = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{AnchorExistenceCaptureRoot}/SRC-089.raw-capture.v1.json")));
        var anchorLedger = ParseAnchorLedger(anchorRequest, g2aRaw.RootElement.GetProperty("rawResponse").GetString()!);
        var hasPrimaries = anchorLedger.Decisions
            .Select(item => JsonDocument.Parse(JsonSerializer.Serialize(item)))
            .Where(item => item.RootElement.GetProperty("anchor").GetString() == "HAS_STRUCTURAL_EXTENT")
            .Select(item => item.RootElement.GetProperty("primary").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        var baseline = ComposeExactExtentFromHas(prepared, hasPrimaries);
        var reordered = ComposeExactExtentWithCandidateOrder(prepared, baseline, reverse: true);
        var baselineLedger = ParseExactExtentLedger(baseline, g2bRaw.RootElement.GetProperty("rawResponse").GetString()!);
        var reorderedLedger = ParseExactExtentLedger(reordered, g2cRaw.RootElement.GetProperty("rawResponse").GetString()!);
        Assert.Equal(6, baselineLedger.AcceptedSelections);
        Assert.Equal(6, reorderedLedger.AcceptedSelections);

        var baselineChoices = baselineLedger.Decisions.Select(item => JsonDocument.Parse(JsonSerializer.Serialize(item)))
            .ToDictionary(item => item.RootElement.GetProperty("primary").GetString()!, item => item.RootElement.GetProperty("candidate").GetString()!, StringComparer.Ordinal);
        var reorderedChoices = reorderedLedger.Decisions.Select(item => JsonDocument.Parse(JsonSerializer.Serialize(item)))
            .ToDictionary(item => item.RootElement.GetProperty("primary").GetString()!, item => item.RootElement.GetProperty("candidate").GetString()!, StringComparer.Ordinal);
        var goldClaims = gold.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray()
            .Select(claim => new
            {
                identity = claim.GetProperty("identity").GetString()!,
                primaryAlias = claim.GetProperty("sourceParts").EnumerateArray().First().GetProperty("sourceAlias").GetString()!,
            })
            .Where(claim => prepared.OccurrenceByAlias.ContainsKey(claim.primaryAlias))
            .ToArray();
        Assert.Equal(6, goldClaims.Length);

        var baselinePosition = baseline.OccurrenceGroups.ToDictionary(group => group.PrimaryOccurrence,
            group => group.CandidateIds.Select((id, index) => (id, index + 1)).ToDictionary(item => item.id, item => item.Item2, StringComparer.Ordinal), StringComparer.Ordinal);
        var reorderedPosition = reordered.OccurrenceGroups.ToDictionary(group => group.PrimaryOccurrence,
            group => group.CandidateIds.Select((id, index) => (id, index + 1)).ToDictionary(item => item.id, item => item.Item2, StringComparer.Ordinal), StringComparer.Ordinal);

        var rows = baselineChoices.Keys.OrderBy(value => value, StringComparer.Ordinal).Select(primary =>
        {
            var baselineId = baselineChoices[primary];
            var reorderedId = reorderedChoices[primary];
            var baselineCandidate = prepared.SourcePack.Universe.Candidates.Single(candidate => candidate.Id == baselineId);
            var reorderedCandidate = prepared.SourcePack.Universe.Candidates.Single(candidate => candidate.Id == reorderedId);
            var primaryAlias = baseline.OccurrenceGroups.Single(group => group.PrimaryOccurrence == primary).PrimaryAlias;
            var goldClaim = goldClaims.Single(claim => claim.primaryAlias == primaryAlias);
            var goldCandidate = prepared.SourcePack.Universe.Candidates.Single(candidate => candidate.SpanIdentity == goldClaim.identity);
            var baselineExact = baselineCandidate.SpanIdentity == goldClaim.identity;
            var reorderedExact = reorderedCandidate.SpanIdentity == goldClaim.identity;
            return new
            {
                primary,
                goldCandidate = goldCandidate.Id,
                goldKind = goldCandidate.Kind.ToString(),
                baselineCandidate = baselineId,
                baselineKind = baselineCandidate.Kind.ToString(),
                baselinePosition = baselinePosition[primary][baselineId],
                baselineExact,
                reorderedCandidate = reorderedId,
                reorderedKind = reorderedCandidate.Kind.ToString(),
                reorderedPosition = reorderedPosition[primary][reorderedId],
                reorderedExact,
                sameAsBaselineChoice = baselineId == reorderedId,
                baselineWholeToReorderedMultipart = baselineCandidate.Kind.ToString() == "WHOLE" && reorderedCandidate.Kind.ToString() == "MULTIPART",
                transition = baselineExact && reorderedExact ? "EXACT_TO_EXACT" : baselineExact ? "EXACT_TO_WRONG" : reorderedExact ? "WRONG_TO_EXACT" : "WRONG_TO_WRONG",
            };
        }).ToArray();

        Assert.Equal(6, rows.Length);
        Assert.Equal(1, rows.Count(row => row.sameAsBaselineChoice));
        Assert.Equal(5, rows.Count(row => !row.sameAsBaselineChoice));
        Assert.Equal(5, rows.Count(row => row.reorderedExact));
        Assert.Equal(4, rows.Count(row => row.goldKind == "MULTIPART" && row.reorderedExact));
        Assert.Equal(1, rows.Count(row => row.goldKind == "WHOLE" && row.reorderedExact));
        Assert.Equal(1, rows.Count(row => row.baselineExact && row.reorderedExact));
        Assert.Equal(4, rows.Count(row => !row.baselineExact && row.reorderedExact));
        Assert.Equal(0, rows.Count(row => row.baselineExact && !row.reorderedExact));

        FreezeArtifact.AssertJson(ExactExtentOrderAuditRoot, "candidate-order-gold-audit.v1.json", new
        {
            schemaVersion = "v5-p6tg2c-candidate-order-gold-audit-v1",
            authority = new
            {
                baselineRawCaptureSha256 = Hashing.Sha256(File.ReadAllText(TestRepository.Path($"{ExactExtentCaptureRoot}/SRC-089.raw-capture.v1.json"))),
                reorderedRawCaptureSha256 = Hashing.Sha256(File.ReadAllText(TestRepository.Path($"{ExactExtentOrderCaptureRoot}/SRC-089.raw-capture.v1.json"))),
                baselineRawResponseSha256 = g2bRaw.RootElement.GetProperty("rawResponseSha256").GetString(),
                reorderedRawResponseSha256 = g2cRaw.RootElement.GetProperty("rawResponseSha256").GetString(),
                orderPreflightSha256 = Hashing.Sha256(File.ReadAllText(TestRepository.Path($"{ExactExtentOrderPreflightRoot}/candidate-order-preflight.v1.json"))),
                goldSha256 = Hashing.Sha256(File.ReadAllText(TestRepository.Path(Gold089Path))),
                providerCallsDuringAudit = 0,
                goldMutation = "NONE",
                runtimeChanged = false,
            },
            execution = new
            {
                baselineFinishReason = g2bRaw.RootElement.GetProperty("finishReason").GetString(),
                reorderedFinishReason = g2cRaw.RootElement.GetProperty("finishReason").GetString(),
                baselineParserAccepted = baselineLedger.Quarantined == 0 && baselineLedger.MissingPrimaries == 0,
                reorderedParserAccepted = reorderedLedger.Quarantined == 0 && reorderedLedger.MissingPrimaries == 0,
                baselineReasoningTokens = g2bRaw.RootElement.GetProperty("reasoningTokens").GetInt32(),
                reorderedReasoningTokens = g2cRaw.RootElement.GetProperty("reasoningTokens").GetInt32(),
            },
            metrics = new
            {
                baselineExact = new { correct = rows.Count(row => row.baselineExact), total = rows.Length },
                reorderedExact = new { correct = rows.Count(row => row.reorderedExact), total = rows.Length },
                baselineMultipartExact = new { correct = rows.Count(row => row.goldKind == "MULTIPART" && row.baselineExact), total = rows.Count(row => row.goldKind == "MULTIPART") },
                reorderedMultipartExact = new { correct = rows.Count(row => row.goldKind == "MULTIPART" && row.reorderedExact), total = rows.Count(row => row.goldKind == "MULTIPART") },
                singletonExact = new { correct = rows.Count(row => row.goldKind == "WHOLE" && row.reorderedExact), total = rows.Count(row => row.goldKind == "WHOLE") },
                sameAsBaselineChoice = rows.Count(row => row.sameAsBaselineChoice),
                changedFromBaseline = rows.Count(row => !row.sameAsBaselineChoice),
                baselineWrongToReorderedExact = rows.Count(row => !row.baselineExact && row.reorderedExact),
                baselineExactToReorderedWrong = rows.Count(row => row.baselineExact && !row.reorderedExact),
                baselineExactToReorderedExact = rows.Count(row => row.baselineExact && row.reorderedExact),
                reorderedFirstCandidate = rows.Count(row => row.reorderedPosition == 1),
                reorderedWhole = rows.Count(row => row.reorderedKind == "WHOLE"),
                reorderedMultipart = rows.Count(row => row.reorderedKind == "MULTIPART"),
            },
            rows,
            conclusion = "P6TG2C_ORDER_ARM_PROVIDER_AND_CONTRACT_PASS; CANDIDATE_ORDER_AFFECTS_SELECTION; FIVE_OF_SIX_EXACT_WITH_FOUR_OF_FIVE_MULTIPART; POSITIONAL_FIRST_ONLY_ONE_OF_SIX; WHOLE_ATOMICITY_PRIOR_NOT_SUPPORTED_AS_SOLE_EXPLANATION",
        });
    }

    [Fact]
    public async Task Run_exactly_one_frozen_candidate_order_call_only_when_explicitly_enabled_for_P6TG2C()
    {
        if (Environment.GetEnvironmentVariable(RunExactExtentOrderVariable) is not ("1" or "true" or "TRUE")) return;

        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        Assert.False(string.IsNullOrWhiteSpace(apiKey), "OPENROUTER_API_KEY is required for the explicitly enabled P6T-G2C canary.");
        var capturePath = TestRepository.Path(ExactExtentOrderCaptureRoot);
        Assert.False(Directory.Exists(capturePath), "P6T-G2C capture root already exists; automatic resume or resend is forbidden.");

        using var f1Retry = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{F1Root}/retry-src089-result.v1.json")));
        using var g1 = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(G1Path)));
        using var g2aRaw = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{AnchorExistenceCaptureRoot}/SRC-089.raw-capture.v1.json")));
        using var preflight = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{ExactExtentOrderPreflightRoot}/candidate-order-preflight.v1.json")));
        var prepared = Prepare("SRC-089", SourcePdfCorpus.Src089, f1Retry.RootElement.GetProperty("row"), g1.RootElement.GetProperty("src089"));
        var anchorRequest = ComposeAnchorExistence(prepared);
        var anchorLedger = ParseAnchorLedger(anchorRequest, g2aRaw.RootElement.GetProperty("rawResponse").GetString()!);
        var hasPrimaries = anchorLedger.Decisions
            .Select(item => JsonDocument.Parse(JsonSerializer.Serialize(item)))
            .Where(item => item.RootElement.GetProperty("anchor").GetString() == "HAS_STRUCTURAL_EXTENT")
            .Select(item => item.RootElement.GetProperty("primary").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(6, hasPrimaries.Count);
        var baseline = ComposeExactExtentFromHas(prepared, hasPrimaries);
        var request = ComposeExactExtentWithCandidateOrder(prepared, baseline, reverse: true);
        var frozen = preflight.RootElement;
        Assert.Equal("PREPARED_NOT_AUTHORIZED", frozen.GetProperty("status").GetString());
        var callPlan = frozen.GetProperty("candidateOrderArm");
        Assert.Equal(request.MessageHash, callPlan.GetProperty("userMessageSha256").GetString());
        Assert.Equal(request.MessageBytes, callPlan.GetProperty("userMessageUtf8Bytes").GetInt32());
        Assert.Equal(request.ProviderHash, callPlan.GetProperty("providerBodySha256").GetString());
        Assert.Equal(request.ProviderBytes, callPlan.GetProperty("providerBodyBytes").GetInt32());

        Directory.CreateDirectory(capturePath);
        WriteNew(Path.Combine(capturePath, "execution-reservation.v1.json"), new
        {
            schemaVersion = "v5-p6tg2c-execution-reservation-v1",
            status = "ONE_PRIMARY_SLOT_RESERVED",
            documentId = request.DocumentId,
            providerRequestHash = request.ProviderHash,
            providerRequestBytes = request.ProviderBytes,
            providerCallsBeforeSend = 0,
            retriesAllowed = 0,
            repairsAllowed = false,
            fallbacksAllowed = false,
            goldRead = false,
        });

        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var provider = new OpenRouterQualificationTransport(http, new RemoteInferenceOptions
        {
            ApiKey = apiKey!,
            Model = "qwen/qwen3.7-flash",
            OpenRouterProviderRoute = "Alibaba",
            TransientRequestRetries = 0,
            MaxParallelRequests = 1,
            ProviderTransportTimeoutSeconds = 300,
            RequireZeroDataRetention = false,
        });

        try
        {
            var observation = await provider.ExecuteObservedAsync(request.ProviderBody, request.MaxCompletionTokens,
                request.SystemPrompt, request.UserMessage, CancellationToken.None);
            Assert.Equal(0, observation.RetryCount);
            var rawCapture = new
            {
                schemaVersion = "v5-p6tg2c-raw-provider-capture-v1",
                documentId = request.DocumentId,
                packId = "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_001",
                provider = "OpenRouter",
                model = "qwen/qwen3.7-flash",
                providerRoute = "Alibaba",
                reasoningRequested = true,
                reasoningEffort = "OMITTED",
                providerRequestHash = request.ProviderHash,
                providerRequestBytes = request.ProviderBytes,
                semanticRequestHash = request.MessageHash,
                finishReason = observation.FinishReason,
                usage = observation.Usage,
                promptTokens = UsageInt(observation.Usage, "prompt_tokens"),
                completionTokens = UsageInt(observation.Usage, "completion_tokens"),
                reasoningTokens = UsageInt(observation.Usage, "reasoning_tokens"),
                reasoningExecutionConfirmed = UsageInt(observation.Usage, "reasoning_tokens") is > 0,
                sseEventCount = observation.SseEventCount,
                retryCount = observation.RetryCount,
                rawSseSha256 = Hashing.Sha256(observation.RawSse),
                rawResponseSha256 = Hashing.Sha256(observation.Content),
                rawSseUtf8Bytes = Encoding.UTF8.GetByteCount(observation.RawSse),
                rawResponseUtf8Bytes = Encoding.UTF8.GetByteCount(observation.Content),
                rawSse = observation.RawSse,
                rawResponse = observation.Content,
            };
            WriteNew(Path.Combine(capturePath, "SRC-089.raw-capture.v1.json"), rawCapture);
            ParsedLedger? parsed = null;
            string? parserError = null;
            try { parsed = ParseExactExtentLedger(request, observation.Content); }
            catch (Exception error) { parserError = error.GetType().Name + ": " + error.Message; }
            WriteNew(Path.Combine(capturePath, "result.v1.json"), new
            {
                schemaVersion = "v5-p6tg2c-one-primary-call-result-v1",
                status = "EXECUTION_SET_FROZEN",
                providerCalls = 1,
                retries = 0,
                repairs = 0,
                fallbacks = 0,
                goldRead = false,
                goldMutation = "NONE",
                runtimeChanged = false,
                rawCaptureSha256 = Hashing.Sha256(File.ReadAllText(Path.Combine(capturePath, "SRC-089.raw-capture.v1.json"))),
                row = new
                {
                    documentId = request.DocumentId,
                    transportStatus = "COMPLETED",
                    finishReason = observation.FinishReason,
                    parserStatus = parsed is null ? "REJECTED" : "PARSED",
                    parserError,
                    rawResponseSha256 = Hashing.Sha256(observation.Content),
                    promptTokens = UsageInt(observation.Usage, "prompt_tokens"),
                    completionTokens = UsageInt(observation.Usage, "completion_tokens"),
                    reasoningTokens = UsageInt(observation.Usage, "reasoning_tokens"),
                    retryCount = observation.RetryCount,
                    parsed,
                },
            });
            Assert.Equal("stop", observation.FinishReason);
            Assert.NotNull(parsed);
            Assert.Equal(6, parsed!.RawDecisions);
            Assert.Equal(6, parsed.AcceptedSelections);
            Assert.Equal(0, parsed.Quarantined);
            Assert.Equal(0, parsed.MissingPrimaries);
        }
        catch (Exception error) when (error is not Xunit.Sdk.XunitException)
        {
            WriteNew(Path.Combine(capturePath, "transport-failure.v1.json"), new
            {
                schemaVersion = "v5-p6tg2c-transport-failure-v1",
                documentId = request.DocumentId,
                providerRequestHash = request.ProviderHash,
                errorType = error.GetType().FullName,
                message = error.Message,
                retryCount = 0,
                goldRead = false,
            });
            WriteNew(Path.Combine(capturePath, "result.v1.json"), new
            {
                schemaVersion = "v5-p6tg2c-one-primary-call-result-v1",
                status = "EXECUTION_SET_FROZEN",
                providerCalls = 1,
                retries = 0,
                repairs = 0,
                fallbacks = 0,
                goldRead = false,
                goldMutation = "NONE",
                runtimeChanged = false,
                row = new { documentId = request.DocumentId, transportStatus = "FAILED", errorType = error.GetType().FullName, retryCount = 0 },
            });
            throw;
        }
    }

    [Fact]
    public async Task Run_exactly_one_frozen_primary_call_only_when_explicitly_enabled_for_P6TG2B()
    {
        if (Environment.GetEnvironmentVariable(RunExactExtentVariable) is not ("1" or "true" or "TRUE")) return;

        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        Assert.False(string.IsNullOrWhiteSpace(apiKey), "OPENROUTER_API_KEY is required for the explicitly enabled P6T-G2B canary.");
        var capturePath = TestRepository.Path(ExactExtentCaptureRoot);
        Assert.False(Directory.Exists(capturePath), "P6T-G2B capture root already exists; automatic resume or resend is forbidden.");

        using var f1Retry = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{F1Root}/retry-src089-result.v1.json")));
        using var g1 = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(G1Path)));
        using var g2aRaw = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{AnchorExistenceCaptureRoot}/SRC-089.raw-capture.v1.json")));
        using var preflight = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{ExactExtentPreflightRoot}/exact-extent-preflight.v1.json")));
        var prepared = Prepare("SRC-089", SourcePdfCorpus.Src089, f1Retry.RootElement.GetProperty("row"), g1.RootElement.GetProperty("src089"));
        var anchorRequest = ComposeAnchorExistence(prepared);
        var anchorLedger = ParseAnchorLedger(anchorRequest, g2aRaw.RootElement.GetProperty("rawResponse").GetString()!);
        var hasPrimaries = anchorLedger.Decisions
            .Select(item => JsonDocument.Parse(JsonSerializer.Serialize(item)))
            .Where(item => item.RootElement.GetProperty("anchor").GetString() == "HAS_STRUCTURAL_EXTENT")
            .Select(item => item.RootElement.GetProperty("primary").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(6, hasPrimaries.Count);
        var request = ComposeExactExtentFromHas(prepared, hasPrimaries);

        var frozen = preflight.RootElement;
        Assert.Equal("PREPARED_NOT_AUTHORIZED", frozen.GetProperty("status").GetString());
        var callPlan = frozen.GetProperty("callPlan");
        AssertFrozenParity(request, callPlan);
        Assert.Equal(6, callPlan.GetProperty("primaryCount").GetInt32());
        Assert.Equal(hasPrimaries.Order(StringComparer.Ordinal),
            callPlan.GetProperty("primaryOccurrences").EnumerateArray().Select(item => item.GetProperty("occurrence").GetString()!).Order(StringComparer.Ordinal));

        Directory.CreateDirectory(capturePath);
        WriteNew(Path.Combine(capturePath, "execution-reservation.v1.json"), new
        {
            schemaVersion = "v5-p6tg2b-execution-reservation-v1",
            status = "ONE_PRIMARY_SLOT_RESERVED",
            documentId = request.DocumentId,
            providerRequestHash = request.ProviderHash,
            providerRequestBytes = request.ProviderBytes,
            providerCallsBeforeSend = 0,
            retriesAllowed = 0,
            repairsAllowed = false,
            fallbacksAllowed = false,
            goldRead = false,
        });

        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var provider = new OpenRouterQualificationTransport(http, new RemoteInferenceOptions
        {
            ApiKey = apiKey!,
            Model = "qwen/qwen3.7-flash",
            OpenRouterProviderRoute = "Alibaba",
            TransientRequestRetries = 0,
            MaxParallelRequests = 1,
            ProviderTransportTimeoutSeconds = 300,
            RequireZeroDataRetention = false,
        });

        try
        {
            var observation = await provider.ExecuteObservedAsync(request.ProviderBody, request.MaxCompletionTokens,
                request.SystemPrompt, request.UserMessage, CancellationToken.None);
            Assert.Equal(0, observation.RetryCount);
            var rawCapture = new
            {
                schemaVersion = "v5-p6tg2b-raw-provider-capture-v1",
                documentId = request.DocumentId,
                packId = "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_001",
                provider = "OpenRouter",
                model = "qwen/qwen3.7-flash",
                providerRoute = "Alibaba",
                reasoningRequested = true,
                reasoningEffort = "OMITTED",
                providerRequestHash = request.ProviderHash,
                providerRequestBytes = request.ProviderBytes,
                semanticRequestHash = request.MessageHash,
                finishReason = observation.FinishReason,
                usage = observation.Usage,
                promptTokens = UsageInt(observation.Usage, "prompt_tokens"),
                completionTokens = UsageInt(observation.Usage, "completion_tokens"),
                reasoningTokens = UsageInt(observation.Usage, "reasoning_tokens"),
                reasoningExecutionConfirmed = UsageInt(observation.Usage, "reasoning_tokens") is > 0,
                sseEventCount = observation.SseEventCount,
                retryCount = observation.RetryCount,
                rawSseSha256 = Hashing.Sha256(observation.RawSse),
                rawResponseSha256 = Hashing.Sha256(observation.Content),
                rawSseUtf8Bytes = Encoding.UTF8.GetByteCount(observation.RawSse),
                rawResponseUtf8Bytes = Encoding.UTF8.GetByteCount(observation.Content),
                rawSse = observation.RawSse,
                rawResponse = observation.Content,
            };
            WriteNew(Path.Combine(capturePath, "SRC-089.raw-capture.v1.json"), rawCapture);

            ParsedLedger? parsed = null;
            string? parserError = null;
            try { parsed = ParseExactExtentLedger(request, observation.Content); }
            catch (Exception error) { parserError = error.GetType().Name + ": " + error.Message; }
            WriteNew(Path.Combine(capturePath, "result.v1.json"), new
            {
                schemaVersion = "v5-p6tg2b-one-primary-call-result-v1",
                status = "EXECUTION_SET_FROZEN",
                providerCalls = 1,
                retries = 0,
                repairs = 0,
                fallbacks = 0,
                goldRead = false,
                goldMutation = "NONE",
                runtimeChanged = false,
                rawCaptureSha256 = Hashing.Sha256(File.ReadAllText(Path.Combine(capturePath, "SRC-089.raw-capture.v1.json"))),
                row = new
                {
                    documentId = request.DocumentId,
                    transportStatus = "COMPLETED",
                    finishReason = observation.FinishReason,
                    parserStatus = parsed is null ? "REJECTED" : "PARSED",
                    parserError,
                    rawResponseSha256 = Hashing.Sha256(observation.Content),
                    promptTokens = UsageInt(observation.Usage, "prompt_tokens"),
                    completionTokens = UsageInt(observation.Usage, "completion_tokens"),
                    reasoningTokens = UsageInt(observation.Usage, "reasoning_tokens"),
                    retryCount = observation.RetryCount,
                    parsed,
                },
            });
            Assert.Equal("stop", observation.FinishReason);
            Assert.NotNull(parsed);
            Assert.Equal(6, parsed!.RawDecisions);
            Assert.Equal(6, parsed.AcceptedSelections);
            Assert.Equal(0, parsed.NoStructuralExtent);
            Assert.Equal(0, parsed.Quarantined);
            Assert.Equal(0, parsed.MissingPrimaries);
        }
        catch (Exception error) when (error is not Xunit.Sdk.XunitException)
        {
            WriteNew(Path.Combine(capturePath, "transport-failure.v1.json"), new
            {
                schemaVersion = "v5-p6tg2b-transport-failure-v1",
                documentId = request.DocumentId,
                providerRequestHash = request.ProviderHash,
                errorType = error.GetType().FullName,
                message = error.Message,
                retryCount = 0,
                goldRead = false,
            });
            WriteNew(Path.Combine(capturePath, "result.v1.json"), new
            {
                schemaVersion = "v5-p6tg2b-one-primary-call-result-v1",
                status = "EXECUTION_SET_FROZEN",
                providerCalls = 1,
                retries = 0,
                repairs = 0,
                fallbacks = 0,
                goldRead = false,
                goldMutation = "NONE",
                runtimeChanged = false,
                row = new { documentId = request.DocumentId, transportStatus = "FAILED", errorType = error.GetType().FullName, retryCount = 0 },
            });
            throw;
        }
    }

    [Fact]
    public void P6TG2B_frozen_capture_is_scored_offline_against_the_six_gold_extents()
    {
        using var f1Retry = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{F1Root}/retry-src089-result.v1.json")));
        using var g1 = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(G1Path)));
        using var gold = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(Gold089Path)));
        using var preflight = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{ExactExtentPreflightRoot}/exact-extent-preflight.v1.json")));
        using var raw = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{ExactExtentCaptureRoot}/SRC-089.raw-capture.v1.json")));
        using var result = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{ExactExtentCaptureRoot}/result.v1.json")));

        Assert.Equal("EXECUTION_SET_FROZEN", result.RootElement.GetProperty("status").GetString());
        Assert.Equal(1, result.RootElement.GetProperty("providerCalls").GetInt32());
        Assert.Equal(0, result.RootElement.GetProperty("retries").GetInt32());
        Assert.Equal(0, result.RootElement.GetProperty("repairs").GetInt32());
        Assert.Equal(0, result.RootElement.GetProperty("fallbacks").GetInt32());
        Assert.False(result.RootElement.GetProperty("goldRead").GetBoolean());
        Assert.Equal("stop", raw.RootElement.GetProperty("finishReason").GetString());

        var prepared = Prepare("SRC-089", SourcePdfCorpus.Src089, f1Retry.RootElement.GetProperty("row"), g1.RootElement.GetProperty("src089"));
        var selectedRows = JsonDocument.Parse(raw.RootElement.GetProperty("rawResponse").GetString()!).RootElement
            .GetProperty("decisions").EnumerateArray()
            .ToDictionary(item => item.GetProperty("primary").GetString()!, item => item.GetProperty("candidate").GetString()!, StringComparer.Ordinal);
        var frozenOptions = preflight.RootElement.GetProperty("callPlan").GetProperty("optionsPerPrimary").EnumerateArray()
            .ToDictionary(item => item.GetProperty("primary").GetString()!, item => item.GetProperty("candidateIds").EnumerateArray().Select(value => value.GetString()!).ToArray(), StringComparer.Ordinal);
        Assert.Equal(6, selectedRows.Count);

        var selectedPrimaries = selectedRows.Keys.ToHashSet(StringComparer.Ordinal);
        var goldClaims = gold.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray()
            .Select(claim => new
            {
                identity = claim.GetProperty("identity").GetString()!,
                primaryAlias = claim.GetProperty("sourceParts").EnumerateArray().First().GetProperty("sourceAlias").GetString()!,
            })
            .Where(claim => selectedPrimaries.Any(primary => prepared.OccurrenceByAlias.TryGetValue(claim.primaryAlias, out var occurrence) && occurrence == primary))
            .ToArray();
        Assert.Equal(6, goldClaims.Length);

        var rows = selectedRows.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item =>
        {
            var primary = item.Key;
            var selectedId = item.Value;
            var group = prepared.SourcePack.Universe.Candidates.Single(candidate => candidate.Id == selectedId);
            var options = frozenOptions[primary];
            Assert.Equal(options[0], selectedId);
            var goldClaim = goldClaims.Single(claim => prepared.OccurrenceByAlias[claim.primaryAlias] == primary);
            var exact = group.SpanIdentity == goldClaim.identity;
            var selectedWhole = string.Equals(group.Kind.ToString(), "WHOLE", StringComparison.Ordinal) && group.Endpoint.Parts.Count == 1;
            var goldCandidate = prepared.SourcePack.Universe.Candidates.Single(candidate => candidate.SpanIdentity == goldClaim.identity);
            var goldMultipart = string.Equals(goldCandidate.Kind.ToString(), "MULTIPART", StringComparison.Ordinal);
            return new
            {
                primary,
                selectedCandidate = selectedId,
                firstCandidate = options[0],
                selectedFirstCandidate = selectedId == options[0],
                selectedKind = group.Kind.ToString(),
                selectedWholePrimary = selectedWhole,
                goldIdentity = goldClaim.identity,
                goldKind = goldCandidate.Kind.ToString(),
                goldMultipart,
                exact,
                multipartExact = goldMultipart && exact,
                multipartUnder = goldMultipart && selectedWhole,
                selectedParts = group.Endpoint.Parts.Select(part => part.Alias).ToArray(),
                goldParts = goldCandidate.Endpoint.Parts.Select(part => part.Alias).ToArray(),
            };
        }).ToArray();

        Assert.Equal(6, rows.Length);
        Assert.Equal(6, rows.Count(row => row.selectedFirstCandidate));
        Assert.Equal(6, rows.Count(row => row.selectedWholePrimary));
        Assert.Equal(1, rows.Count(row => row.exact));
        Assert.Equal(0, rows.Count(row => row.multipartExact));
        Assert.Equal(5, rows.Count(row => row.multipartUnder));
        Assert.Equal(1, rows.Count(row => !row.goldMultipart && row.exact));

        FreezeArtifact.AssertJson(ExactExtentAuditRoot, "exact-extent-gold-audit.v1.json", new
        {
            schemaVersion = "v5-p6tg2b-exact-extent-gold-audit-v1",
            authority = new
            {
                rawCaptureSha256 = Hashing.Sha256(File.ReadAllText(TestRepository.Path($"{ExactExtentCaptureRoot}/SRC-089.raw-capture.v1.json"))),
                rawResponseSha256 = raw.RootElement.GetProperty("rawResponseSha256").GetString(),
                resultSha256 = Hashing.Sha256(File.ReadAllText(TestRepository.Path($"{ExactExtentCaptureRoot}/result.v1.json"))),
                preflightSha256 = Hashing.Sha256(File.ReadAllText(TestRepository.Path($"{ExactExtentPreflightRoot}/exact-extent-preflight.v1.json"))),
                goldSha256 = Hashing.Sha256(File.ReadAllText(TestRepository.Path(Gold089Path))),
                providerCallsDuringAudit = 0,
                goldMutation = "NONE",
                runtimeChanged = false,
            },
            execution = new
            {
                finishReason = raw.RootElement.GetProperty("finishReason").GetString(),
                reasoningExecutionConfirmed = raw.RootElement.GetProperty("reasoningExecutionConfirmed").GetBoolean(),
                promptTokens = raw.RootElement.GetProperty("promptTokens").GetInt32(),
                completionTokens = raw.RootElement.GetProperty("completionTokens").GetInt32(),
                reasoningTokens = raw.RootElement.GetProperty("reasoningTokens").GetInt32(),
                rawDecisions = rows.Length,
                parserAccepted = result.RootElement.GetProperty("row").GetProperty("parserStatus").GetString() == "PARSED",
                quarantine = result.RootElement.GetProperty("row").GetProperty("parsed").GetProperty("Quarantined").GetInt32(),
            },
            metrics = new
            {
                exact = new { correct = rows.Count(row => row.exact), total = rows.Length },
                multipart = new { exact = rows.Count(row => row.multipartExact), under = rows.Count(row => row.multipartUnder), total = rows.Count(row => row.goldMultipart) },
                singleton = new { exact = rows.Count(row => !row.goldMultipart && row.exact), total = rows.Count(row => !row.goldMultipart) },
                selectedFirstCandidate = rows.Count(row => row.selectedFirstCandidate),
                selectedWholePrimary = rows.Count(row => row.selectedWholePrimary),
                primaryWholeSelectionBias = rows.All(row => row.selectedWholePrimary),
            },
            rows,
            conclusion = "G2B_TRANSPORT_AND_CONTRACT_PASS; EXACT_EXTENT_AUDIT_1_OF_6; MULTIPART_EXACT_0_OF_5; SINGLETON_EXACT_1_OF_1; PRIMARY_WHOLE_SELECTION_BIAS_CONFIRMED_ON_CANARY",
        });
    }

    [Fact]
    public void P6TG2C_preflight_reverses_only_candidate_order_and_preserves_the_frozen_extent_menu()
    {
        using var f1Retry = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{F1Root}/retry-src089-result.v1.json")));
        using var g1 = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(G1Path)));
        using var g2b = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{ExactExtentPreflightRoot}/exact-extent-preflight.v1.json")));
        using var g2aRaw = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{AnchorExistenceCaptureRoot}/SRC-089.raw-capture.v1.json")));
        var prepared = Prepare("SRC-089", SourcePdfCorpus.Src089, f1Retry.RootElement.GetProperty("row"), g1.RootElement.GetProperty("src089"));
        var anchorRequest = ComposeAnchorExistence(prepared);
        var anchorLedger = ParseAnchorLedger(anchorRequest, g2aRaw.RootElement.GetProperty("rawResponse").GetString()!);
        var hasPrimaries = anchorLedger.Decisions
            .Select(item => JsonDocument.Parse(JsonSerializer.Serialize(item)))
            .Where(item => item.RootElement.GetProperty("anchor").GetString() == "HAS_STRUCTURAL_EXTENT")
            .Select(item => item.RootElement.GetProperty("primary").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(6, hasPrimaries.Count);

        var baseline = ComposeExactExtentFromHas(prepared, hasPrimaries);
        var reversed = ComposeExactExtentWithCandidateOrder(prepared, baseline, reverse: true);
        var frozenCallPlan = g2b.RootElement.GetProperty("callPlan");
        Assert.Equal(baseline.MessageHash, frozenCallPlan.GetProperty("userMessageSha256").GetString());
        Assert.Equal(baseline.ProviderHash, frozenCallPlan.GetProperty("providerBodySha256").GetString());
        Assert.Equal(baseline.OccurrenceGroups.Count, reversed.OccurrenceGroups.Count);
        Assert.All(baseline.OccurrenceGroups, group => Assert.NotEmpty(group.CandidateIds));
        Assert.All(reversed.OccurrenceGroups, group => Assert.NotEmpty(group.CandidateIds));
        Assert.All(baseline.OccurrenceGroups, group =>
        {
            var reversedGroup = reversed.OccurrenceGroups.Single(item => item.PrimaryOccurrence == group.PrimaryOccurrence);
            Assert.Equal(group.CandidateIds.Reverse(), reversedGroup.CandidateIds);
            Assert.Equal(group.CandidateIds.ToHashSet(StringComparer.Ordinal), reversedGroup.CandidateIds.ToHashSet(StringComparer.Ordinal));
        });

        var baselineWholePositions = baseline.OccurrenceGroups.Select(group =>
        {
            var whole = group.CandidateIds.Select((id, index) => (id, index))
                .Single(item => prepared.SourcePack.Universe.Candidates.Single(candidate => candidate.Id == item.id).Kind.ToString() == "WHOLE");
            return new { primary = group.PrimaryOccurrence, candidateCount = group.CandidateIds.Count, position = whole.index + 1, candidate = whole.id };
        }).ToArray();
        var reversedWholePositions = reversed.OccurrenceGroups.Select(group =>
        {
            var whole = group.CandidateIds.Select((id, index) => (id, index))
                .Single(item => prepared.SourcePack.Universe.Candidates.Single(candidate => candidate.Id == item.id).Kind.ToString() == "WHOLE");
            return new { primary = group.PrimaryOccurrence, candidateCount = group.CandidateIds.Count, position = whole.index + 1, candidate = whole.id };
        }).ToArray();
        Assert.All(baselineWholePositions, row => Assert.Equal(1, row.position));
        Assert.All(reversedWholePositions, row => Assert.Equal(row.candidateCount, row.position));

        FreezeArtifact.AssertJson(ExactExtentOrderPreflightRoot, "candidate-order-preflight.v1.json", new
        {
            schemaVersion = "v5-p6tg2c-candidate-order-preflight-v1",
            status = "PREPARED_NOT_AUTHORIZED",
            treatment = new
            {
                onlyIndependentVariable = "CANDIDATE_ORDER",
                baselineProtocol = "v5-function-conditioned-exact-extent-resolver-2",
                candidateOrderArm = "REVERSE_FROZEN_MENU_ORDER",
                model = "qwen/qwen3.7-flash",
                provider = "alibaba",
                reasoning = new { enabled = true, effort = "OMITTED" },
                samePrimaries = true,
                sameCandidateIds = true,
                sameCandidateContentAndKinds = true,
                samePrompt = true,
                goldUsedForRequestConstruction = false,
            },
            sourceAuthority = new
            {
                g2bPreflightSha256 = Hashing.Sha256(File.ReadAllText(TestRepository.Path($"{ExactExtentPreflightRoot}/exact-extent-preflight.v1.json"))),
                g2aRawResponseSha256 = g2aRaw.RootElement.GetProperty("rawResponseSha256").GetString(),
                hasPrimaries = baseline.OccurrenceGroups.Select(group => new { primary = group.PrimaryOccurrence, alias = group.PrimaryAlias }).ToArray(),
            },
            baseline = new
            {
                providerBodySha256 = baseline.ProviderHash,
                userMessageSha256 = baseline.MessageHash,
                providerBodyBytes = baseline.ProviderBytes,
                primaryCount = baseline.OccurrenceGroups.Count,
                wholeCandidatePositions = baselineWholePositions,
                optionsPerPrimary = baseline.OccurrenceGroups.Select(group => new { primary = group.PrimaryOccurrence, candidateIds = group.CandidateIds }).ToArray(),
            },
            candidateOrderArm = new
            {
                providerBodySha256 = reversed.ProviderHash,
                userMessageSha256 = reversed.MessageHash,
                userMessageUtf8Bytes = reversed.MessageBytes,
                providerBodyBytes = reversed.ProviderBytes,
                primaryCount = reversed.OccurrenceGroups.Count,
                wholeCandidatePositions = reversedWholePositions,
                optionsPerPrimary = reversed.OccurrenceGroups.Select(group => new { primary = group.PrimaryOccurrence, candidateIds = group.CandidateIds }).ToArray(),
            },
            execution = new { providerCalls = 0, retries = 0, repairs = 0, fallbacks = 0, goldRead = false, runtimeChanged = false },
            conclusion = "P6TG2C_PROVIDER_FREE_ORDER_ARM_FROZEN; BASELINE_WHOLE_IS_FIRST_AND_ORDER_ARM_MOVES_WHOLE_TO_LAST; ONE_MATCHED_PROVIDER_CALL_REQUIRES_SEPARATE_AUTHORIZATION",
        });
    }

    private static PreparedDocument Prepare(string documentId, string sourcePdf, JsonElement captureRow, JsonElement g1Document)
    {
        var sourceHash = CanonicalSemanticSourceHash.Compute(TestRepository.Path(sourcePdf));
        var plan = PdfCandidateAuthorityQualificationAdapter.PrepareFromSnapshot(
            TestRepository.Path($"{SnapshotRoot}/{sourceHash}.json"), documentId);
        var sourcePack = plan.Packs.Single(pack => pack.PackId.EndsWith("PACK_001", StringComparison.Ordinal));
        var f1Pack = PdfTotalOccurrenceRoleQualificationAdapter.PrepareFunctionMembershipF1(plan, sourcePack, Correspondences(sourcePack));
        Assert.Equal(f1Pack.Request.UserMessageSha256, captureRow.GetProperty("semanticRequestHash").GetString());
        Assert.Equal(f1Pack.ProviderRequestHash, captureRow.GetProperty("providerRequestHash").GetString());
        Assert.Equal("stop", captureRow.GetProperty("finishReason").GetString());
        Assert.True(captureRow.GetProperty("analysis").GetProperty("parserAccepted").GetBoolean());
        var raw = captureRow.GetProperty("rawResponse").GetString()!;
        Assert.Equal(captureRow.GetProperty("rawResponseSha256").GetString(), Hashing.Sha256(raw));
        var parsed = PdfTotalOccurrenceRoleQualificationAdapter.ParseFunctionMembershipF1(f1Pack, raw);
        Assert.Equal(96, parsed.Decisions.Count);
        var atomById = f1Pack.Request.Occurrences.ToDictionary(item => item.Id, item => item.Atom, StringComparer.Ordinal);
        var functionByAlias = parsed.Decisions.ToDictionary(item => atomById[item.OccurrenceId].Alias, item => item.Function.ToString(), StringComparer.Ordinal);
        var occurrenceByAlias = f1Pack.Request.Occurrences.ToDictionary(item => item.Atom.Alias, item => item.Id, StringComparer.Ordinal);
        var classes = Classify(sourcePack, functionByAlias);
        var persistedCounts = g1Document.GetProperty("classes");
        foreach (var value in Enum.GetValues<ProjectionClass>())
            Assert.Equal(classes.Values.Count(item => item == value), persistedCounts.GetProperty(value.ToString()).GetInt32());
        Assert.Equal(sourcePack.Universe.Fingerprint, g1Document.GetProperty("candidateUniverseFingerprint").GetString());
        Assert.Equal(sourcePack.Universe.Candidates.Count, g1Document.GetProperty("candidateCount").GetInt32());
        return new PreparedDocument(documentId, plan, sourcePack, f1Pack, functionByAlias, occurrenceByAlias, classes);
    }

    private sealed record Group(string PrimaryOccurrence, string PrimaryAlias, IReadOnlyList<string> CandidateIds);
    private sealed record PreparedRequest(string DocumentId, string SystemPrompt, string UserMessage, string MessageHash,
        int MessageBytes, byte[] ProviderBody, int ProviderBytes, string ProviderHash, IReadOnlyList<Group> OccurrenceGroups,
        int MaxCompletionTokens);

    private sealed record ParsedLedger(int RawDecisions, int AcceptedSelections, int NoStructuralExtent,
        int Quarantined, int MissingPrimaries, IReadOnlyList<object> Decisions, IReadOnlyList<object> Refusals);

    private sealed record ForensicArm(string Name, string ProviderBodySha256, string RawResponseSha256, IReadOnlyList<ForensicRow> Rows);

    private sealed record ForensicRow(
        string Primary,
        IReadOnlyList<string> CandidateIds,
        IReadOnlyList<string> CandidateKinds,
        IReadOnlyList<string> CandidateIdentities,
        string SelectedCandidate,
        string SelectedKind,
        string SelectedIdentity,
        int SelectedPosition,
        int WholePosition,
        bool SelectedFirstCandidate,
        string SelectedRelativeToWhole);

    private sealed record IndependentJudgmentRequest(
        string DocumentId,
        string Primary,
        string PrimaryAlias,
        string CandidateId,
        string CandidateKind,
        string CandidateIdentity,
        string SystemPrompt,
        string UserMessage,
        string MessageHash,
        int MessageBytes,
        byte[] ProviderBody,
        int ProviderBytes,
        string ProviderHash,
        int MaxCompletionTokens);

    private sealed record IndependentJudgmentLedger(string Primary, string Candidate, string Judgment);

    private sealed record IndependentAggregate(string Primary, string Outcome, string? SelectedCandidate,
        IReadOnlyList<string> ExactCandidates);

    private sealed record ContinuationEdge(string Anchor, string Left, string Right, int Ordinal);

    private sealed record ContinuationBoundaryRequest(
        string DocumentId,
        string SystemPrompt,
        string UserMessage,
        string MessageHash,
        int MessageBytes,
        byte[] ProviderBody,
        int ProviderBytes,
        string ProviderHash,
        IReadOnlyList<ContinuationEdge> Edges,
        int MaxCompletionTokens);

    private sealed record ContinuationBoundaryLedger(
        string Anchor,
        string Left,
        string Right,
        string Boundary);

    private sealed record ReconstructedContinuation(string Anchor, string Outcome, IReadOnlyList<string> Aliases);

    private sealed record AnchorExistenceRequest(string DocumentId, string SystemPrompt, string UserMessage,
        string MessageHash, int MessageBytes, byte[] ProviderBody, int ProviderBytes, string ProviderHash,
        IReadOnlyList<(string Occurrence, string Alias)> Primaries, int MaxCompletionTokens);

    private sealed record ParsedAnchorLedger(int RawDecisions, int HasStructuralExtent, int NoStructuralExtent,
        int Quarantined, int MissingPrimaries, IReadOnlyList<object> Decisions, IReadOnlyList<object> Refusals);

    [Fact]
    public void P6TH2_preflight_freezes_function_conditioned_continuation_boundaries_without_candidate_menus()
    {
        using var f1Retry = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{F1Root}/retry-src089-result.v1.json")));
        using var g1 = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(G1Path)));
        using var g2aRaw = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{AnchorExistenceCaptureRoot}/SRC-089.raw-capture.v1.json")));
        var prepared = Prepare("SRC-089", SourcePdfCorpus.Src089, f1Retry.RootElement.GetProperty("row"), g1.RootElement.GetProperty("src089"));
        var anchorRequest = ComposeAnchorExistence(prepared);
        var anchorLedger = ParseAnchorLedger(anchorRequest, g2aRaw.RootElement.GetProperty("rawResponse").GetString()!);
        var hasPrimaries = anchorLedger.Decisions.Select(item => JsonDocument.Parse(JsonSerializer.Serialize(item)))
            .Where(item => item.RootElement.GetProperty("anchor").GetString() == "HAS_STRUCTURAL_EXTENT")
            .Select(item => item.RootElement.GetProperty("primary").GetString()!).OrderBy(item => item, StringComparer.Ordinal).ToArray();
        try
        {
            Assert.Equal(6, hasPrimaries.Length);
            var request = ComposeContinuationBoundaries(prepared, hasPrimaries);

            Assert.Equal(18, request.Edges.Count); // 6 anchored chains × (max representable parts 3: two continuations plus terminal stop).
            Assert.All(request.Edges.GroupBy(edge => edge.Anchor), group => Assert.Equal(3, group.Count()));
            Assert.DoesNotContain("candidate", request.UserMessage, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sourceParts", request.UserMessage, StringComparison.Ordinal);
            Assert.DoesNotContain("span", request.UserMessage, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("heading", request.SystemPrompt, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("representation", request.SystemPrompt, StringComparison.OrdinalIgnoreCase);

            var synthetic = request.Edges.Select(edge => new ContinuationBoundaryLedger(edge.Anchor, edge.Left, edge.Right,
                edge.Ordinal < 2 ? "CONTINUES_STRUCTURAL_UNIT" : "STOPS_STRUCTURAL_UNIT")).ToArray();
            var reconstructed = ReconstructContinuationBoundaries(request, synthetic);
            Assert.All(reconstructed, row => Assert.Equal("RECONSTRUCTED", row.Outcome));
            Assert.All(reconstructed, row => Assert.Equal(3, row.Aliases.Count));

            var invalid = synthetic.Select(item => item with { }).ToArray();
            invalid[1] = invalid[1] with { Boundary = "STOPS_STRUCTURAL_UNIT" };
            invalid[2] = invalid[2] with { Boundary = "CONTINUES_STRUCTURAL_UNIT" };
            Assert.Throws<InvalidOperationException>(() => ParseContinuationBoundaries(request,
                JsonSerializer.Serialize(new { decisions = invalid.Select(item => new { anchor = item.Anchor, left = item.Left, right = item.Right, boundary = item.Boundary }) })));

            FreezeArtifact.AssertJson(ContinuationBoundaryPreflightRoot, "continuation-boundary-preflight.v1.json", new
            {
                schemaVersion = "v5-p6th2-function-conditioned-continuation-preflight-v1",
                status = "PREPARED_NOT_AUTHORIZED",
                protocolVersion = ContinuationBoundaryProtocol,
                purpose = "DERIVE_EXACT_REPRESENTABLE_EXTENT_FROM_SOURCE_ORDERED_CONTINUATION_BOUNDARIES_AFTER_F1_FUNCTION_AND_G2A_ANCHOR",
                treatment = new
                {
                    documentId = request.DocumentId,
                    packId = "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_001",
                    model = "qwen/qwen3.7-flash",
                    provider = "alibaba",
                    reasoning = new { enabled = true, effort = "OMITTED" },
                    upstreamFunctionAuthority = "FROZEN_F1_ESTABLISHES_STRUCTURE",
                    upstreamAnchorAuthority = "FROZEN_G2A_RAW_HAS_STRUCTURAL_EXTENT",
                    candidateMenus = "ABSENT",
                    candidateIds = "ABSENT",
                    exactExtentSelection = "ABSENT",
                },
                outputContract = new
                {
                    shape = "{\"decisions\":[{\"anchor\":\"O9\",\"left\":\"O9\",\"right\":\"O10\",\"boundary\":\"CONTINUES_STRUCTURAL_UNIT\"}]}",
                    oneDecisionPerIssuedBoundaryEdge = true,
                    allowedBoundaries = new[] { "CONTINUES_STRUCTURAL_UNIT", "STOPS_STRUCTURAL_UNIT" },
                    sourceOrderAdjacentEdgesOnly = true,
                    firstStopTerminatesTheReconstructedExtent = true,
                    continuationAfterStopIsInvalid = true,
                    modelAuthoredCandidateOrLocator = false,
                    invalidDecisionPolicy = "whole-request rejection; never infer a continuation boundary",
                },
                boundedRepresentableDomain = new
                {
                    maxMultipartParts = 3,
                    edgesPerAnchor = 3,
                    interpretation = "two possible continuation edges plus one terminal boundary; a later production execution may not infer beyond this frozen V5 candidate representation bound",
                },
                sourceAuthority = new
                {
                    f1RequestSha256 = prepared.F1Pack.Request.UserMessageSha256,
                    g2aRawResponseSha256 = g2aRaw.RootElement.GetProperty("rawResponseSha256").GetString(),
                    hasPrimaryCount = hasPrimaries.Length,
                    hasPrimaries,
                    candidateUniverseFingerprint = prepared.SourcePack.Universe.Fingerprint,
                    goldReadForRequestConstruction = false,
                    goldMutation = "NONE",
                    sharedRuntime = "UNCHANGED",
                },
                callPlan = new
                {
                    providerCallsAuthorized = 0,
                    providerCalls = 0,
                    retries = 0,
                    repairs = 0,
                    fallbacks = 0,
                    anchorCount = hasPrimaries.Length,
                    edgeCount = request.Edges.Count,
                    edges = request.Edges.Select(edge => new { anchor = edge.Anchor, left = edge.Left, right = edge.Right, ordinal = edge.Ordinal }).ToArray(),
                    systemPromptSha256 = Hashing.Sha256(request.SystemPrompt),
                    userMessageSha256 = request.MessageHash,
                    userMessageUtf8Bytes = request.MessageBytes,
                    providerBodySha256 = request.ProviderHash,
                    providerBodyBytes = request.ProviderBytes,
                },
                conclusion = "P6TH2_PROVIDER_FREE_PREFLIGHT_FROZEN; H1_SHOWED_EXACTNESS_IS_NOT_A_UNARY_CANDIDATE_PROPERTY; PROVIDER_EXECUTION_REQUIRES_SEPARATE_EXPLICIT_AUTHORIZATION",
            });
        }
        finally
        {
            foreach (var item in anchorLedger.Decisions.Select(item => JsonDocument.Parse(JsonSerializer.Serialize(item)))) item.Dispose();
        }
    }

    [Fact]
    public async Task Run_exactly_one_frozen_H2_continuation_boundary_call_only_when_explicitly_enabled()
    {
        if (Environment.GetEnvironmentVariable(RunContinuationBoundaryVariable) is not ("1" or "true" or "TRUE")) return;

        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        Assert.False(string.IsNullOrWhiteSpace(apiKey), "OPENROUTER_API_KEY is required for the explicitly enabled P6T-H2 canary.");
        var capturePath = TestRepository.Path(ContinuationBoundaryCaptureRoot);
        Assert.False(Directory.Exists(capturePath), "P6T-H2 capture root already exists; automatic resume or resend is forbidden.");

        using var f1Retry = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{F1Root}/retry-src089-result.v1.json")));
        using var g1 = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(G1Path)));
        using var g2aRaw = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{AnchorExistenceCaptureRoot}/SRC-089.raw-capture.v1.json")));
        using var preflight = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{ContinuationBoundaryPreflightRoot}/continuation-boundary-preflight.v1.json")));
        var prepared = Prepare("SRC-089", SourcePdfCorpus.Src089, f1Retry.RootElement.GetProperty("row"), g1.RootElement.GetProperty("src089"));
        var anchorLedger = ParseAnchorLedger(ComposeAnchorExistence(prepared), g2aRaw.RootElement.GetProperty("rawResponse").GetString()!);
        var has = anchorLedger.Decisions.Select(item => JsonDocument.Parse(JsonSerializer.Serialize(item)))
            .Where(item => item.RootElement.GetProperty("anchor").GetString() == "HAS_STRUCTURAL_EXTENT")
            .Select(item => item.RootElement.GetProperty("primary").GetString()!).ToArray();
        ContinuationBoundaryRequest request;
        try { request = ComposeContinuationBoundaries(prepared, has); }
        finally { foreach (var item in anchorLedger.Decisions.Select(item => JsonDocument.Parse(JsonSerializer.Serialize(item)))) item.Dispose(); }

        var callPlan = preflight.RootElement.GetProperty("callPlan");
        Assert.Equal("PREPARED_NOT_AUTHORIZED", preflight.RootElement.GetProperty("status").GetString());
        Assert.Equal(request.MessageHash, callPlan.GetProperty("userMessageSha256").GetString());
        Assert.Equal(request.ProviderHash, callPlan.GetProperty("providerBodySha256").GetString());
        Assert.Equal(request.ProviderBytes, callPlan.GetProperty("providerBodyBytes").GetInt32());

        Directory.CreateDirectory(capturePath);
        WriteNew(Path.Combine(capturePath, "execution-reservation.v1.json"), new
        {
            schemaVersion = "v5-p6th2-execution-reservation-v1",
            status = "ONE_PRIMARY_SLOT_RESERVED",
            documentId = request.DocumentId,
            providerRequestHash = request.ProviderHash,
            providerCallsBeforeSend = 0,
            retriesAllowed = 0,
            repairsAllowed = false,
            fallbacksAllowed = false,
            goldRead = false,
        });

        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var provider = new OpenRouterQualificationTransport(http, new RemoteInferenceOptions
        {
            ApiKey = apiKey!, Model = "qwen/qwen3.7-flash", OpenRouterProviderRoute = "Alibaba",
            TransientRequestRetries = 0, MaxParallelRequests = 1, ProviderTransportTimeoutSeconds = 300,
            RequireZeroDataRetention = false,
        });
        try
        {
            var observation = await provider.ExecuteObservedAsync(request.ProviderBody, request.MaxCompletionTokens,
                request.SystemPrompt, request.UserMessage, CancellationToken.None);
            Assert.Equal(0, observation.RetryCount);
            WriteNew(Path.Combine(capturePath, "SRC-089.raw-capture.v1.json"), new
            {
                schemaVersion = "v5-p6th2-raw-provider-capture-v1", documentId = request.DocumentId,
                provider = "OpenRouter", model = "qwen/qwen3.7-flash", providerRoute = "Alibaba",
                reasoningRequested = true, reasoningEffort = "OMITTED", providerRequestHash = request.ProviderHash,
                semanticRequestHash = request.MessageHash, finishReason = observation.FinishReason, usage = observation.Usage,
                promptTokens = UsageInt(observation.Usage, "prompt_tokens"), completionTokens = UsageInt(observation.Usage, "completion_tokens"),
                reasoningTokens = UsageInt(observation.Usage, "reasoning_tokens"), reasoningExecutionConfirmed = UsageInt(observation.Usage, "reasoning_tokens") is > 0,
                sseEventCount = observation.SseEventCount, retryCount = observation.RetryCount,
                rawSseSha256 = Hashing.Sha256(observation.RawSse), rawResponseSha256 = Hashing.Sha256(observation.Content),
                rawSse = observation.RawSse, rawResponse = observation.Content,
            });
            IReadOnlyList<ContinuationBoundaryLedger>? parsed = null;
            string? parserError = null;
            try { parsed = ParseContinuationBoundaries(request, observation.Content); }
            catch (Exception error) { parserError = error.GetType().Name + ": " + error.Message; }
            WriteNew(Path.Combine(capturePath, "result.v1.json"), new
            {
                schemaVersion = "v5-p6th2-one-primary-call-result-v1", status = "EXECUTION_SET_FROZEN",
                providerCalls = 1, retries = 0, repairs = 0, fallbacks = 0, goldRead = false, goldMutation = "NONE", runtimeChanged = false,
                rawCaptureSha256 = Hashing.Sha256(File.ReadAllText(Path.Combine(capturePath, "SRC-089.raw-capture.v1.json"))),
                row = new { documentId = request.DocumentId, transportStatus = "COMPLETED", finishReason = observation.FinishReason,
                    parserStatus = parsed is null ? "REJECTED" : "PARSED", parserError, edgeCount = request.Edges.Count,
                    acceptedEdges = parsed?.Count ?? 0, promptTokens = UsageInt(observation.Usage, "prompt_tokens"),
                    completionTokens = UsageInt(observation.Usage, "completion_tokens"), reasoningTokens = UsageInt(observation.Usage, "reasoning_tokens"),
                    rawResponseSha256 = Hashing.Sha256(observation.Content) },
            });
        }
        catch (Exception error)
        {
            WriteNew(Path.Combine(capturePath, "result.v1.json"), new
            {
                schemaVersion = "v5-p6th2-one-primary-call-result-v1", status = "EXECUTION_SET_FROZEN",
                providerCalls = 1, retries = 0, repairs = 0, fallbacks = 0, goldRead = false, goldMutation = "NONE", runtimeChanged = false,
                row = new { documentId = request.DocumentId, transportStatus = "FAILED", error = error.GetType().Name + ": " + error.Message },
            });
            throw;
        }
    }

    [Fact]
    public void P6TH2_frozen_continuation_boundaries_are_audited_against_gold_without_provider_calls()
    {
        using var f1Retry = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{F1Root}/retry-src089-result.v1.json")));
        using var g1 = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(G1Path)));
        using var g2aRaw = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{AnchorExistenceCaptureRoot}/SRC-089.raw-capture.v1.json")));
        using var execution = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{ContinuationBoundaryCaptureRoot}/result.v1.json")));
        using var raw = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{ContinuationBoundaryCaptureRoot}/SRC-089.raw-capture.v1.json")));
        using var gold = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(Gold089Path)));
        var prepared = Prepare("SRC-089", SourcePdfCorpus.Src089, f1Retry.RootElement.GetProperty("row"), g1.RootElement.GetProperty("src089"));
        var anchorLedger = ParseAnchorLedger(ComposeAnchorExistence(prepared), g2aRaw.RootElement.GetProperty("rawResponse").GetString()!);
        var has = anchorLedger.Decisions.Select(item => JsonDocument.Parse(JsonSerializer.Serialize(item)))
            .Where(item => item.RootElement.GetProperty("anchor").GetString() == "HAS_STRUCTURAL_EXTENT")
            .Select(item => item.RootElement.GetProperty("primary").GetString()!).ToArray();
        try
        {
            var request = ComposeContinuationBoundaries(prepared, has);
            Assert.Equal(request.ProviderHash, raw.RootElement.GetProperty("providerRequestHash").GetString());
            Assert.Equal(raw.RootElement.GetProperty("rawResponseSha256").GetString(), Hashing.Sha256(raw.RootElement.GetProperty("rawResponse").GetString()!));
            Assert.Equal("stop", execution.RootElement.GetProperty("row").GetProperty("finishReason").GetString());
            Assert.Equal("PARSED", execution.RootElement.GetProperty("row").GetProperty("parserStatus").GetString());
            var observed = ParseContinuationBoundaries(request, raw.RootElement.GetProperty("rawResponse").GetString()!);
            var reconstructed = ReconstructContinuationBoundaries(request, observed);
            var aliasByOccurrence = prepared.OccurrenceByAlias.ToDictionary(item => item.Value, item => item.Key, StringComparer.Ordinal);
            var goldByAnchor = gold.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray()
                .Select(claim => claim.GetProperty("sourceParts").EnumerateArray().Select(part => part.GetProperty("sourceAlias").GetString()!).ToArray())
                .Where(parts => parts.Length > 0 && prepared.OccurrenceByAlias.ContainsKey(parts[0]))
                .ToDictionary(parts => prepared.OccurrenceByAlias[parts[0]], parts => parts, StringComparer.Ordinal);
            Assert.Equal(6, has.Length);
            Assert.All(has, anchor => Assert.True(goldByAnchor.ContainsKey(anchor), $"h2-gold-anchor-missing:{anchor}"));

            var edgeRows = request.Edges.Select(edge =>
            {
                var observedBoundary = observed.Single(item => item.Anchor == edge.Anchor && item.Left == edge.Left && item.Right == edge.Right).Boundary;
                var expectedAliases = goldByAnchor[edge.Anchor];
                var expected = expectedAliases.Contains(aliasByOccurrence[edge.Right], StringComparer.Ordinal) &&
                    Array.IndexOf(expectedAliases, aliasByOccurrence[edge.Right]) == Array.IndexOf(expectedAliases, aliasByOccurrence[edge.Left]) + 1
                    ? "CONTINUES_STRUCTURAL_UNIT" : "STOPS_STRUCTURAL_UNIT";
                return new { anchor = edge.Anchor, left = edge.Left, right = edge.Right, expected, observed = observedBoundary,
                    correct = expected == observedBoundary };
            }).ToArray();
            var extentRows = reconstructed.Select(row =>
            {
                var expected = goldByAnchor[row.Anchor];
                var actual = row.Aliases.Select(occurrence => aliasByOccurrence[occurrence]).ToArray();
                var classification = actual.SequenceEqual(expected, StringComparer.Ordinal) ? "EXACT" :
                    actual.All(alias => expected.Contains(alias, StringComparer.Ordinal)) ? "UNDEREXTENT" :
                    expected.All(alias => actual.Contains(alias, StringComparer.Ordinal)) ? "OVEREXTENT" : "WRONG_PARTS";
                var firstWrong = edgeRows.Where(edge => edge.anchor == row.Anchor && !edge.correct)
                    .Select(edge => new { edge.left, edge.right, edge.expected, edge.observed }).FirstOrDefault();
                return new { anchor = row.Anchor, expectedAliases = expected, observedAliases = actual, classification, firstWrongEdge = firstWrong };
            }).OrderBy(row => row.anchor, StringComparer.Ordinal).ToArray();

            Assert.Equal(17, edgeRows.Count(row => row.correct));
            Assert.Equal(1, edgeRows.Count(row => !row.correct));
            Assert.Equal(5, extentRows.Count(row => row.classification == "EXACT"));
            Assert.Equal(1, extentRows.Count(row => row.classification == "OVEREXTENT"));
            Assert.Equal(0, extentRows.Count(row => row.classification is "UNDEREXTENT" or "WRONG_PARTS"));

            FreezeArtifact.AssertJson(ContinuationBoundaryGoldAuditRoot, "continuation-boundary-gold-audit.v1.json", new
            {
                schemaVersion = "v5-p6th2-continuation-boundary-gold-audit-v1",
                status = "FROZEN_OFFLINE_AUDIT",
                executionAuthority = new
                {
                    executionResultSha256 = Hashing.Sha256(File.ReadAllText(TestRepository.Path($"{ContinuationBoundaryCaptureRoot}/result.v1.json"))),
                    rawResponseSha256 = raw.RootElement.GetProperty("rawResponseSha256").GetString(),
                    providerCallsDuringAudit = 0,
                    repair = false,
                    fallback = false,
                },
                evaluationBasis = new
                {
                    goldPath = Gold089Path,
                    canonicalGoldSha256 = Hashing.Sha256(File.ReadAllText(TestRepository.Path(Gold089Path))),
                    goldMutation = "NONE",
                    anchorCount = has.Length,
                },
                edgeScore = new
                {
                    total = edgeRows.Length,
                    correct = edgeRows.Count(row => row.correct),
                    incorrect = edgeRows.Count(row => !row.correct),
                    continuation = new { truePositive = edgeRows.Count(row => row.expected == "CONTINUES_STRUCTURAL_UNIT" && row.observed == row.expected), falsePositive = edgeRows.Count(row => row.observed == "CONTINUES_STRUCTURAL_UNIT" && row.expected != row.observed), falseNegative = edgeRows.Count(row => row.expected == "CONTINUES_STRUCTURAL_UNIT" && row.observed != row.expected) },
                    stop = new { correct = edgeRows.Count(row => row.expected == "STOPS_STRUCTURAL_UNIT" && row.correct), incorrect = edgeRows.Count(row => row.expected == "STOPS_STRUCTURAL_UNIT" && !row.correct) },
                    rows = edgeRows,
                },
                reconstructedExtentScore = new
                {
                    total = extentRows.Length,
                    exact = extentRows.Count(row => row.classification == "EXACT"),
                    underextent = extentRows.Count(row => row.classification == "UNDEREXTENT"),
                    overextent = extentRows.Count(row => row.classification == "OVEREXTENT"),
                    wrongParts = extentRows.Count(row => row.classification == "WRONG_PARTS"),
                    rows = extentRows,
                },
                conclusion = "H2_FUNCTION_CONDITIONED_CONTINUATION_RECONSTRUCTS_5_OF_6_FROZEN_SRC089_ANCHOR_EXTENTS_EXACTLY; ONLY_ERROR_IS_AN_OVEREXTENT_BOUNDARY_BETWEEN_ADJACENT_STRUCTURAL_UNITS; NO_G2A_POST_HOC_REMEDIATION_APPLIED",
            });
        }
        finally
        {
            foreach (var item in anchorLedger.Decisions.Select(item => JsonDocument.Parse(JsonSerializer.Serialize(item)))) item.Dispose();
        }
    }

    [Fact]
    public void P6TH21_composes_frozen_G2A_anchors_with_H2_boundaries_without_mutating_raw_H2()
    {
        using var f1Retry = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{F1Root}/retry-src089-result.v1.json")));
        using var g1 = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(G1Path)));
        using var g2aRaw = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{AnchorExistenceCaptureRoot}/SRC-089.raw-capture.v1.json")));
        using var h2Raw = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{ContinuationBoundaryCaptureRoot}/SRC-089.raw-capture.v1.json")));
        using var gold = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(Gold089Path)));
        var prepared = Prepare("SRC-089", SourcePdfCorpus.Src089, f1Retry.RootElement.GetProperty("row"), g1.RootElement.GetProperty("src089"));
        var g2a = ParseAnchorLedger(ComposeAnchorExistence(prepared), g2aRaw.RootElement.GetProperty("rawResponse").GetString()!);
        var anchors = g2a.Decisions.Select(item => JsonDocument.Parse(JsonSerializer.Serialize(item)))
            .Where(item => item.RootElement.GetProperty("anchor").GetString() == "HAS_STRUCTURAL_EXTENT")
            .Select(item => item.RootElement.GetProperty("primary").GetString()!).ToHashSet(StringComparer.Ordinal);
        try
        {
            var request = ComposeContinuationBoundaries(prepared, anchors);
            var raw = ParseContinuationBoundaries(request, h2Raw.RootElement.GetProperty("rawResponse").GetString()!);
            var crossing = raw.Where(edge => edge.Boundary == "CONTINUES_STRUCTURAL_UNIT" && edge.Right != edge.Anchor && anchors.Contains(edge.Right))
                .Select(edge => new { edge.Anchor, edge.Left, edge.Right, rawBoundary = edge.Boundary, composedBoundary = "STOPS_STRUCTURAL_UNIT" })
                .ToArray();
            Assert.Single(crossing);
            Assert.Equal("O17", crossing[0].Anchor);
            Assert.Equal("O18", crossing[0].Left);
            Assert.Equal("O19", crossing[0].Right);

            var composed = raw.Select(edge => crossing.Any(conflict => conflict.Anchor == edge.Anchor && conflict.Left == edge.Left && conflict.Right == edge.Right)
                ? edge with { Boundary = "STOPS_STRUCTURAL_UNIT" } : edge).ToArray();
            var composedExtents = ReconstructContinuationBoundaries(request, composed);
            var aliasByOccurrence = prepared.OccurrenceByAlias.ToDictionary(item => item.Value, item => item.Key, StringComparer.Ordinal);
            var goldByAnchor = gold.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray()
                .Select(claim => claim.GetProperty("sourceParts").EnumerateArray().Select(part => part.GetProperty("sourceAlias").GetString()!).ToArray())
                .Where(parts => parts.Length > 0 && prepared.OccurrenceByAlias.ContainsKey(parts[0]))
                .ToDictionary(parts => prepared.OccurrenceByAlias[parts[0]], parts => parts, StringComparer.Ordinal);
            var extentRows = composedExtents.Select(row => new
            {
                anchor = row.Anchor,
                expectedAliases = goldByAnchor[row.Anchor],
                observedAliases = row.Aliases.Select(id => aliasByOccurrence[id]).ToArray(),
                exact = row.Aliases.Select(id => aliasByOccurrence[id]).SequenceEqual(goldByAnchor[row.Anchor], StringComparer.Ordinal),
            }).OrderBy(row => row.anchor, StringComparer.Ordinal).ToArray();
            Assert.Equal(6, extentRows.Length);
            Assert.All(extentRows, row => Assert.True(row.exact, $"h21-composed-extent-not-exact:{row.anchor}"));
            Assert.Equal(raw.Count, composed.Length);
            Assert.Equal(1, raw.Zip(composed).Count(pair => pair.First.Boundary != pair.Second.Boundary));

            FreezeArtifact.AssertJson(ContinuationCompositionAuditRoot, "anchor-continuation-composition-audit.v1.json", new
            {
                schemaVersion = "v5-p6th21-anchor-continuation-composition-audit-v1",
                status = "FROZEN_PROVIDER_FREE_COMPOSITION_AUDIT",
                rawAuthorities = new
                {
                    g2aRawResponseSha256 = g2aRaw.RootElement.GetProperty("rawResponseSha256").GetString(),
                    h2RawResponseSha256 = h2Raw.RootElement.GetProperty("rawResponseSha256").GetString(),
                    rawH2Mutated = false,
                    providerCallsDuringAudit = 0,
                    repairs = 0,
                    fallbacks = 0,
                },
                invariant = new
                {
                    name = "NO_CROSS_DISTINCT_FROZEN_G2A_ANCHOR",
                    rule = "A raw H2 CONTINUES_STRUCTURAL_UNIT edge must compose to STOPS_STRUCTURAL_UNIT when its right O# is a distinct frozen G2A HAS_STRUCTURAL_EXTENT anchor.",
                    anchorCount = anchors.Count,
                    rawContinuations = raw.Count(edge => edge.Boundary == "CONTINUES_STRUCTURAL_UNIT"),
                    crossingConflicts = crossing,
                    rawDecisionOverrides = 1,
                },
                evaluationBasis = new
                {
                    canonicalGoldSha256 = Hashing.Sha256(File.ReadAllText(TestRepository.Path(Gold089Path))),
                    goldMutation = "NONE",
                    goldUsedOnlyForOfflineCompositionEvaluation = true,
                },
                composedResult = new
                {
                    exactExtents = extentRows.Count(row => row.exact),
                    totalExtents = extentRows.Length,
                    rows = extentRows,
                },
                conclusion = "COMPOSITION_RESULT_ONLY: FROZEN_G2A_ANCHOR_AUTHORITY_PLUS_FROZEN_H2_CONTINUATION_WITH_NO_CROSS_ANCHOR_INVARIANT_RECONSTRUCTS_6_OF_6_SRC089_PROBE_EXTENTS; RAW_H2_ACCURACY_REMAINS_17_OF_18_EDGES_AND_5_OF_6_EXTENTS",
            });
        }
        finally
        {
            foreach (var item in g2a.Decisions.Select(item => JsonDocument.Parse(JsonSerializer.Serialize(item)))) item.Dispose();
        }
    }

    [Fact]
    public void P6TH3_provider_free_cross_document_shape_audit_scopes_composition_evidence_without_claiming_unavailable_anchor_data()
    {
        var documents = new[]
        {
            (Id: "SRC-089", Pdf: SourcePdfCorpus.Src089, Gold: Gold089Path),
            (Id: "SRC-095", Pdf: SourcePdfCorpus.Src095, Gold: Gold095Path),
        };
        var rows = new List<object>();
        var totals = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["A_MULTIPART_TO_BODY"] = 0, ["B_MULTIPART_TO_ADJACENT_HEADING"] = 0,
            ["C_SINGLETON_TO_ADJACENT_HEADING"] = 0, ["D_MULTIPART_TO_MULTIPART_HEADING"] = 0,
            ["F_WRAPPED_OR_HYPHENATED_PROXY"] = 0,
        };
        foreach (var document in documents)
        {
            var sourceHash = CanonicalSemanticSourceHash.Compute(TestRepository.Path(document.Pdf));
            var plan = PdfCandidateAuthorityQualificationAdapter.PrepareFromSnapshot(TestRepository.Path($"{SnapshotRoot}/{sourceHash}.json"), document.Id);
            var atoms = plan.SourceAtoms.ToDictionary(atom => atom.Alias, StringComparer.Ordinal);
            using var gold = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(document.Gold)));
            var claims = gold.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray().Select(claim =>
            {
                var aliases = claim.GetProperty("sourceParts").EnumerateArray().Select(part => part.GetProperty("sourceAlias").GetString()!).ToArray();
                return new { aliases, first = atoms[aliases[0]].Ordinal, last = atoms[aliases[^1]].Ordinal };
            }).OrderBy(claim => claim.first).ToArray();
            for (var index = 0; index < claims.Length; index++)
            {
                var claim = claims[index];
                var multipart = claim.aliases.Length > 1;
                var next = claims.Skip(index + 1).FirstOrDefault(nextClaim => nextClaim.first > claim.last);
                var adjacentHeading = next is not null && next.first == claim.last + 1;
                var category = multipart
                    ? adjacentHeading ? (next!.aliases.Length > 1 ? "D_MULTIPART_TO_MULTIPART_HEADING" : "B_MULTIPART_TO_ADJACENT_HEADING") : "A_MULTIPART_TO_BODY"
                    : adjacentHeading ? "C_SINGLETON_TO_ADJACENT_HEADING" : null;
                if (category is not null) totals[category]++;
                var boundaryText = string.Concat(claim.aliases.Select(alias => atoms[alias].Text));
                var wrappedProxy = multipart && (atoms[claim.aliases[^2]].Text.EndsWith("-", StringComparison.Ordinal) ||
                    (atoms[claim.aliases[^1]].Text.Length > 0 && char.IsLower(atoms[claim.aliases[^1]].Text.TrimStart().FirstOrDefault())));
                if (wrappedProxy) totals["F_WRAPPED_OR_HYPHENATED_PROXY"]++;
                rows.Add(new { documentId = document.Id, aliases = claim.aliases, multipart, category, wrappedOrHyphenatedProxy = wrappedProxy,
                    textSha256 = Hashing.Sha256(boundaryText), nextHeadingPrimary = adjacentHeading ? next!.aliases[0] : null });
            }
        }
        using var g2aRaw = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{AnchorExistenceCaptureRoot}/SRC-089.raw-capture.v1.json")));
        var g2aAnchors = g2aRaw.RootElement.GetProperty("rawResponse").GetString()!;
        using var g2aResponse = JsonDocument.Parse(g2aAnchors);
        var hasAliases = g2aResponse.RootElement.GetProperty("decisions").EnumerateArray()
            .Where(item => item.GetProperty("anchor").GetString() == "HAS_STRUCTURAL_EXTENT")
            .Select(item => item.GetProperty("primary").GetString()!).ToHashSet(StringComparer.Ordinal);
        // Only SRC-089 has an observed G2A ledger, so E is explicitly scoped rather than extrapolated.
        var src089Hash = CanonicalSemanticSourceHash.Compute(TestRepository.Path(SourcePdfCorpus.Src089));
        var src089Plan = PdfCandidateAuthorityQualificationAdapter.PrepareFromSnapshot(TestRepository.Path($"{SnapshotRoot}/{src089Hash}.json"), "SRC-089");
        var src089Pack = src089Plan.Packs.First();
        var f1RetryPath = TestRepository.Path($"{F1Root}/retry-src089-result.v1.json");
        using var f1Retry = JsonDocument.Parse(File.ReadAllText(f1RetryPath));
        using var g1 = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(G1Path)));
        var src089Prepared = Prepare("SRC-089", SourcePdfCorpus.Src089, f1Retry.RootElement.GetProperty("row"), g1.RootElement.GetProperty("src089"));
        var occurrenceByAlias = src089Prepared.OccurrenceByAlias;
        var insideMultipartAnchor = rows.Where(row => JsonSerializer.Serialize(row).Contains("SRC-089", StringComparison.Ordinal) && JsonSerializer.Serialize(row).Contains("\"multipart\":true", StringComparison.Ordinal))
            .SelectMany(row => JsonDocument.Parse(JsonSerializer.Serialize(row)).RootElement.GetProperty("aliases").EnumerateArray().Skip(1).Select(item => item.GetString()!))
            .Where(occurrenceByAlias.ContainsKey)
            .Any(alias => hasAliases.Contains(occurrenceByAlias[alias]));
        Assert.False(insideMultipartAnchor);

        FreezeArtifact.AssertJson(CrossDocumentShapeAuditRoot, "cross-document-shape-audit.v1.json", new
        {
            schemaVersion = "v5-p6th3-cross-document-composition-shape-audit-v1",
            status = "FROZEN_PROVIDER_FREE_SHAPE_AUDIT",
            scope = new { documents = documents.Select(item => item.Id).ToArray(), canonicalSourceSnapshots = true, providerCalls = 0, runtimeChanged = false, goldMutation = "NONE" },
            shapes = totals,
            observedG2AAnchorInsideTrueMultipart = new { documentId = "SRC-089", evaluable = true, count = insideMultipartAnchor ? 1 : 0, verdict = "NOT_OBSERVED_ON_THE_ONLY_AVAILABLE_G2A_LEDGER" },
            unavailable = new { otherDocumentG2AAnchorLedgers = "NOT_CAPTURED", corpus004058043 = "OUT_OF_SCOPE: no P6 canonical source snapshot and no frozen G2A ledger", strictGoldDocuments = "NOT_COMPOSITION_EVALUABLE without P6 source + F1 + G2A authorities" },
            rows,
            conclusion = "SHAPE_INVENTORY_ONLY; NO_CROSS_ANCHOR_INVARIANT_IS_SUPPORTED_ON_SRC089_CANARY_BUT_NOT YET QUALIFIED ACROSS DOCUMENTS OR AGAINST FALSE_POSITIVE_ANCHORS_INSIDE_TRUE_MULTIPART_UNITS",
        });
    }

    [Fact]
    public void P6TG2A_preflight_freezes_candidate_free_anchor_existence_contract_for_src089_controls()
    {
        using var f1Retry = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{F1Root}/retry-src089-result.v1.json")));
        using var g1 = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(G1Path)));
        var prepared = Prepare("SRC-089", SourcePdfCorpus.Src089, f1Retry.RootElement.GetProperty("row"), g1.RootElement.GetProperty("src089"));
        var request = ComposeAnchorExistence(prepared);

        var positivePrimaries = new[] { "L0006:S0", "L0014:S0", "L0016:S0", "L0024:S0", "L0051:S0", "L0080:S0" };
        var continuations = new[] { "L0007:S0", "L0008:S0", "L0015:S0", "L0025:S0", "L0052:S0", "L0081:S0" };
        var frontMatter = new[] { "L0002:S0", "L0002:S1", "L0003:S1" };
        var issued = request.Primaries.Select(item => item.Alias).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(15, request.Primaries.Count);
        Assert.True(issued.SetEquals(positivePrimaries.Concat(continuations).Concat(frontMatter)));
        Assert.DoesNotContain("\"candidates\"", request.UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("\"candidate\"", request.UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("sourceParts", request.UserMessage, StringComparison.Ordinal);

        FreezeArtifact.AssertJson(AnchorExistencePreflightRoot, "anchor-existence-preflight.v1.json", new
        {
            schemaVersion = "v5-p6tg2a-anchor-existence-preflight-v1",
            status = "PREPARED_NOT_AUTHORIZED",
            protocolVersion = "v5-function-conditioned-anchor-existence-1",
            purpose = "SEPARATE_ANCHOR_EXISTENCE_FROM_EXACT_CANDIDATE_SELECTION",
            treatment = new
            {
                documentId = request.DocumentId,
                packId = "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_001",
                model = "qwen/qwen3.7-flash",
                provider = "alibaba",
                reasoning = new { enabled = true, effort = "OMITTED" },
                candidateMenus = "ABSENT",
                exactExtentSelection = "ABSENT",
                functionMembership = "READ_ONLY_UPSTREAM_ELIGIBILITY_EVIDENCE",
            },
            outputContract = new
            {
                shape = "{\"decisions\":[{\"primary\":\"O27\",\"anchor\":\"HAS_STRUCTURAL_EXTENT\"},{\"primary\":\"O28\",\"anchor\":\"NO_STRUCTURAL_EXTENT\"}]}",
                oneDecisionPerIssuedPrimary = true,
                allowedAnchors = new[] { "HAS_STRUCTURAL_EXTENT", "NO_STRUCTURAL_EXTENT" },
                modelAuthoredCandidateOrLocator = false,
                invalidDecisionPolicy = "decision-local quarantine; never infer an anchor decision",
            },
            inputInvariants = new
            {
                primaryOccurrenceOnly = true,
                candidateIdsAbsent = true,
                candidateTextAbsent = true,
                sourceCoordinatesAbsent = true,
                goldAbsent = true,
                sourceReviewAbsent = true,
                localContextReadOnly = true,
            },
            callPlan = new
            {
                providerCallsAuthorized = 0,
                providerCalls = 0,
                retries = 0,
                repairs = 0,
                fallbacks = 0,
                documentId = request.DocumentId,
                primaryCount = request.Primaries.Count,
                primaryOccurrences = request.Primaries.Select(item => new { occurrence = item.Occurrence, alias = item.Alias }).ToArray(),
                systemPromptSha256 = Hashing.Sha256(request.SystemPrompt),
                userMessageSha256 = request.MessageHash,
                userMessageUtf8Bytes = request.MessageBytes,
                providerBodySha256 = request.ProviderHash,
                providerBodyBytes = request.ProviderBytes,
            },
            auditOnlyControls = new
            {
                positiveGoldPrimaryAliases = positivePrimaries,
                continuationNegativeAliases = continuations,
                frontMatterNegativeAliases = frontMatter,
                expected = "6 HAS_STRUCTURAL_EXTENT and 9 NO_STRUCTURAL_EXTENT; controls are excluded from model-visible input semantics",
            },
            authority = new
            {
                functionMembershipRequestSha256 = prepared.F1Pack.Request.UserMessageSha256,
                candidateUniverseFingerprint = prepared.SourcePack.Universe.Fingerprint,
                goldOrReviewReadForRequestConstruction = false,
                goldMutation = "NONE",
                sharedRuntime = "UNCHANGED",
            },
            conclusion = "P6TG2A_PREFLIGHT_FROZEN; PROVIDER_EXECUTION_REQUIRES_SEPARATE_EXPLICIT_AUTHORIZATION",
        });
    }

    [Fact]
    public void P6TG2A_anchor_ledger_is_total_and_quarantines_invalid_decisions_locally()
    {
        using var f1Retry = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{F1Root}/retry-src089-result.v1.json")));
        using var g1 = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(G1Path)));
        var prepared = Prepare("SRC-089", SourcePdfCorpus.Src089, f1Retry.RootElement.GetProperty("row"), g1.RootElement.GetProperty("src089"));
        var request = ComposeAnchorExistence(prepared);
        var valid = JsonSerializer.Serialize(new
        {
            decisions = request.Primaries.Select((item, index) => new
            {
                primary = item.Occurrence,
                anchor = index % 2 == 0 ? "HAS_STRUCTURAL_EXTENT" : "NO_STRUCTURAL_EXTENT",
            }).ToArray(),
        });
        var accepted = ParseAnchorLedger(request, valid);
        Assert.Equal(15, accepted.RawDecisions);
        Assert.Equal(8, accepted.HasStructuralExtent);
        Assert.Equal(7, accepted.NoStructuralExtent);
        Assert.Equal(0, accepted.Quarantined);
        Assert.Equal(0, accepted.MissingPrimaries);

        var malformed = JsonSerializer.Serialize(new
        {
            decisions = request.Primaries.Skip(1).Select(item => new { primary = item.Occurrence, anchor = "HAS_STRUCTURAL_EXTENT" })
                .Append(new { primary = "O999", anchor = "HAS_STRUCTURAL_EXTENT" })
                .Append(new { primary = request.Primaries[0].Occurrence, anchor = "INVALID" })
                .ToArray(),
        });
        var quarantined = ParseAnchorLedger(request, malformed);
        Assert.Equal(16, quarantined.RawDecisions);
        Assert.True(quarantined.Quarantined >= 2);
        Assert.Equal(14, quarantined.HasStructuralExtent);
        Assert.Equal(1, quarantined.MissingPrimaries);
    }

    [Fact]
    public async Task Run_exactly_one_frozen_primary_call_only_when_explicitly_enabled_for_P6TG2A()
    {
        if (Environment.GetEnvironmentVariable(RunAnchorExistenceVariable) is not ("1" or "true" or "TRUE")) return;

        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        Assert.False(string.IsNullOrWhiteSpace(apiKey), "OPENROUTER_API_KEY is required for the explicitly enabled P6T-G2A canary.");
        var capturePath = TestRepository.Path(AnchorExistenceCaptureRoot);
        Assert.False(Directory.Exists(capturePath), "P6T-G2A capture root already exists; automatic resume or resend is forbidden.");

        using var f1Retry = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{F1Root}/retry-src089-result.v1.json")));
        using var g1 = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(G1Path)));
        var prepared = Prepare("SRC-089", SourcePdfCorpus.Src089, f1Retry.RootElement.GetProperty("row"), g1.RootElement.GetProperty("src089"));
        var request = ComposeAnchorExistence(prepared);
        using var preflight = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{AnchorExistencePreflightRoot}/anchor-existence-preflight.v1.json")));
        var frozen = preflight.RootElement;
        Assert.Equal("PREPARED_NOT_AUTHORIZED", frozen.GetProperty("status").GetString());
        var callPlan = frozen.GetProperty("callPlan");
        Assert.Equal(request.DocumentId, callPlan.GetProperty("documentId").GetString());
        Assert.Equal(request.Primaries.Count, callPlan.GetProperty("primaryCount").GetInt32());
        Assert.Equal(request.MessageHash, callPlan.GetProperty("userMessageSha256").GetString());
        Assert.Equal(request.MessageBytes, callPlan.GetProperty("userMessageUtf8Bytes").GetInt32());
        Assert.Equal(request.ProviderHash, callPlan.GetProperty("providerBodySha256").GetString());
        Assert.Equal(request.ProviderBytes, callPlan.GetProperty("providerBodyBytes").GetInt32());

        Directory.CreateDirectory(capturePath);
        WriteNew(Path.Combine(capturePath, "execution-reservation.v1.json"), new
        {
            schemaVersion = "v5-p6tg2a-execution-reservation-v1",
            status = "ONE_PRIMARY_SLOT_RESERVED",
            documentId = request.DocumentId,
            providerRequestHash = request.ProviderHash,
            providerRequestBytes = request.ProviderBytes,
            providerCallsBeforeSend = 0,
            retriesAllowed = 0,
            repairsAllowed = false,
            fallbacksAllowed = false,
            goldRead = false,
        });

        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var provider = new OpenRouterQualificationTransport(http, new RemoteInferenceOptions
        {
            ApiKey = apiKey!,
            Model = "qwen/qwen3.7-flash",
            OpenRouterProviderRoute = "Alibaba",
            TransientRequestRetries = 0,
            MaxParallelRequests = 1,
            ProviderTransportTimeoutSeconds = 300,
            RequireZeroDataRetention = false,
        });

        try
        {
            var observation = await provider.ExecuteObservedAsync(request.ProviderBody, request.MaxCompletionTokens,
                request.SystemPrompt, request.UserMessage, CancellationToken.None);
            Assert.Equal(0, observation.RetryCount);
            var rawCapture = new
            {
                schemaVersion = "v5-p6tg2a-raw-provider-capture-v1",
                documentId = request.DocumentId,
                packId = "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_001",
                provider = "OpenRouter",
                model = "qwen/qwen3.7-flash",
                providerRoute = "Alibaba",
                reasoningRequested = true,
                reasoningEffort = "OMITTED",
                providerRequestHash = request.ProviderHash,
                providerRequestBytes = request.ProviderBytes,
                semanticRequestHash = request.MessageHash,
                finishReason = observation.FinishReason,
                usage = observation.Usage,
                promptTokens = UsageInt(observation.Usage, "prompt_tokens"),
                completionTokens = UsageInt(observation.Usage, "completion_tokens"),
                reasoningTokens = UsageInt(observation.Usage, "reasoning_tokens"),
                reasoningExecutionConfirmed = UsageInt(observation.Usage, "reasoning_tokens") is > 0,
                sseEventCount = observation.SseEventCount,
                retryCount = observation.RetryCount,
                rawSseSha256 = Hashing.Sha256(observation.RawSse),
                rawResponseSha256 = Hashing.Sha256(observation.Content),
                rawSseUtf8Bytes = Encoding.UTF8.GetByteCount(observation.RawSse),
                rawResponseUtf8Bytes = Encoding.UTF8.GetByteCount(observation.Content),
                rawSse = observation.RawSse,
                rawResponse = observation.Content,
            };
            // Persist response bytes before ParseAnchorLedger and before any source or Gold audit.
            WriteNew(Path.Combine(capturePath, "SRC-089.raw-capture.v1.json"), rawCapture);
            ParsedAnchorLedger? parsed = null;
            string? parserError = null;
            try { parsed = ParseAnchorLedger(request, observation.Content); }
            catch (Exception error) { parserError = error.GetType().Name + ": " + error.Message; }
            WriteNew(Path.Combine(capturePath, "result.v1.json"), new
            {
                schemaVersion = "v5-p6tg2a-one-primary-call-result-v1",
                status = "EXECUTION_SET_FROZEN",
                providerCalls = 1,
                retries = 0,
                repairs = 0,
                fallbacks = 0,
                goldRead = false,
                goldMutation = "NONE",
                runtimeChanged = false,
                rawCaptureSha256 = Hashing.Sha256(File.ReadAllText(Path.Combine(capturePath, "SRC-089.raw-capture.v1.json"))),
                row = new
                {
                    documentId = request.DocumentId,
                    transportStatus = "COMPLETED",
                    finishReason = observation.FinishReason,
                    parserStatus = parsed is null ? "REJECTED" : "PARSED",
                    parserError,
                    rawResponseSha256 = Hashing.Sha256(observation.Content),
                    promptTokens = UsageInt(observation.Usage, "prompt_tokens"),
                    completionTokens = UsageInt(observation.Usage, "completion_tokens"),
                    reasoningTokens = UsageInt(observation.Usage, "reasoning_tokens"),
                    retryCount = observation.RetryCount,
                    parsed,
                },
            });
            Assert.Equal("stop", observation.FinishReason);
            Assert.NotNull(parsed);
            Assert.Equal(15, parsed!.RawDecisions);
            Assert.Equal(0, parsed.Quarantined);
            Assert.Equal(0, parsed.MissingPrimaries);
        }
        catch (Exception error) when (error is not Xunit.Sdk.XunitException)
        {
            WriteNew(Path.Combine(capturePath, "transport-failure.v1.json"), new
            {
                schemaVersion = "v5-p6tg2a-transport-failure-v1",
                documentId = request.DocumentId,
                providerRequestHash = request.ProviderHash,
                errorType = error.GetType().FullName,
                message = error.Message,
                retryCount = 0,
                goldRead = false,
            });
            WriteNew(Path.Combine(capturePath, "result.v1.json"), new
            {
                schemaVersion = "v5-p6tg2a-one-primary-call-result-v1",
                status = "EXECUTION_SET_FROZEN",
                providerCalls = 1,
                retries = 0,
                repairs = 0,
                fallbacks = 0,
                goldRead = false,
                goldMutation = "NONE",
                runtimeChanged = false,
                row = new { documentId = request.DocumentId, transportStatus = "FAILED", errorType = error.GetType().FullName, retryCount = 0 },
            });
            throw;
        }
    }

    [Fact]
    public void P6TG2A_frozen_canary_is_audited_against_the_pre_registered_three_control_groups_without_provider_calls()
    {
        var capturePath = TestRepository.Path(AnchorExistenceCaptureRoot);
        using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(capturePath, "result.v1.json")));
        using var raw = JsonDocument.Parse(File.ReadAllText(Path.Combine(capturePath, "SRC-089.raw-capture.v1.json")));
        using var f1Retry = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{F1Root}/retry-src089-result.v1.json")));
        using var g1 = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(G1Path)));
        using var preflight = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{AnchorExistencePreflightRoot}/anchor-existence-preflight.v1.json")));

        var resultRoot = result.RootElement;
        Assert.Equal("EXECUTION_SET_FROZEN", resultRoot.GetProperty("status").GetString());
        Assert.Equal(1, resultRoot.GetProperty("providerCalls").GetInt32());
        Assert.Equal(0, resultRoot.GetProperty("retries").GetInt32());
        Assert.Equal(0, resultRoot.GetProperty("repairs").GetInt32());
        Assert.Equal(0, resultRoot.GetProperty("fallbacks").GetInt32());
        Assert.False(resultRoot.GetProperty("goldRead").GetBoolean());
        Assert.Equal("NONE", resultRoot.GetProperty("goldMutation").GetString());
        var rawRoot = raw.RootElement;
        Assert.Equal(resultRoot.GetProperty("rawCaptureSha256").GetString(), Hashing.Sha256(File.ReadAllText(Path.Combine(capturePath, "SRC-089.raw-capture.v1.json"))));

        var prepared = Prepare("SRC-089", SourcePdfCorpus.Src089, f1Retry.RootElement.GetProperty("row"), g1.RootElement.GetProperty("src089"));
        var request = ComposeAnchorExistence(prepared);
        Assert.Equal(request.ProviderHash, rawRoot.GetProperty("providerRequestHash").GetString());
        Assert.Equal(request.MessageHash, rawRoot.GetProperty("semanticRequestHash").GetString());
        Assert.Equal("stop", rawRoot.GetProperty("finishReason").GetString());
        Assert.Equal(0, rawRoot.GetProperty("retryCount").GetInt32());
        Assert.True(rawRoot.GetProperty("reasoningExecutionConfirmed").GetBoolean());
        var rawResponse = rawRoot.GetProperty("rawResponse").GetString()!;
        Assert.Equal(rawRoot.GetProperty("rawResponseSha256").GetString(), Hashing.Sha256(rawResponse));
        var ledger = ParseAnchorLedger(request, rawResponse);
        Assert.Equal(15, ledger.RawDecisions);
        Assert.Equal(0, ledger.Quarantined);
        Assert.Equal(0, ledger.MissingPrimaries);

        var byOccurrence = ledger.Decisions.Select(item => JsonDocument.Parse(JsonSerializer.Serialize(item))).ToArray();
        try
        {
            var aliasesByOccurrence = request.Primaries.ToDictionary(item => item.Occurrence, item => item.Alias, StringComparer.Ordinal);
            var outcomeByAlias = byOccurrence.ToDictionary(item => aliasesByOccurrence[item.RootElement.GetProperty("primary").GetString()!],
                item => item.RootElement.GetProperty("anchor").GetString()!, StringComparer.Ordinal);
            var controlGroups = preflight.RootElement.GetProperty("auditOnlyControls");
            var positive = controlGroups.GetProperty("positiveGoldPrimaryAliases").EnumerateArray().Select(item => item.GetString()!).ToArray();
            var continuation = controlGroups.GetProperty("continuationNegativeAliases").EnumerateArray().Select(item => item.GetString()!).ToArray();
            var frontMatter = controlGroups.GetProperty("frontMatterNegativeAliases").EnumerateArray().Select(item => item.GetString()!).ToArray();
            Assert.Equal(6, positive.Length); Assert.Equal(6, continuation.Length); Assert.Equal(3, frontMatter.Length);
            Assert.All(positive, alias => Assert.Equal("HAS_STRUCTURAL_EXTENT", outcomeByAlias[alias]));
            Assert.All(continuation, alias => Assert.Equal("NO_STRUCTURAL_EXTENT", outcomeByAlias[alias]));
            Assert.All(frontMatter, alias => Assert.Equal("NO_STRUCTURAL_EXTENT", outcomeByAlias[alias]));

            FreezeArtifact.AssertJson("artifacts/v5-p6t-function-membership/p6tg2a-anchor-existence-audit", "anchor-existence-canary-audit.v1.json", new
            {
                schemaVersion = "v5-p6tg2a-anchor-existence-canary-audit-v1",
                authority = new
                {
                    executionResultSha256 = Hashing.Sha256(File.ReadAllText(Path.Combine(capturePath, "result.v1.json"))),
                    rawCaptureSha256 = resultRoot.GetProperty("rawCaptureSha256").GetString(),
                    rawResponseSha256 = rawRoot.GetProperty("rawResponseSha256").GetString(),
                    preflightSha256 = Hashing.Sha256(File.ReadAllText(TestRepository.Path($"{AnchorExistencePreflightRoot}/anchor-existence-preflight.v1.json"))),
                    providerCallsDuringAudit = 0,
                    goldReadDuringAudit = false,
                    rawCaptureMutation = "NONE",
                },
                execution = new
                {
                    finishReason = rawRoot.GetProperty("finishReason").GetString(),
                    retryCount = rawRoot.GetProperty("retryCount").GetInt32(),
                    reasoningExecutionConfirmed = rawRoot.GetProperty("reasoningExecutionConfirmed").GetBoolean(),
                    reasoningTokens = rawRoot.GetProperty("reasoningTokens").GetInt32(),
                    rawDecisions = ledger.RawDecisions,
                    quarantined = ledger.Quarantined,
                    missingPrimaries = ledger.MissingPrimaries,
                },
                probes = new
                {
                    trueHeadingAnchors = new { expected = "HAS_STRUCTURAL_EXTENT", total = positive.Length, has = positive.Count(alias => outcomeByAlias[alias] == "HAS_STRUCTURAL_EXTENT"), no = positive.Count(alias => outcomeByAlias[alias] == "NO_STRUCTURAL_EXTENT"), aliases = positive },
                    continuationNegativeControls = new { expected = "NO_STRUCTURAL_EXTENT", total = continuation.Length, has = continuation.Count(alias => outcomeByAlias[alias] == "HAS_STRUCTURAL_EXTENT"), no = continuation.Count(alias => outcomeByAlias[alias] == "NO_STRUCTURAL_EXTENT"), aliases = continuation },
                    frontMatterNegativeControls = new { expected = "NO_STRUCTURAL_EXTENT", total = frontMatter.Length, has = frontMatter.Count(alias => outcomeByAlias[alias] == "HAS_STRUCTURAL_EXTENT"), no = frontMatter.Count(alias => outcomeByAlias[alias] == "NO_STRUCTURAL_EXTENT"), aliases = frontMatter },
                },
                conclusion = "G2A_ANCHOR_EXISTENCE_SUPPORTED_ON_PRE_REGISTERED_SRC089_15_PRIMARY_PROBE; G2B_EXACT_EXTENT_REMAINS_BLOCKED_PENDING_SEPARATE_AUTHORIZATION",
            });
        }
        finally
        {
            foreach (var item in byOccurrence) item.Dispose();
        }
    }

    [Fact]
    public void P6TG2B_preflight_uses_only_raw_G2A_HAS_primaries_and_reuses_the_frozen_G2_menus()
    {
        using var f1Retry = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{F1Root}/retry-src089-result.v1.json")));
        using var g1 = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(G1Path)));
        using var g2 = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{OutputRoot}/exact-extent-resolver-preflight.v1.json")));
        using var g2aResult = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{AnchorExistenceCaptureRoot}/result.v1.json")));
        using var g2aRaw = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{AnchorExistenceCaptureRoot}/SRC-089.raw-capture.v1.json")));
        var prepared = Prepare("SRC-089", SourcePdfCorpus.Src089, f1Retry.RootElement.GetProperty("row"), g1.RootElement.GetProperty("src089"));
        var allG2 = Compose(prepared, new HashSet<string>(StringComparer.Ordinal));
        var anchorRequest = ComposeAnchorExistence(prepared);
        var anchorLedger = ParseAnchorLedger(anchorRequest, g2aRaw.RootElement.GetProperty("rawResponse").GetString()!);
        var hasPrimaries = anchorLedger.Decisions.Select(item => JsonDocument.Parse(JsonSerializer.Serialize(item)))
            .Where(item => item.RootElement.GetProperty("anchor").GetString() == "HAS_STRUCTURAL_EXTENT")
            .Select(item => item.RootElement.GetProperty("primary").GetString()!).ToHashSet(StringComparer.Ordinal);
        try
        {
            Assert.Equal(6, hasPrimaries.Count);
            Assert.Equal(1, g2aResult.RootElement.GetProperty("providerCalls").GetInt32());
            Assert.False(g2aResult.RootElement.GetProperty("goldRead").GetBoolean());
            var frozenCombinedRows = g2.RootElement.GetProperty("callPlan").EnumerateArray()
                .Single(row => row.GetProperty("documentId").GetString() == "SRC-089")
                .GetProperty("optionsPerPrimary").EnumerateArray()
                .ToDictionary(row => row.GetProperty("primary").GetString()!,
                    row => row.GetProperty("candidateIds").EnumerateArray().Select(item => item.GetString()!).ToArray(), StringComparer.Ordinal);
            var selected = allG2.OccurrenceGroups.Where(group => hasPrimaries.Contains(group.PrimaryOccurrence)).ToArray();
            Assert.Equal(6, selected.Length);
            foreach (var group in selected)
            {
                Assert.Equal(frozenCombinedRows[group.PrimaryOccurrence], group.CandidateIds);
                Assert.NotEmpty(group.CandidateIds);
            }

            var g2b = ComposeExactExtentFromHas(prepared, hasPrimaries);
            FreezeArtifact.AssertJson(ExactExtentPreflightRoot, "exact-extent-preflight.v1.json", new
            {
                schemaVersion = "v5-p6tg2b-exact-extent-preflight-v1",
                status = "PREPARED_NOT_AUTHORIZED",
                protocolVersion = "v5-function-conditioned-exact-extent-resolver-2",
                treatment = new
                {
                    model = "qwen/qwen3.7-flash",
                    provider = "alibaba",
                    reasoning = new { enabled = true, effort = "OMITTED" },
                    anchorAuthority = "FROZEN_P6TG2A_RAW_HAS_DECISIONS",
                    primarySelection = "RAW_G2A_ONLY; GOLD_NOT_USED_TO_SELECT_PRIMARY",
                    extentAuthority = "FROZEN_G2_CANDIDATE_MENUS",
                },
                outputContract = new
                {
                    shape = "{\"decisions\":[{\"primary\":\"O27\",\"candidate\":\"C123\"}]}",
                    oneDecisionPerIssuedHasPrimary = true,
                    sentinelAllowed = false,
                    candidateMustBeIssuedForThatPrimary = true,
                    modelAuthoredTextOrCoordinates = false,
                    invalidDecisionPolicy = "decision-local quarantine; no repair or inference",
                },
                sourceAuthority = new
                {
                    g2aResultSha256 = Hashing.Sha256(File.ReadAllText(TestRepository.Path($"{AnchorExistenceCaptureRoot}/result.v1.json"))),
                    g2aRawResponseSha256 = g2aRaw.RootElement.GetProperty("rawResponseSha256").GetString(),
                    g2aHasPrimaryCount = hasPrimaries.Count,
                    g2aHasPrimaries = selected.Select(group => new { primary = group.PrimaryOccurrence, alias = group.PrimaryAlias }).ToArray(),
                    combinedG2PreflightSha256 = Hashing.Sha256(File.ReadAllText(TestRepository.Path($"{OutputRoot}/exact-extent-resolver-preflight.v1.json"))),
                    menuByteParity = true,
                    goldMutation = "NONE",
                    sharedRuntime = "UNCHANGED",
                },
                callPlan = new
                {
                    documentId = g2b.DocumentId,
                    primaryCount = g2b.OccurrenceGroups.Count,
                    primaryOccurrences = g2b.OccurrenceGroups.Select(group => new { occurrence = group.PrimaryOccurrence, alias = group.PrimaryAlias }).ToArray(),
                    eligibleCandidateCount = g2b.OccurrenceGroups.Sum(group => group.CandidateIds.Count),
                    optionsPerPrimary = g2b.OccurrenceGroups.Select(group => new { primary = group.PrimaryOccurrence, primaryAlias = group.PrimaryAlias, candidateIds = group.CandidateIds }).ToArray(),
                    systemPromptSha256 = Hashing.Sha256(g2b.SystemPrompt),
                    userMessageSha256 = g2b.MessageHash,
                    userMessageUtf8Bytes = g2b.MessageBytes,
                    providerBodySha256 = g2b.ProviderHash,
                    providerBodyBytes = g2b.ProviderBytes,
                    providerCallsAuthorized = 0,
                    providerCalls = 0,
                    retries = 0,
                    repairs = 0,
                    fallbacks = 0,
                },
                expectedOfflineProbes = new
                {
                    goldExtents = 6,
                    multipartGoldExtents = 5,
                    singletonGoldExtents = 1,
                    primaryWholeBiasDiagnostic = true,
                    expectedCandidateIdsAreAuditOnly = true,
                },
                conclusion = "P6TG2B_PREFLIGHT_FROZEN_FROM_RAW_G2A_HAS_DECISIONS; PROVIDER_EXECUTION_REQUIRES_SEPARATE_EXPLICIT_AUTHORIZATION",
            });
        }
        finally
        {
            foreach (var item in anchorLedger.Decisions.Select(item => JsonDocument.Parse(JsonSerializer.Serialize(item)))) item.Dispose();
        }
    }

    private static PreparedRequest ComposeExactExtentFromHas(PreparedDocument prepared, IReadOnlySet<string> hasPrimaries)
    {
        var allG2 = Compose(prepared, new HashSet<string>(StringComparer.Ordinal));
        var userDocument = JsonDocument.Parse(allG2.UserMessage);
        try
        {
            var selectedGroups = userDocument.RootElement.GetProperty("occurrenceGroups").EnumerateArray()
                .Where(group => hasPrimaries.Contains(group.GetProperty("primary").GetString()!)).ToArray();
            var selected = allG2.OccurrenceGroups.Where(group => hasPrimaries.Contains(group.PrimaryOccurrence)).ToArray();
            Assert.Equal(selected.Length, selectedGroups.Length);
            var systemPrompt = """
                Resolve exact extent only for the already-established structural anchors supplied by the harness. Anchor existence has already been decided upstream; do not reconsider whether an anchor exists.

                For every issued primary O#, return exactly one decision selecting exactly one C# from that primary's frozen candidate menu. Candidate IDs, kinds, source-part text, ordering and local context are harness-issued evidence. Do not create, edit, join, trim, retype or infer candidate extents. Do not return NO_STRUCTURAL_EXTENT. Do not output source text, coordinates, aliases, locators, relations, hierarchy, rationale, confidence, or extra properties.

                Return exactly one JSON object with this shape: {"decisions":[{"primary":"O27","candidate":"C123"}]}. Each decision has exactly primary and candidate.
                """;
            var userMessage = JsonSerializer.Serialize(new
            {
                protocolVersion = "v5-function-conditioned-exact-extent-resolver-2",
                occurrenceGroups = selectedGroups,
            });
            var requestModel = new V5FreeHeadingRequestV1("v5-function-conditioned-exact-extent-resolver-2", systemPrompt,
                userMessage, Hashing.Sha256(userMessage), Encoding.UTF8.GetByteCount(systemPrompt), Encoding.UTF8.GetByteCount(userMessage));
            var body = PdfCandidateAuthorityQualificationAdapter.BuildProviderBodyReasoningEnabled(requestModel, prepared.SourcePack.MaxCompletionTokens);
            return new PreparedRequest(prepared.DocumentId, systemPrompt, userMessage, requestModel.UserMessageSha256,
                requestModel.UserMessageUtf8Bytes, body.PayloadBytes, body.Bytes, body.Hash, selected, prepared.SourcePack.MaxCompletionTokens);
        }
        finally
        {
            userDocument.Dispose();
        }
    }

    private static IReadOnlyList<IndependentJudgmentRequest> ComposeIndependentCandidateJudgments(PreparedDocument prepared,
        PreparedRequest menuRequest)
    {
        var document = JsonNode.Parse(menuRequest.UserMessage)!.AsObject();
        var universe = prepared.SourcePack.Universe.Candidates.ToDictionary(candidate => candidate.Id, StringComparer.Ordinal);
        const string systemPrompt = """
            Judge one harness-issued candidate proposition for an already-established structural anchor. This request contains exactly one candidate extent, not a menu; do not compare it to any unissued or hypothetical extent.

            Return EXACT_STRUCTURAL_EXTENT only when this supplied candidate itself is the exact full source extent of a local structural heading. Return NOT_EXACT_STRUCTURAL_EXTENT otherwise, including when the candidate is too short, too long, a continuation, front matter, or another structural-looking but non-heading extent.

            Return exactly one JSON object with this shape: {"primary":"O17","candidate":"C104","judgment":"EXACT_STRUCTURAL_EXTENT"}. The primary and candidate must exactly echo the issued values. Do not output text, coordinates, aliases, locators, relations, hierarchy, rationale, confidence, or any extra property.
            """;
        var requests = new List<IndependentJudgmentRequest>();
        foreach (var groupNode in document["occurrenceGroups"]!.AsArray())
        {
            var group = groupNode!.AsObject();
            var primary = group["primary"]!.GetValue<string>();
            var primaryAlias = menuRequest.OccurrenceGroups.Single(item => item.PrimaryOccurrence == primary).PrimaryAlias;
            foreach (var candidateNode in group["candidates"]!.AsArray())
            {
                var candidate = candidateNode!.AsObject();
                var candidateId = candidate["id"]!.GetValue<string>();
                Assert.True(universe.ContainsKey(candidateId), $"independent-candidate-not-issued:{candidateId}");
                var messageRoot = new JsonObject
                {
                    ["protocolVersion"] = IndependentJudgmentProtocol,
                    ["primary"] = primary,
                    ["function"] = group["function"]!.DeepClone(),
                    ["primaryText"] = group["primaryText"]!.DeepClone(),
                    ["candidate"] = candidate.DeepClone(),
                    ["context"] = group["context"]!.DeepClone(),
                };
                var userMessage = messageRoot.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
                var requestModel = new V5FreeHeadingRequestV1(IndependentJudgmentProtocol, systemPrompt, userMessage,
                    Hashing.Sha256(userMessage), Encoding.UTF8.GetByteCount(systemPrompt), Encoding.UTF8.GetByteCount(userMessage));
                var body = PdfCandidateAuthorityQualificationAdapter.BuildProviderBodyReasoningEnabled(requestModel,
                    menuRequest.MaxCompletionTokens);
                requests.Add(new IndependentJudgmentRequest(prepared.DocumentId, primary, primaryAlias, candidateId,
                    universe[candidateId].Kind.ToString(), universe[candidateId].SpanIdentity, systemPrompt, userMessage,
                    requestModel.UserMessageSha256, requestModel.UserMessageUtf8Bytes, body.PayloadBytes, body.Bytes, body.Hash,
                    menuRequest.MaxCompletionTokens));
            }
        }
        return requests.OrderBy(request => request.Primary, StringComparer.Ordinal)
            .ThenBy(request => request.CandidateId, StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyList<IndependentAggregate> AggregateIndependentJudgments(
        IEnumerable<IndependentJudgmentLedger> judgments, IEnumerable<string> issuedPrimaries)
    {
        var byPrimary = judgments.GroupBy(judgment => judgment.Primary, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        return issuedPrimaries.OrderBy(primary => primary, StringComparer.Ordinal).Select(primary =>
        {
            var exact = byPrimary.GetValueOrDefault(primary, []).Where(judgment => judgment.Judgment == "EXACT_STRUCTURAL_EXTENT")
                .Select(judgment => judgment.Candidate).OrderBy(candidate => candidate, StringComparer.Ordinal).ToArray();
            return exact.Length switch
            {
                0 => new IndependentAggregate(primary, "NO_EXACT_EXTENT", null, exact),
                1 => new IndependentAggregate(primary, "SELECTED_EXACT_EXTENT", exact[0], exact),
                _ => new IndependentAggregate(primary, "CONFLICT_MULTIPLE_EXACT", null, exact),
            };
        }).ToArray();
    }

    private static IndependentJudgmentLedger ParseIndependentJudgment(IndependentJudgmentRequest request, string raw)
    {
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 3 ||
            !root.TryGetProperty("primary", out var primary) || primary.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("candidate", out var candidate) || candidate.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("judgment", out var judgment) || judgment.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("independent-judgment-schema-invalid");

        if (!StringComparer.Ordinal.Equals(request.Primary, primary.GetString()))
            throw new InvalidOperationException("independent-judgment-primary-not-issued");
        if (!StringComparer.Ordinal.Equals(request.CandidateId, candidate.GetString()))
            throw new InvalidOperationException("independent-judgment-candidate-not-issued");
        var value = judgment.GetString()!;
        if (value is not ("EXACT_STRUCTURAL_EXTENT" or "NOT_EXACT_STRUCTURAL_EXTENT"))
            throw new InvalidOperationException("independent-judgment-enum-invalid");
        return new IndependentJudgmentLedger(request.Primary, request.CandidateId, value);
    }

    private static ContinuationBoundaryRequest ComposeContinuationBoundaries(PreparedDocument prepared,
        IReadOnlyCollection<string> anchorOccurrences)
    {
        var atoms = prepared.Plan.SourceAtoms.ToDictionary(atom => atom.Alias, StringComparer.Ordinal);
        var owned = prepared.SourcePack.OwnedAliases.ToArray();
        var aliasByOccurrence = prepared.OccurrenceByAlias.ToDictionary(item => item.Value, item => item.Key, StringComparer.Ordinal);
        var rows = new List<object>();
        var edges = new List<ContinuationEdge>();
        foreach (var anchor in anchorOccurrences.OrderBy(item => item, StringComparer.Ordinal))
        {
            Assert.True(aliasByOccurrence.TryGetValue(anchor, out var anchorAlias), $"h2-anchor-not-issued:{anchor}");
            var index = Array.IndexOf(owned, anchorAlias);
            Assert.True(index >= 0, $"h2-anchor-not-owned:{anchorAlias}");
            Assert.True(index + 3 < owned.Length, $"h2-insufficient-following-owned-occurrences:{anchorAlias}");
            var chainAliases = owned.Skip(index).Take(4).ToArray();
            var chain = chainAliases.Select(alias => new
            {
                occurrence = prepared.OccurrenceByAlias[alias],
                page = atoms[alias].Page,
                text = atoms[alias].Text,
                upstreamFunction = prepared.FunctionByAlias.GetValueOrDefault(alias, "UNCLASSIFIED"),
                selectable = false,
            }).ToArray();
            var chainEdges = Enumerable.Range(0, 3).Select(ordinal => new ContinuationEdge(anchor,
                prepared.OccurrenceByAlias[chainAliases[ordinal]], prepared.OccurrenceByAlias[chainAliases[ordinal + 1]], ordinal)).ToArray();
            edges.AddRange(chainEdges);
            rows.Add(new { anchor, occurrences = chain, edges = chainEdges.Select(edge => new { left = edge.Left, right = edge.Right, ordinal = edge.Ordinal }).ToArray() });
        }

        const string systemPrompt = """
            Judge only source-order continuation boundaries for already-qualified structural anchors. For each issued edge, decide whether right continues the same local structural unit begun at anchor, or whether the unit stops before right.

            CONTINUES_STRUCTURAL_UNIT means left and right belong to the same exact local structural unit. STOPS_STRUCTURAL_UNIT means the unit begun at anchor ends before right. The edges are consecutive source occurrences; do not use similarity, hierarchy, candidates, spans, locators, or hypothetical text not issued in the request. Once an anchor stops, every later issued edge for that anchor must also be STOPS_STRUCTURAL_UNIT.

            Return exactly one JSON object: {"decisions":[{"anchor":"O9","left":"O9","right":"O10","boundary":"CONTINUES_STRUCTURAL_UNIT"}]}. Return exactly one decision for every issued edge. Echo only issued O# values. Do not output candidate IDs, source text, coordinates, aliases, locators, relations, hierarchy, rationale, confidence, or extra properties.
            """;
        var userMessage = JsonSerializer.Serialize(new
        {
            protocolVersion = ContinuationBoundaryProtocol,
            anchors = rows,
        });
        var request = new V5FreeHeadingRequestV1(ContinuationBoundaryProtocol, systemPrompt, userMessage,
            Hashing.Sha256(userMessage), Encoding.UTF8.GetByteCount(systemPrompt), Encoding.UTF8.GetByteCount(userMessage));
        var body = PdfCandidateAuthorityQualificationAdapter.BuildProviderBodyReasoningEnabled(request,
            prepared.SourcePack.MaxCompletionTokens);
        return new ContinuationBoundaryRequest(prepared.DocumentId, systemPrompt, userMessage, request.UserMessageSha256,
            request.UserMessageUtf8Bytes, body.PayloadBytes, body.Bytes, body.Hash, edges, prepared.SourcePack.MaxCompletionTokens);
    }

    private static IReadOnlyList<ContinuationBoundaryLedger> ParseContinuationBoundaries(ContinuationBoundaryRequest request, string raw)
    {
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 ||
            !root.TryGetProperty("decisions", out var decisions) || decisions.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("h2-response-root-invalid");
        var issued = request.Edges.ToDictionary(edge => $"{edge.Anchor}|{edge.Left}|{edge.Right}", StringComparer.Ordinal);
        var parsed = new Dictionary<string, ContinuationBoundaryLedger>(StringComparer.Ordinal);
        foreach (var decision in decisions.EnumerateArray())
        {
            if (decision.ValueKind != JsonValueKind.Object || decision.EnumerateObject().Count() != 4 ||
                !decision.TryGetProperty("anchor", out var anchor) || anchor.ValueKind != JsonValueKind.String ||
                !decision.TryGetProperty("left", out var left) || left.ValueKind != JsonValueKind.String ||
                !decision.TryGetProperty("right", out var right) || right.ValueKind != JsonValueKind.String ||
                !decision.TryGetProperty("boundary", out var boundary) || boundary.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("h2-decision-schema-invalid");
            var key = $"{anchor.GetString()}|{left.GetString()}|{right.GetString()}";
            if (!issued.ContainsKey(key)) throw new InvalidOperationException("h2-edge-not-issued");
            if (!parsed.TryAdd(key, new ContinuationBoundaryLedger(anchor.GetString()!, left.GetString()!, right.GetString()!, boundary.GetString()!)))
                throw new InvalidOperationException("h2-edge-duplicate");
            if (boundary.GetString() is not ("CONTINUES_STRUCTURAL_UNIT" or "STOPS_STRUCTURAL_UNIT"))
                throw new InvalidOperationException("h2-boundary-enum-invalid");
        }
        if (parsed.Count != issued.Count) throw new InvalidOperationException("h2-edge-missing");
        var result = request.Edges.Select(edge => parsed[$"{edge.Anchor}|{edge.Left}|{edge.Right}"]).ToArray();
        foreach (var group in result.GroupBy(item => item.Anchor, StringComparer.Ordinal))
        {
            var stopped = false;
            foreach (var edge in group)
            {
                if (stopped && edge.Boundary != "STOPS_STRUCTURAL_UNIT")
                    throw new InvalidOperationException("h2-continuation-after-stop");
                stopped |= edge.Boundary == "STOPS_STRUCTURAL_UNIT";
            }
        }
        return result;
    }

    private static IReadOnlyList<ReconstructedContinuation> ReconstructContinuationBoundaries(
        ContinuationBoundaryRequest request, IReadOnlyList<ContinuationBoundaryLedger> decisions)
    {
        var byAnchor = decisions.GroupBy(item => item.Anchor, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        return request.Edges.GroupBy(edge => edge.Anchor, StringComparer.Ordinal).Select(group =>
        {
            var edges = group.OrderBy(edge => edge.Ordinal).ToArray();
            if (!byAnchor.TryGetValue(group.Key, out var values) || values.Length != edges.Length)
                throw new InvalidOperationException("h2-reconstruction-incomplete");
            var choice = values.ToDictionary(item => $"{item.Left}|{item.Right}", StringComparer.Ordinal);
            var aliases = new List<string> { edges[0].Left };
            foreach (var edge in edges)
            {
                if (choice[$"{edge.Left}|{edge.Right}"].Boundary == "STOPS_STRUCTURAL_UNIT") break;
                aliases.Add(edge.Right);
            }
            return new ReconstructedContinuation(group.Key, "RECONSTRUCTED", aliases);
        }).OrderBy(item => item.Anchor, StringComparer.Ordinal).ToArray();
    }

    private static PreparedRequest ComposeExactExtentWithCandidateOrder(PreparedDocument prepared, PreparedRequest baseline, bool reverse)
    {
        var document = JsonNode.Parse(baseline.UserMessage)!.AsObject();
        var groups = document["occurrenceGroups"]!.AsArray();
        foreach (var node in groups)
        {
            var candidates = node!["candidates"]!.AsArray();
            var values = candidates.ToList();
            candidates.Clear();
            var ordered = reverse ? values.AsEnumerable().Reverse() : values;
            foreach (var value in ordered) candidates.Add(value);
        }

        var userMessage = document.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        var requestModel = new V5FreeHeadingRequestV1("v5-function-conditioned-exact-extent-resolver-2", baseline.SystemPrompt,
            userMessage, Hashing.Sha256(userMessage), Encoding.UTF8.GetByteCount(baseline.SystemPrompt), Encoding.UTF8.GetByteCount(userMessage));
        var body = PdfCandidateAuthorityQualificationAdapter.BuildProviderBodyReasoningEnabled(requestModel, baseline.MaxCompletionTokens);
        var groupsReordered = baseline.OccurrenceGroups.Select(group => new Group(group.PrimaryOccurrence, group.PrimaryAlias,
            reverse ? group.CandidateIds.Reverse().ToArray() : group.CandidateIds)).ToArray();
        return new PreparedRequest(baseline.DocumentId, baseline.SystemPrompt, userMessage, requestModel.UserMessageSha256,
            requestModel.UserMessageUtf8Bytes, body.PayloadBytes, body.Bytes, body.Hash, groupsReordered, baseline.MaxCompletionTokens);
    }

    private static PreparedRequest ComposeExactExtentWithHashPermutation(PreparedDocument prepared, PreparedRequest baseline, string salt)
    {
        var orderedGroups = baseline.OccurrenceGroups.ToDictionary(group => group.PrimaryOccurrence,
            group => group.CandidateIds.OrderBy(id => Hashing.Sha256($"{group.PrimaryOccurrence}|{id}|{salt}"), StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var document = JsonNode.Parse(baseline.UserMessage)!.AsObject();
        foreach (var node in document["occurrenceGroups"]!.AsArray())
        {
            var primary = node!["primary"]!.GetValue<string>();
            var candidates = node["candidates"]!.AsArray();
            var byId = candidates.ToDictionary(item => item!["id"]!.GetValue<string>(), item => item, StringComparer.Ordinal);
            candidates.Clear();
            foreach (var id in orderedGroups[primary]) candidates.Add(byId[id]);
        }
        var userMessage = document.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        var requestModel = new V5FreeHeadingRequestV1("v5-function-conditioned-exact-extent-resolver-2", baseline.SystemPrompt,
            userMessage, Hashing.Sha256(userMessage), Encoding.UTF8.GetByteCount(baseline.SystemPrompt), Encoding.UTF8.GetByteCount(userMessage));
        var body = PdfCandidateAuthorityQualificationAdapter.BuildProviderBodyReasoningEnabled(requestModel, baseline.MaxCompletionTokens);
        var groupsReordered = baseline.OccurrenceGroups.Select(group => new Group(group.PrimaryOccurrence, group.PrimaryAlias,
            orderedGroups[group.PrimaryOccurrence])).ToArray();
        return new PreparedRequest(baseline.DocumentId, baseline.SystemPrompt, userMessage, requestModel.UserMessageSha256,
            requestModel.UserMessageUtf8Bytes, body.PayloadBytes, body.Bytes, body.Hash, groupsReordered, baseline.MaxCompletionTokens);
    }

    private static PreparedRequest ComposeExactExtentWithWholeMoved(PreparedDocument prepared, PreparedRequest baseline)
    {
        var orderedGroups = baseline.OccurrenceGroups.ToDictionary(group => group.PrimaryOccurrence, group =>
        {
            var whole = group.CandidateIds.Single(id => prepared.SourcePack.Universe.Candidates.Single(candidate => candidate.Id == id).Kind.ToString() == "WHOLE");
            return group.CandidateIds.Where(id => id != whole).Append(whole).ToArray();
        }, StringComparer.Ordinal);
        var document = JsonNode.Parse(baseline.UserMessage)!.AsObject();
        foreach (var node in document["occurrenceGroups"]!.AsArray())
        {
            var primary = node!["primary"]!.GetValue<string>();
            var candidates = node["candidates"]!.AsArray();
            var byId = candidates.ToDictionary(item => item!["id"]!.GetValue<string>(), item => item, StringComparer.Ordinal);
            candidates.Clear();
            foreach (var id in orderedGroups[primary]) candidates.Add(byId[id]);
        }
        var userMessage = document.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        var requestModel = new V5FreeHeadingRequestV1("v5-function-conditioned-exact-extent-resolver-2", baseline.SystemPrompt,
            userMessage, Hashing.Sha256(userMessage), Encoding.UTF8.GetByteCount(baseline.SystemPrompt), Encoding.UTF8.GetByteCount(userMessage));
        var body = PdfCandidateAuthorityQualificationAdapter.BuildProviderBodyReasoningEnabled(requestModel, baseline.MaxCompletionTokens);
        var groupsReordered = baseline.OccurrenceGroups.Select(group => new Group(group.PrimaryOccurrence, group.PrimaryAlias,
            orderedGroups[group.PrimaryOccurrence])).ToArray();
        return new PreparedRequest(baseline.DocumentId, baseline.SystemPrompt, userMessage, requestModel.UserMessageSha256,
            requestModel.UserMessageUtf8Bytes, body.PayloadBytes, body.Bytes, body.Hash, groupsReordered, baseline.MaxCompletionTokens);
    }

    [Fact]
    public async Task Run_exactly_two_frozen_primary_calls_only_when_explicitly_enabled()
    {
        if (Environment.GetEnvironmentVariable(RunVariable) is not ("1" or "true" or "TRUE")) return;

        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        Assert.False(string.IsNullOrWhiteSpace(apiKey), "OPENROUTER_API_KEY is required for the explicitly enabled P6T-G2 canary.");
        var capturePath = TestRepository.Path(CaptureRoot);
        Assert.False(Directory.Exists(capturePath), "P6T-G2 capture root already exists; automatic resume or resend is forbidden.");

        using var f1Primary = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{F1Root}/result.v1.json")));
        using var f1Retry = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{F1Root}/retry-src089-result.v1.json")));
        using var g1 = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(G1Path)));
        var p089 = Prepare("SRC-089", SourcePdfCorpus.Src089, f1Retry.RootElement.GetProperty("row"), g1.RootElement.GetProperty("src089"));
        var p095 = Prepare("SRC-095", SourcePdfCorpus.Src095,
            f1Primary.RootElement.GetProperty("rows").EnumerateArray().Single(row => row.GetProperty("documentId").GetString() == "SRC-095"),
            g1.RootElement.GetProperty("src095"));

        // P6T-G2 preflight excluded reviewed TOC identities, but F1 function membership alone also
        // excludes them. Rebuild from source/function captures only and require byte parity with the
        // already-frozen request hashes before opening the network gate.
        var requests = new[] { Compose(p089, new HashSet<string>(StringComparer.Ordinal)), Compose(p095, new HashSet<string>(StringComparer.Ordinal)) };
        using var preflight = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{OutputRoot}/exact-extent-resolver-preflight.v1.json")));
        Assert.Equal("PREPARED_NOT_AUTHORIZED", preflight.RootElement.GetProperty("status").GetString());
        var frozenRows = preflight.RootElement.GetProperty("callPlan").EnumerateArray().ToArray();
        Assert.Equal(2, frozenRows.Length);
        Assert.Collection(requests,
            request => AssertFrozenParity(request, frozenRows[0]),
            request => AssertFrozenParity(request, frozenRows[1]));

        Directory.CreateDirectory(capturePath);
        WriteNew(Path.Combine(capturePath, "execution-reservation.v1.json"), new
        {
            schemaVersion = "v5-p6tg2-execution-reservation-v1",
            status = "CAPTURE_SLOTS_RESERVED",
            requestedCalls = 2,
            providerCallsBeforeSend = 0,
            requests = requests.Select(request => new { request.DocumentId, request.ProviderHash, request.ProviderBytes }).ToArray(),
            retriesAllowed = 0,
            goldRead = false,
        });

        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var provider = new OpenRouterQualificationTransport(http, new RemoteInferenceOptions
        {
            ApiKey = apiKey!,
            Model = "qwen/qwen3.7-flash",
            OpenRouterProviderRoute = "Alibaba",
            TransientRequestRetries = 0,
            MaxParallelRequests = 1,
            ProviderTransportTimeoutSeconds = 300,
            RequireZeroDataRetention = false,
        });

        var rows = new List<object>(2);
        foreach (var request in requests)
        {
            var slotPath = Path.Combine(capturePath, $"{request.DocumentId}.reservation.v1.json");
            WriteNew(slotPath, new { schemaVersion = "v5-p6tg2-call-reservation-v1", documentId = request.DocumentId,
                providerRequestHash = request.ProviderHash, status = "ONE_PRIMARY_ATTEMPT_RESERVED" });
            OpenRouterExecutionObservation observation;
            try
            {
                observation = await provider.ExecuteObservedAsync(request.ProviderBody, request.MaxCompletionTokens,
                    request.SystemPrompt, request.UserMessage, CancellationToken.None);
            }
            catch (Exception error)
            {
                // A failure is frozen and is never retried. Continue to the second reserved primary call.
                WriteNew(Path.Combine(capturePath, $"{request.DocumentId}.transport-failure.v1.json"), new
                {
                    schemaVersion = "v5-p6tg2-transport-failure-v1",
                    documentId = request.DocumentId,
                    providerRequestHash = request.ProviderHash,
                    errorType = error.GetType().FullName,
                    message = error.Message,
                    retryCount = 0,
                });
                rows.Add(new { documentId = request.DocumentId, transportStatus = "FAILED", errorType = error.GetType().FullName, retryCount = 0 });
                continue;
            }

            Assert.Equal(0, observation.RetryCount);
            var rawRow = new
            {
                schemaVersion = "v5-p6tg2-raw-provider-capture-v1",
                documentId = request.DocumentId,
                packId = "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_001",
                provider = "OpenRouter",
                model = "qwen/qwen3.7-flash",
                providerRoute = "Alibaba",
                reasoningRequested = true,
                reasoningEffort = "OMITTED",
                providerRequestHash = request.ProviderHash,
                providerRequestBytes = request.ProviderBytes,
                semanticRequestHash = request.MessageHash,
                finishReason = observation.FinishReason,
                usage = observation.Usage,
                promptTokens = UsageInt(observation.Usage, "prompt_tokens"),
                completionTokens = UsageInt(observation.Usage, "completion_tokens"),
                reasoningTokens = UsageInt(observation.Usage, "reasoning_tokens"),
                sseEventCount = observation.SseEventCount,
                retryCount = observation.RetryCount,
                rawSseSha256 = Hashing.Sha256(observation.RawSse),
                rawResponseSha256 = Hashing.Sha256(observation.Content),
                rawSseUtf8Bytes = Encoding.UTF8.GetByteCount(observation.RawSse),
                rawResponseUtf8Bytes = Encoding.UTF8.GetByteCount(observation.Content),
                rawSse = observation.RawSse,
                rawResponse = observation.Content,
            };
            // Durable raw capture precedes any JSON, contract, or source/Gold audit.
            WriteNew(Path.Combine(capturePath, $"{request.DocumentId}.raw-capture.v1.json"), rawRow);
            try
            {
                var parsed = ParseLedger(request, observation.Content);
                rows.Add(new
                {
                    documentId = request.DocumentId,
                    transportStatus = "COMPLETED",
                    finishReason = observation.FinishReason,
                    parserStatus = "PARSED",
                    rawResponseSha256 = Hashing.Sha256(observation.Content),
                    promptTokens = UsageInt(observation.Usage, "prompt_tokens"),
                    completionTokens = UsageInt(observation.Usage, "completion_tokens"),
                    reasoningTokens = UsageInt(observation.Usage, "reasoning_tokens"),
                    retryCount = observation.RetryCount,
                    parsed,
                });
            }
            catch (Exception error)
            {
                rows.Add(new
                {
                    documentId = request.DocumentId,
                    transportStatus = "COMPLETED",
                    finishReason = observation.FinishReason,
                    parserStatus = "REJECTED",
                    parserError = error.GetType().Name + ": " + error.Message,
                    rawResponseSha256 = Hashing.Sha256(observation.Content),
                    retryCount = observation.RetryCount,
                });
            }
        }

        Assert.Equal(2, rows.Count);
        var rawPaths = Directory.GetFiles(capturePath, "*.raw-capture.v1.json", SearchOption.TopDirectoryOnly);
        var rawHashes = rawPaths.OrderBy(path => path, StringComparer.Ordinal).Select(path => Hashing.Sha256(File.ReadAllText(path))).ToArray();
        WriteNew(Path.Combine(capturePath, "result.v1.json"), new
        {
            schemaVersion = "v5-p6tg2-two-primary-call-result-v1",
            status = "EXECUTION_SET_FROZEN",
            providerCalls = 2,
            retries = 0,
            repairs = 0,
            fallbacks = 0,
            goldRead = false,
            goldMutation = "NONE",
            runtimeChanged = false,
            rawCaptureFileCount = rawPaths.Length,
            rawCaptureSetSha256 = Hashing.Sha256(string.Join("\n", rawHashes)),
            rows,
        });
        Assert.Equal(2, rawPaths.Length);
    }

    [Fact]
    public void P6TG2_frozen_canary_is_audited_against_gold_and_source_review_without_provider_calls()
    {
        var captureRoot = TestRepository.Path(CaptureRoot);
        using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(captureRoot, "result.v1.json")));
        var resultRoot = result.RootElement;
        Assert.Equal("EXECUTION_SET_FROZEN", resultRoot.GetProperty("status").GetString());
        Assert.Equal(2, resultRoot.GetProperty("providerCalls").GetInt32());
        Assert.Equal(0, resultRoot.GetProperty("retries").GetInt32());
        Assert.Equal(0, resultRoot.GetProperty("repairs").GetInt32());
        Assert.Equal(0, resultRoot.GetProperty("fallbacks").GetInt32());
        Assert.False(resultRoot.GetProperty("goldRead").GetBoolean());
        Assert.Equal("NONE", resultRoot.GetProperty("goldMutation").GetString());

        var captureFiles = Directory.GetFiles(captureRoot, "*.raw-capture.v1.json", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        Assert.Equal(2, captureFiles.Length);
        var captureFileHashes = captureFiles.Select(path => Hashing.Sha256(File.ReadAllText(path))).ToArray();
        Assert.Equal(resultRoot.GetProperty("rawCaptureSetSha256").GetString(), Hashing.Sha256(string.Join("\n", captureFileHashes)));

        using var f1Primary = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{F1Root}/result.v1.json")));
        using var f1Retry = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{F1Root}/retry-src089-result.v1.json")));
        using var g1 = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(G1Path)));
        using var gold089 = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(Gold089Path)));
        using var gold095 = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(Gold095Path)));
        using var sourceReview095 = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(Review095Path)));
        var p089 = Prepare("SRC-089", SourcePdfCorpus.Src089, f1Retry.RootElement.GetProperty("row"), g1.RootElement.GetProperty("src089"));
        var p095 = Prepare("SRC-095", SourcePdfCorpus.Src095,
            f1Primary.RootElement.GetProperty("rows").EnumerateArray().Single(row => row.GetProperty("documentId").GetString() == "SRC-095"),
            g1.RootElement.GetProperty("src095"));

        var captureRows = captureFiles.Select(path => JsonDocument.Parse(File.ReadAllText(path))).ToArray();
        try
        {
            var rowsByDocument = captureRows.ToDictionary(doc => doc.RootElement.GetProperty("documentId").GetString()!, doc => doc.RootElement, StringComparer.Ordinal);
            var request089 = Compose(p089, new HashSet<string>(StringComparer.Ordinal));
            var request095 = Compose(p095, new HashSet<string>(StringComparer.Ordinal));
            var ledgers = new Dictionary<string, ParsedLedger>(StringComparer.Ordinal);
            foreach (var (documentId, request) in new[] { ("SRC-089", request089), ("SRC-095", request095) })
            {
                var capture = rowsByDocument[documentId];
                Assert.Equal(request.ProviderHash, capture.GetProperty("providerRequestHash").GetString());
                Assert.Equal(request.MessageHash, capture.GetProperty("semanticRequestHash").GetString());
                Assert.Equal("stop", capture.GetProperty("finishReason").GetString());
                Assert.Equal(0, capture.GetProperty("retryCount").GetInt32());
                var raw = capture.GetProperty("rawResponse").GetString()!;
                Assert.Equal(capture.GetProperty("rawResponseSha256").GetString(), Hashing.Sha256(raw));
                ledgers.Add(documentId, ParseLedger(request, raw));
            }

            var gold089Identities = GoldIdentities(gold089.RootElement);
            var gold095Identities = GoldIdentities(gold095.RootElement);
            var selected089 = SelectedCandidates(request089, ledgers["SRC-089"], p089.SourcePack);
            var selected095 = SelectedCandidates(request095, ledgers["SRC-095"], p095.SourcePack);

            var sixReviewedUnits = new[]
            {
                new { aliases = new[] { "L0006:S0", "L0007:S0", "L0008:S0" }, primary = "L0006:S0" },
                new { aliases = new[] { "L0014:S0", "L0015:S0" }, primary = "L0014:S0" },
                new { aliases = new[] { "L0016:S0" }, primary = "L0016:S0" },
                new { aliases = new[] { "L0024:S0", "L0025:S0" }, primary = "L0024:S0" },
                new { aliases = new[] { "L0051:S0", "L0052:S0" }, primary = "L0051:S0" },
                new { aliases = new[] { "L0080:S0", "L0081:S0" }, primary = "L0080:S0" },
            };
            var positives089 = sixReviewedUnits.Select(unit =>
            {
                var candidate = selected089.Single(item => item.PrimaryAlias == unit.primary);
                var expected = gold089Identities.Single(identity => identity.StartsWith(unit.primary + ":", StringComparison.Ordinal) &&
                    unit.aliases.All(alias => identity.Contains(alias + ":", StringComparison.Ordinal)));
                var extentClass = candidate.Outcome == "NO_STRUCTURAL_EXTENT" ? "NO_STRUCTURAL_EXTENT"
                    : candidate.Identity == expected ? "EXACT"
                    : candidate.Aliases.Count < unit.aliases.Length && candidate.Aliases.All(unit.aliases.Contains) ? "UNDEREXTENT_PRIMARY_SUBSET"
                    : "OTHER_IDENTITY_MISMATCH";
                return new
                {
                    primaryAlias = unit.primary,
                    expectedGoldIdentity = expected,
                    selectedCandidateId = candidate.CandidateId,
                    selectedIdentity = candidate.Identity,
                    exact = candidate.Outcome == "SELECTED_CANDIDATE" && candidate.Identity == expected,
                    extentClass,
                    selectedPartsAreGoldParts = candidate.Outcome == "SELECTED_CANDIDATE" && candidate.Aliases.All(unit.aliases.Contains),
                    decision = candidate.Outcome,
                };
            }).ToArray();
            var continuationAliases = new[] { "L0007:S0", "L0008:S0", "L0015:S0", "L0025:S0", "L0052:S0", "L0081:S0" };
            var continuationNegatives089 = continuationAliases.Select(alias =>
            {
                var candidate = selected089.Single(item => item.PrimaryAlias == alias);
                return new
                {
                    primaryAlias = alias,
                    selectedCandidateId = candidate.CandidateId,
                    selectedIdentity = candidate.Identity,
                    exactGoldMatch = candidate.Outcome == "SELECTED_CANDIDATE" && candidate.Identity is not null && gold089Identities.Contains(candidate.Identity),
                    decision = candidate.Outcome,
                    expectedNegativeControl = "NO_INDEPENDENT_STRUCTURAL_EXTENT_AT_CONTINUATION_ATOM",
                };
            }).ToArray();
            var frontMatterAliases = new[] { "L0002:S0", "L0002:S1", "L0003:S1" };
            var frontMatterNegatives089 = frontMatterAliases.Select(alias =>
            {
                var candidate = selected089.Single(item => item.PrimaryAlias == alias);
                return new
                {
                    primaryAlias = alias,
                    selectedCandidateId = candidate.CandidateId,
                    selectedIdentity = candidate.Identity,
                    exactGoldMatch = candidate.Outcome == "SELECTED_CANDIDATE" && candidate.Identity is not null && gold089Identities.Contains(candidate.Identity),
                    decision = candidate.Outcome,
                    expectedNegativeControl = "SOURCE_REVIEWED_NON_HEADING_FRONT_MATTER",
                };
            }).ToArray();

            var headingReview095 = sourceReview095.RootElement.GetProperty("items").EnumerateArray()
                .Where(item => item.GetProperty("verdict").GetString() == "HEADING")
                .ToDictionary(item => item.GetProperty("parts")[0].GetProperty("sourceAlias").GetString()!, StringComparer.Ordinal);
            var fourEstablishes095 = selected095.Select(candidate =>
            {
                var review = headingReview095[candidate.PrimaryAlias];
                return new
                {
                    primaryAlias = candidate.PrimaryAlias,
                    reviewedVerdict = review.GetProperty("verdict").GetString(),
                    reviewedPattern = review.GetProperty("pattern").GetString(),
                    selectedCandidateId = candidate.CandidateId,
                    selectedIdentity = candidate.Identity,
                    exactGoldMatch = candidate.Outcome == "SELECTED_CANDIDATE" && candidate.Identity is not null && gold095Identities.Contains(candidate.Identity),
                    decision = candidate.Outcome,
                };
            }).ToArray();
            Assert.Equal(4, fourEstablishes095.Length);
            Assert.All(fourEstablishes095, row => Assert.Equal("HEADING", row.reviewedVerdict));
            Assert.Equal(1, positives089.Count(row => row.exact));
            Assert.Equal(5, positives089.Count(row => row.extentClass == "UNDEREXTENT_PRIMARY_SUBSET"));
            Assert.Equal(6, continuationNegatives089.Length);
            Assert.Equal(6, continuationNegatives089.Count(row => row.decision == "SELECTED_CANDIDATE"));
            Assert.Equal(3, frontMatterNegatives089.Length);
            Assert.Equal(3, frontMatterNegatives089.Count(row => row.decision == "SELECTED_CANDIDATE"));
            Assert.Equal(4, fourEstablishes095.Count(row => row.exactGoldMatch));

            FreezeArtifact.AssertJson(GoldAuditRoot, "function-conditioned-extent-canary-audit.v1.json", new
            {
                schemaVersion = "v5-p6tg2-gold-source-review-audit-v1",
                authority = new
                {
                    captureSetSha256 = resultRoot.GetProperty("rawCaptureSetSha256").GetString(),
                    rawResponseSha256 = rowsByDocument.ToDictionary(pair => pair.Key,
                        pair => pair.Value.GetProperty("rawResponseSha256").GetString(), StringComparer.Ordinal),
                    resultArtifactSha256 = Hashing.Sha256(File.ReadAllText(TestRepository.Path($"{CaptureRoot}/result.v1.json"))),
                    gold089Sha256 = Hashing.Sha256(File.ReadAllText(TestRepository.Path(Gold089Path))),
                    gold095Sha256 = Hashing.Sha256(File.ReadAllText(TestRepository.Path(Gold095Path))),
                    src095SourceReviewItemsSha256 = Hashing.Sha256(File.ReadAllText(TestRepository.Path(Review095Path))),
                    goldMutation = "NONE",
                    providerCallsDuringAudit = 0,
                    rawCaptureMutation = "NONE",
                },
                execution = resultRoot.GetProperty("rows").EnumerateArray().Select(row => new
                {
                    documentId = row.GetProperty("documentId").GetString(),
                    finishReason = row.GetProperty("finishReason").GetString(),
                    retryCount = row.GetProperty("retryCount").GetInt32(),
                    rawDecisions = row.GetProperty("parsed").GetProperty("RawDecisions").GetInt32(),
                    acceptedSelections = row.GetProperty("parsed").GetProperty("AcceptedSelections").GetInt32(),
                    noStructuralExtent = row.GetProperty("parsed").GetProperty("NoStructuralExtent").GetInt32(),
                    quarantined = row.GetProperty("parsed").GetProperty("Quarantined").GetInt32(),
                    reasoningTokens = row.GetProperty("reasoningTokens").GetInt32(),
                }).ToArray(),
                src089 = new
                {
                    positiveGoldUnits = positives089,
                    positiveExactCount = positives089.Count(row => row.exact),
                    positiveUnderextentCount = positives089.Count(row => row.extentClass == "UNDEREXTENT_PRIMARY_SUBSET"),
                    continuationNegativeControls = continuationNegatives089,
                    continuationNegativeSelectedCount = continuationNegatives089.Count(row => row.decision == "SELECTED_CANDIDATE"),
                    frontMatterNegativeControls = frontMatterNegatives089,
                    frontMatterNegativeSelectedCount = frontMatterNegatives089.Count(row => row.decision == "SELECTED_CANDIDATE"),
                },
                src095 = new
                {
                    sourceReviewedEstablishes = fourEstablishes095,
                    exactGoldCount = fourEstablishes095.Count(row => row.exactGoldMatch),
                },
                conclusion = "CANARY_CONTRACT_AND_CANDIDATE_SELECTION_AUDITED; G2 DID NOT ABSTAIN ON ANY ISSUED PRIMARY; EXACTNESS_REQUIRES_SELECTED_CANDIDATE_TO_MATCH_GOLD_IDENTITY; THIS IS NOT FULL-PACK OR PRODUCTION QUALIFICATION",
            });
        }
        finally
        {
            foreach (var capture in captureRows) capture.Dispose();
        }
    }

    [Fact]
    public void P6TG2_audit_preserves_no_structural_extent_as_a_valid_discriminated_outcome()
    {
        using var f1Retry = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{F1Root}/retry-src089-result.v1.json")));
        using var g1 = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(G1Path)));
        var prepared = Prepare("SRC-089", SourcePdfCorpus.Src089, f1Retry.RootElement.GetProperty("row"), g1.RootElement.GetProperty("src089"));
        var request = Compose(prepared, new HashSet<string>(StringComparer.Ordinal));
        var raw = JsonSerializer.Serialize(new
        {
            decisions = request.OccurrenceGroups.Select(group => new
            {
                primary = group.PrimaryOccurrence,
                candidate = "NO_STRUCTURAL_EXTENT",
            }).ToArray(),
        });

        var ledger = ParseLedger(request, raw);
        var outcomes = SelectedCandidates(request, ledger, prepared.SourcePack);
        Assert.Equal(request.OccurrenceGroups.Count, outcomes.Count);
        Assert.Equal(request.OccurrenceGroups.Count, ledger.NoStructuralExtent);
        Assert.All(outcomes, outcome =>
        {
            Assert.Equal("NO_STRUCTURAL_EXTENT", outcome.Outcome);
            Assert.Null(outcome.CandidateId);
            Assert.Null(outcome.Identity);
            Assert.Empty(outcome.Aliases);
        });
    }

    private sealed record SelectedCandidate(string PrimaryOccurrence, string PrimaryAlias, string Outcome, string? CandidateId,
        string? Identity, IReadOnlyList<string> Aliases);

    private static IReadOnlyList<SelectedCandidate> SelectedCandidates(PreparedRequest request, ParsedLedger ledger,
        PdfCandidateAuthorityPreparedPack pack)
    {
        var decisions = ledger.Decisions.Select(item => JsonDocument.Parse(JsonSerializer.Serialize(item))).ToArray();
        try
        {
            var accepted = decisions.Select(doc => doc.RootElement).ToArray();
            var byPrimary = request.OccurrenceGroups.ToDictionary(group => group.PrimaryOccurrence, StringComparer.Ordinal);
            return accepted.Select(item =>
            {
                var primary = item.GetProperty("primary").GetString()!;
                var candidateId = item.GetProperty("candidate").GetString()!;
                if (candidateId == "NO_STRUCTURAL_EXTENT")
                    return new SelectedCandidate(primary, byPrimary[primary].PrimaryAlias, "NO_STRUCTURAL_EXTENT", null, null, []);
                var candidate = pack.Universe.Candidates.Single(value => value.Id == candidateId);
                return new SelectedCandidate(primary, byPrimary[primary].PrimaryAlias, "SELECTED_CANDIDATE", candidateId, candidate.SpanIdentity,
                    candidate.Endpoint.Parts.Select(part => part.Alias).ToArray());
            }).ToArray();
        }
        finally
        {
            foreach (var decision in decisions) decision.Dispose();
        }
    }

    private static HashSet<string> GoldIdentities(JsonElement root) => root.GetProperty("occurrence").GetProperty("claims")
        .EnumerateArray().Select(claim => claim.GetProperty("identity").GetString()!).ToHashSet(StringComparer.Ordinal);

    private static PreparedRequest Compose(PreparedDocument prepared, IReadOnlySet<string> forbiddenContextAliases)
    {
        var atoms = prepared.Plan.SourceAtoms.ToDictionary(atom => atom.Alias, StringComparer.Ordinal);
        var ownedOrder = prepared.SourcePack.OwnedAliases;
        var candidatesByPrimary = prepared.SourcePack.Universe.Candidates
            .Where(candidate => prepared.CandidateClasses[candidate.Id] == ProjectionClass.ALL_ESTABLISHES)
            .GroupBy(candidate => candidate.Endpoint.Parts[0].Alias, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(candidate => int.Parse(candidate.Id[1..])).ToArray(), StringComparer.Ordinal);
        var roots = prepared.FunctionByAlias.Where(item => item.Value == "ESTABLISHES_STRUCTURE")
            .Select(item => item.Key).OrderBy(alias => atoms[alias].Ordinal).ThenBy(alias => alias, StringComparer.Ordinal).ToArray();
        var groups = roots.Select(alias =>
        {
            Assert.True(candidatesByPrimary.TryGetValue(alias, out var candidates) && candidates.Length > 0,
                $"establishes-primary-has-no-eligible-candidate:{prepared.DocumentId}:{alias}");
            return new Group(prepared.OccurrenceByAlias[alias], alias, candidates!.Select(candidate => candidate.Id).ToArray());
        }).ToArray();

        var tocAliasSet = forbiddenContextAliases;
        var rows = groups.Select(group =>
        {
            var rootIndex = Array.IndexOf(ownedOrder.ToArray(), group.PrimaryAlias);
            Assert.True(rootIndex >= 0, $"establishes-primary-not-owned:{group.PrimaryAlias}");
            var context = new List<object>();
            if (rootIndex > 0) AddContext(rootIndex - 1, "PREVIOUS");
            if (rootIndex + 1 < ownedOrder.Count) AddContext(rootIndex + 1, "NEXT");

            void AddContext(int index, string direction)
            {
                var alias = ownedOrder[index];
                var function = prepared.FunctionByAlias[alias];
                if (function == "REPRESENTS_STRUCTURE" || tocAliasSet.Contains(alias)) return;
                var atom = atoms[alias];
                context.Add(new { direction, page = atom.Page, text = atom.Text, function, selectable = false });
            }

            var options = group.CandidateIds.Select(id =>
            {
                var candidate = prepared.SourcePack.Universe.Candidates.Single(value => value.Id == id);
                Assert.All(candidate.Endpoint.Parts, part => Assert.Equal("ESTABLISHES_STRUCTURE", prepared.FunctionByAlias[part.Alias]));
                return new
                {
                    id = candidate.Id,
                    kind = candidate.Kind.ToString(),
                    text = candidate.Text,
                    parts = candidate.Endpoint.Parts.Select(part =>
                    {
                        var atom = atoms[part.Alias];
                        return new
                        {
                            occurrence = prepared.OccurrenceByAlias[part.Alias],
                            function = prepared.FunctionByAlias[part.Alias],
                            text = atom.Text.Substring(part.Start, part.End - part.Start),
                        };
                    }).ToArray(),
                };
            }).ToArray();
            return new
            {
                primary = group.PrimaryOccurrence,
                function = "ESTABLISHES_STRUCTURE",
                primaryText = atoms[group.PrimaryAlias].Text,
                candidates = options,
                context = context.ToArray(),
            };
        }).ToArray();

        var systemPrompt = $$"""
            Resolve only the exact source extent for each issued primary occurrence. The input contains a local menu of harness-issued candidate extents whose every source part is already classified ESTABLISHES_STRUCTURE. This function label is read-only evidence, not proof that a valid heading extent exists.

            For every supplied primary O#, return exactly one decision. Choose exactly one C# from that primary's own candidate menu, or return the exact sentinel NO_STRUCTURAL_EXTENT if none of the candidates is a valid structural heading extent. NO_STRUCTURAL_EXTENT is an explicit valid decision and must not be converted to OTHER or omitted.

            Return exactly one JSON object with this shape: {"decisions":[{"primary":"O27","candidate":"C123"},{"primary":"O28","candidate":"NO_STRUCTURAL_EXTENT"}]}. Each decision has exactly primary and candidate. Candidate IDs and source-part text are supplied by the harness; do not create, edit, join, trim, or retype extents. Do not output source text, coordinates, aliases, relations, hierarchy, rationale, confidence, or any extra property. Context items are read-only and never selectable. Do not use an external answer key.
            """;
        var userMessage = JsonSerializer.Serialize(new
        {
            protocolVersion = Protocol,
            occurrenceGroups = rows,
        });
        var request = new V5FreeHeadingRequestV1(Protocol, systemPrompt, userMessage, Hashing.Sha256(userMessage),
            Encoding.UTF8.GetByteCount(systemPrompt), Encoding.UTF8.GetByteCount(userMessage));
        var providerBody = PdfCandidateAuthorityQualificationAdapter.BuildProviderBodyReasoningEnabled(request,
            prepared.SourcePack.MaxCompletionTokens);
        return new PreparedRequest(prepared.DocumentId, systemPrompt, userMessage, request.UserMessageSha256,
            request.UserMessageUtf8Bytes, providerBody.PayloadBytes, providerBody.Bytes, providerBody.Hash, groups,
            prepared.SourcePack.MaxCompletionTokens);
    }

    private static AnchorExistenceRequest ComposeAnchorExistence(PreparedDocument prepared)
    {
        var atoms = prepared.Plan.SourceAtoms.ToDictionary(atom => atom.Alias, StringComparer.Ordinal);
        var owned = prepared.SourcePack.OwnedAliases;
        var primaries = prepared.FunctionByAlias
            .Where(item => item.Value == "ESTABLISHES_STRUCTURE")
            .OrderBy(item => atoms[item.Key].Ordinal).ThenBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => (Occurrence: prepared.OccurrenceByAlias[item.Key], Alias: item.Key)).ToArray();
        var rows = primaries.Select(primary =>
        {
            var atom = atoms[primary.Alias];
            var index = Array.IndexOf(owned.ToArray(), primary.Alias);
            Assert.True(index >= 0, $"anchor-existence-primary-not-owned:{primary.Alias}");
            object? PreviousOrNext(int candidateIndex)
            {
                if (candidateIndex < 0 || candidateIndex >= owned.Count) return null;
                var neighbor = atoms[owned[candidateIndex]];
                return new { occurrence = prepared.OccurrenceByAlias[neighbor.Alias], page = neighbor.Page, text = neighbor.Text, selectable = false };
            }
            return new
            {
                primary = primary.Occurrence,
                page = atom.Page,
                text = atom.Text,
                upstreamFunction = "ESTABLISHES_STRUCTURE",
                previous = PreviousOrNext(index - 1),
                next = PreviousOrNext(index + 1),
            };
        }).ToArray();
        var systemPrompt = """
            Decide anchor existence only. Each issued primary occurrence has an upstream ESTABLISHES_STRUCTURE eligibility signal, but that signal is not proof that a valid local structural heading extent begins at this primary.

            For every issued O#, return exactly one anchor: HAS_STRUCTURAL_EXTENT if at least one valid local structural heading extent begins at that primary; otherwise NO_STRUCTURAL_EXTENT. Do not choose or describe any extent. Do not infer an answer from context-only items.

            Return exactly one JSON object with this shape: {"decisions":[{"primary":"O27","anchor":"HAS_STRUCTURAL_EXTENT"},{"primary":"O28","anchor":"NO_STRUCTURAL_EXTENT"}]}. Each decision has exactly primary and anchor. Do not output source text, candidate IDs, coordinates, aliases, locators, relations, hierarchy, rationale, confidence, or extra properties.
            """;
        var userMessage = JsonSerializer.Serialize(new
        {
            protocolVersion = "v5-function-conditioned-anchor-existence-1",
            occurrences = rows,
        });
        var headingRequest = new V5FreeHeadingRequestV1("v5-function-conditioned-anchor-existence-1", systemPrompt, userMessage,
            Hashing.Sha256(userMessage), Encoding.UTF8.GetByteCount(systemPrompt), Encoding.UTF8.GetByteCount(userMessage));
        var body = PdfCandidateAuthorityQualificationAdapter.BuildProviderBodyReasoningEnabled(headingRequest,
            prepared.SourcePack.MaxCompletionTokens);
        return new AnchorExistenceRequest(prepared.DocumentId, systemPrompt, userMessage, headingRequest.UserMessageSha256,
            headingRequest.UserMessageUtf8Bytes, body.PayloadBytes, body.Bytes, body.Hash, primaries, prepared.SourcePack.MaxCompletionTokens);
    }

    private static ParsedAnchorLedger ParseAnchorLedger(AnchorExistenceRequest request, string raw)
    {
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 ||
            !root.TryGetProperty("decisions", out var decisions) || decisions.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("g2a-response-root-invalid");
        var issued = request.Primaries.ToDictionary(item => item.Occurrence, StringComparer.Ordinal);
        var perPrimary = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var accepted = new List<object>();
        var refused = new List<object>();
        foreach (var item in decisions.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || item.EnumerateObject().Count() != 2 ||
                !item.TryGetProperty("primary", out var primaryElement) || primaryElement.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("anchor", out var anchorElement) || anchorElement.ValueKind != JsonValueKind.String)
            {
                refused.Add(new { reason = "decision-schema-invalid" });
                continue;
            }
            var primary = primaryElement.GetString()!;
            var anchor = anchorElement.GetString()!;
            if (!issued.ContainsKey(primary))
            {
                refused.Add(new { primary, anchor, reason = "primary-not-issued" });
                continue;
            }
            if (anchor is not ("HAS_STRUCTURAL_EXTENT" or "NO_STRUCTURAL_EXTENT"))
            {
                refused.Add(new { primary, anchor, reason = "anchor-invalid" });
                continue;
            }
            if (!perPrimary.TryGetValue(primary, out var values)) perPrimary.Add(primary, values = []);
            values.Add(anchor);
        }
        foreach (var primary in request.Primaries)
        {
            if (!perPrimary.TryGetValue(primary.Occurrence, out var values))
            {
                refused.Add(new { primary = primary.Occurrence, reason = "missing-primary-decision" });
                continue;
            }
            if (values.Count != 1)
            {
                refused.Add(new { primary = primary.Occurrence, count = values.Count, reason = "duplicate-primary-decision" });
                continue;
            }
            accepted.Add(new { primary = primary.Occurrence, anchor = values[0] });
        }
        var has = accepted.Count(item => JsonSerializer.Serialize(item).Contains("HAS_STRUCTURAL_EXTENT", StringComparison.Ordinal));
        var no = accepted.Count(item => JsonSerializer.Serialize(item).Contains("NO_STRUCTURAL_EXTENT", StringComparison.Ordinal));
        var missing = refused.Count(item => JsonSerializer.Serialize(item).Contains("missing-primary-decision", StringComparison.Ordinal));
        return new ParsedAnchorLedger(decisions.GetArrayLength(), has, no, refused.Count, missing, accepted, refused);
    }

    private static object Describe(PreparedRequest request) => new
    {
        documentId = request.DocumentId,
        packId = "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_001",
        status = "PREPARED_NOT_AUTHORIZED",
        primaryCount = request.OccurrenceGroups.Count,
        eligibleCandidateCount = request.OccurrenceGroups.Sum(group => group.CandidateIds.Count),
        optionsPerPrimary = request.OccurrenceGroups.Select(group => new
            { primary = group.PrimaryOccurrence, primaryAlias = group.PrimaryAlias, candidateCount = group.CandidateIds.Count, candidateIds = group.CandidateIds }).ToArray(),
        systemPromptSha256 = Hashing.Sha256(request.SystemPrompt),
        userMessageSha256 = request.MessageHash,
        userMessageUtf8Bytes = request.MessageBytes,
        providerBodySha256 = request.ProviderHash,
        providerBodyBytes = request.ProviderBytes,
    };

    private static void AssertFrozenParity(PreparedRequest request, JsonElement frozen)
    {
        Assert.Equal(request.DocumentId, frozen.GetProperty("documentId").GetString());
        Assert.Equal(request.MessageHash, frozen.GetProperty("userMessageSha256").GetString());
        Assert.Equal(request.MessageBytes, frozen.GetProperty("userMessageUtf8Bytes").GetInt32());
        Assert.Equal(request.ProviderHash, frozen.GetProperty("providerBodySha256").GetString());
        Assert.Equal(request.ProviderBytes, frozen.GetProperty("providerBodyBytes").GetInt32());
    }

    private static ParsedLedger ParseLedger(PreparedRequest request, string raw)
    {
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 || !root.TryGetProperty("decisions", out var decisions) || decisions.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("g2-response-root-invalid");
        var groups = request.OccurrenceGroups.ToDictionary(group => group.PrimaryOccurrence, group => group, StringComparer.Ordinal);
        var perPrimary = new Dictionary<string, List<(string? Candidate, string? Error)>>(StringComparer.Ordinal);
        var accepted = new List<object>(); var refused = new List<object>();
        foreach (var item in decisions.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || item.EnumerateObject().Count() != 2 ||
                !item.TryGetProperty("primary", out var primaryElement) || primaryElement.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("candidate", out var candidateElement) || candidateElement.ValueKind != JsonValueKind.String)
            {
                refused.Add(new { reason = "decision-schema-invalid" });
                continue;
            }
            var primary = primaryElement.GetString()!;
            var candidate = candidateElement.GetString()!;
            if (!groups.ContainsKey(primary))
            {
                refused.Add(new { primary, candidate, reason = "primary-not-issued" });
                continue;
            }
            if (!perPrimary.TryGetValue(primary, out var list)) perPrimary.Add(primary, list = []);
            list.Add((candidate, null));
        }

        foreach (var group in request.OccurrenceGroups)
        {
            if (!perPrimary.TryGetValue(group.PrimaryOccurrence, out var decisionsForPrimary))
            {
                refused.Add(new { primary = group.PrimaryOccurrence, reason = "missing-primary-decision" });
                continue;
            }
            if (decisionsForPrimary.Count != 1)
            {
                refused.Add(new { primary = group.PrimaryOccurrence, count = decisionsForPrimary.Count, reason = "duplicate-primary-decision" });
                continue;
            }
            var candidate = decisionsForPrimary[0].Candidate!;
            if (candidate == "NO_STRUCTURAL_EXTENT")
            {
                accepted.Add(new { primary = group.PrimaryOccurrence, candidate, outcome = "NO_STRUCTURAL_EXTENT" });
            }
            else if (group.CandidateIds.Contains(candidate, StringComparer.Ordinal))
            {
                accepted.Add(new { primary = group.PrimaryOccurrence, candidate, outcome = "SELECTED_CANDIDATE" });
            }
            else
            {
                refused.Add(new { primary = group.PrimaryOccurrence, candidate, reason = "candidate-not-issued-for-primary" });
            }
        }

        var noExtent = accepted.Count(item => JsonSerializer.Serialize(item).Contains("NO_STRUCTURAL_EXTENT", StringComparison.Ordinal));
        var missing = refused.Count(item => JsonSerializer.Serialize(item).Contains("missing-primary-decision", StringComparison.Ordinal));
        return new ParsedLedger(decisions.GetArrayLength(), accepted.Count - noExtent, noExtent, refused.Count, missing, accepted, refused);
    }

    private static ParsedLedger ParseExactExtentLedger(PreparedRequest request, string raw)
    {
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 ||
            !root.TryGetProperty("decisions", out var decisions) || decisions.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("g2b-response-root-invalid");

        var groups = request.OccurrenceGroups.ToDictionary(group => group.PrimaryOccurrence, group => group, StringComparer.Ordinal);
        var perPrimary = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var accepted = new List<object>();
        var refused = new List<object>();
        foreach (var item in decisions.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || item.EnumerateObject().Count() != 2 ||
                !item.TryGetProperty("primary", out var primaryElement) || primaryElement.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("candidate", out var candidateElement) || candidateElement.ValueKind != JsonValueKind.String)
            {
                refused.Add(new { reason = "decision-schema-invalid" });
                continue;
            }

            var primary = primaryElement.GetString()!;
            var candidate = candidateElement.GetString()!;
            if (!groups.ContainsKey(primary))
            {
                refused.Add(new { primary, candidate, reason = "primary-not-issued" });
                continue;
            }
            if (candidate == "NO_STRUCTURAL_EXTENT")
            {
                refused.Add(new { primary, candidate, reason = "sentinel-not-allowed" });
                continue;
            }
            if (!groups[primary].CandidateIds.Contains(candidate, StringComparer.Ordinal))
            {
                refused.Add(new { primary, candidate, reason = "candidate-not-issued-for-primary" });
                continue;
            }
            if (!perPrimary.TryGetValue(primary, out var values)) perPrimary.Add(primary, values = []);
            values.Add(candidate);
        }

        foreach (var group in request.OccurrenceGroups)
        {
            if (!perPrimary.TryGetValue(group.PrimaryOccurrence, out var values))
            {
                refused.Add(new { primary = group.PrimaryOccurrence, reason = "missing-primary-decision" });
                continue;
            }
            if (values.Count != 1)
            {
                refused.Add(new { primary = group.PrimaryOccurrence, count = values.Count, reason = "duplicate-primary-decision" });
                continue;
            }
            accepted.Add(new { primary = group.PrimaryOccurrence, candidate = values[0], outcome = "SELECTED_CANDIDATE" });
        }

        var missing = refused.Count(item => JsonSerializer.Serialize(item).Contains("missing-primary-decision", StringComparison.Ordinal));
        return new ParsedLedger(decisions.GetArrayLength(), accepted.Count, 0, refused.Count, missing, accepted, refused);
    }

    private static int? UsageInt(JsonElement? usage, string key)
    {
        if (usage is not { ValueKind: JsonValueKind.Object } value) return null;
        if (value.TryGetProperty(key, out var direct) && direct.ValueKind == JsonValueKind.Number) return direct.GetInt32();
        if (key == "reasoning_tokens" && value.TryGetProperty("completion_tokens_details", out var details) &&
            details.ValueKind == JsonValueKind.Object && details.TryGetProperty("reasoning_tokens", out var nested) && nested.ValueKind == JsonValueKind.Number)
            return nested.GetInt32();
        return null;
    }

    private static void WriteNew<T>(string path, T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, FreezeArtifact.Json);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static IReadOnlyDictionary<string, ProjectionClass> Classify(PdfCandidateAuthorityPreparedPack pack, IReadOnlyDictionary<string, string> functionByAlias)
    {
        var output = new Dictionary<string, ProjectionClass>(StringComparer.Ordinal);
        foreach (var candidate in pack.Universe.Candidates)
        {
            var functions = candidate.Endpoint.Parts.Select(part => functionByAlias[part.Alias]).ToArray();
            var establishes = functions.Count(function => function == "ESTABLISHES_STRUCTURE");
            output.Add(candidate.Id, establishes == functions.Length ? ProjectionClass.ALL_ESTABLISHES
                : establishes > 0 ? ProjectionClass.MIXED : ProjectionClass.NO_ESTABLISHES);
        }
        return output;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>> Correspondences(PdfCandidateAuthorityPreparedPack pack)
    {
        var owned = pack.OwnedAliases.ToHashSet(StringComparer.Ordinal);
        var candidates = pack.Universe.Candidates.ToDictionary(value => value.Id, StringComparer.Ordinal);
        var result = new Dictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>>(StringComparer.Ordinal);
        foreach (var relation in pack.Universe.Relations)
        {
            if (!candidates.TryGetValue(relation.CandidateId, out var candidate) || !owned.Contains(candidate.Endpoint.Parts[0].Alias)) continue;
            var root = candidate.Endpoint.Parts[0].Alias;
            var values = result.TryGetValue(root, out var existing) ? existing.ToList() : [];
            if (!values.Any(value => value.TargetPage == relation.TargetPage && value.TargetText == relation.TargetText))
                values.Add(new V5ReadOnlyCorrespondenceV1(relation.TargetPage, relation.TargetText));
            result[root] = values;
        }
        return result;
    }
}
