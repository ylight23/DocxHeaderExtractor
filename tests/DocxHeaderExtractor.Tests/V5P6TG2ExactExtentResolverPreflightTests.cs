using System.Text;
using System.Text.Json;
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
    private const string Gold089Path = "eval/a99-closed-loop/gold/SRC-089.gold.json";
    private const string Gold095Path = "eval/a99-closed-loop/gold/SRC-095.gold.json";
    private const string Review095Path = "eval/a99-closed-loop/source-review-v1/SRC-095/review-items.json";
    private const string RunVariable = "A99_RUN_P6TG2_CANARY";
    private const string RunAnchorExistenceVariable = "A99_RUN_P6TG2A_CANARY";
    private const string Protocol = "v5-function-conditioned-exact-extent-resolver-preflight-1";
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

    private sealed record AnchorExistenceRequest(string DocumentId, string SystemPrompt, string UserMessage,
        string MessageHash, int MessageBytes, byte[] ProviderBody, int ProviderBytes, string ProviderHash,
        IReadOnlyList<(string Occurrence, string Alias)> Primaries, int MaxCompletionTokens);

    private sealed record ParsedAnchorLedger(int RawDecisions, int HasStructuralExtent, int NoStructuralExtent,
        int Quarantined, int MissingPrimaries, IReadOnlyList<object> Decisions, IReadOnlyList<object> Refusals);

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
        using var provider = new OpenRouterHeaderExtractor(http, new RemoteInferenceOptions
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
        using var provider = new OpenRouterHeaderExtractor(http, new RemoteInferenceOptions
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
