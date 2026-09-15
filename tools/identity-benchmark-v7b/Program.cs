using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Eval.ReasoningRetention;
using DocxHeaderExtractor.Infrastructure.AI;

namespace IdentityBenchmarkV7B;

internal static class Program
{
    private const string V7APreflightRelative = "artifacts/identity-benchmark/v7/identity-proposal/preflight-v1-sparse-positive";
    private const string V7AExecutionRelative = "artifacts/identity-benchmark/v7/identity-proposal/execution-v1-sparse-positive";
    private const string OutputRelative = "artifacts/identity-benchmark/v7/identity-verification/preflight-v1-independent-positive-edge";
    private const string ExecutionRelative = "artifacts/identity-benchmark/v7/identity-verification/execution-v1-independent-positive-edge";
    private const string Provider = "OpenRouter";
    private const string Model = "qwen/qwen3.7-flash";
    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";
    private const string RequestSetSha256 = "5af2dd92a27ff109a4bf929685f336fe9496d6c12602adfd1d3c6fe62e70cf45";
    private const int ExpectedOccurrences = 226;
    private const int ExpectedProposals = 58;
    private static readonly string[] ExpectedDocuments = ["DOC-0123", "DOC-0133", "DOC-0252"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> Main(string[] args)
    {
        var root = Path.GetFullPath(args.FirstOrDefault(x => !x.StartsWith("--", StringComparison.Ordinal)) ?? Directory.GetCurrentDirectory());
        try
        {
            if (args.Any(x => string.Equals(x, "--execute-v7b", StringComparison.Ordinal))) return await ExecuteAsync(root);
            if (args.Any(x => string.Equals(x, "--finalize-v7b", StringComparison.Ordinal))) return await FinalizeAsync(root);
            await PrepareAsync(root);
            Console.WriteLine("V7B_STATUS=INDEPENDENT_EDGE_VERIFIER_PREFLIGHT_FROZEN PROVIDER_CALLS=0 GOLD_READ_COUNT=0");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"V7B_ERROR={ex.Message}");
            return 2;
        }
    }

    private static async Task<int> ExecuteAsync(string root)
    {
        var preflight = Full(root, OutputRelative);
        var execution = Full(root, ExecutionRelative);
        var frozen = LoadFrozenRequests(preflight);
        Require(!Directory.Exists(execution) || !Directory.EnumerateFiles(execution, "*", SearchOption.AllDirectories).Any(), "V7B_EXECUTION_ALREADY_STARTED_NO_RESUME");
        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey)) return await BlockExecutionAsync(execution);
        var attemptsDir = Path.Combine(execution, "attempts");
        var rawDir = Path.Combine(execution, "raw-responses");
        var parsedDir = Path.Combine(execution, "parsed");
        Directory.CreateDirectory(attemptsDir); Directory.CreateDirectory(rawDir); Directory.CreateDirectory(parsedDir);
        await WriteAsync(Path.Combine(execution, "execution-manifest.json"), new
        {
            schemaVersion = "a99-v7b-execution-manifest-v1",
            status = "EXECUTING",
            provider = Provider,
            model = Model,
            endpoint = Endpoint,
            requestSetSha256 = RequestSetSha256,
            scheduledCalls = 3,
            transientRequestRetries = 0,
            maxParallelRequests = 1,
            goldReadCount = 0,
            historicalPredictionReadCount = 0,
            evaluationReadCount = 0,
            autoCollapse = false,
            connectedComponentsAuthority = false,
        });
        var options = new RemoteInferenceOptions
        {
            Endpoint = new Uri(Endpoint), ApiKey = apiKey, Model = Model, ContextSize = 1_000_000,
            MaxOutputTokens = 24_000, RequestTimeoutSeconds = 600, TransientRequestRetries = 0,
            MaxParallelRequests = 1, SendChatTemplateKwargs = false, OpenRouterAllowNonZdrPublicBenchmark = true,
        };
        var capability = new OpenRouterModelCapability
        {
            ModelId = Model, ContextLength = 1_000_000, ReasoningSupported = true,
            StructuredOutputSupported = true, SelectedReasoningEffort = "enabled",
            ReasoningEnabled = true, EffortListReported = false, MaxCompletionTokens = 65_536,
        };
        using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = Timeout.InfiniteTimeSpan };
        using var lease = await A99OpenRouterLiveProviderLease.AcquireAsync(root, "identity-benchmark-v7b", "3-document-global-independent-positive-edge-verifications");
        using var model = new OpenRouterCeilingReasoningModel(options, capability, http);
        var completed = new List<object>();
        for (var i = 0; i < frozen.Count; i++)
        {
            var item = frozen[i]; var sequence = i + 1; var attemptId = $"primary-{sequence:D3}";
            await WriteAsync(Path.Combine(attemptsDir, $"{sequence:D3}.started.json"), new
            {
                schemaVersion = "a99-v7b-attempt-start-v1", attemptId, sequence,
                documentId = item.DocumentId, requestHash = item.RequestHash, requestSetSha256 = RequestSetSha256,
                retry = false, goldReadBeforeAttempt = false, historicalPredictionReadBeforeAttempt = false,
                evaluationReadBeforeAttempt = false, autoCollapse = false, connectedComponentsAuthority = false,
            });
            var status = "PROVIDER_ERROR"; string? error = null; string? rawHash = null; string? parsedHash = null;
            int? httpStatus = null; string? providerCallId = null; RequestPacketTelemetry? telemetry = null; object? parsed = null;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var requestJson = item.Request.GetRawText();
                var result = await model.CompleteRawStructuredSemanticAsync(
                    item.DocumentId,
                    "V7B_INDEPENDENT_POSITIVE_EDGE_VERIFICATION",
                    $"v7b:{item.DocumentId}:{item.RequestHash}",
                    requestJson,
                    item.OccurrenceCount,
                    item.ProposalCount,
                    item.EvidenceCount,
                    SystemPromptV7B,
                    $"TASK=V7B_INDEPENDENT_POSITIVE_EDGE_VERIFICATION\n{requestJson}\nReturn exactly the requested JSON object.",
                    ResponseSchemaV7B(),
                    "a99_v7b_independent_positive_edge_verification_v1");
                telemetry = result.Telemetry; httpStatus = telemetry.HttpStatus; providerCallId = telemetry.ProviderCallId;
                rawHash = Sha256Text(result.Content);
                await File.WriteAllTextAsync(Path.Combine(rawDir, $"{sequence:D3}.json"), result.Content, new UTF8Encoding(false));
                using var response = JsonDocument.Parse(result.Content);
                var validation = ValidateResponse(item.Request, response.RootElement);
                parsed = new { response = response.RootElement.Clone(), validation };
                parsedHash = Sha256Text(JsonSerializer.Serialize(parsed, JsonOptions));
                await WriteAsync(Path.Combine(parsedDir, $"{sequence:D3}.json"), parsed);
                status = validation.Accepted ? "VALID" : "INVALID_VALIDATION";
                error = validation.Accepted ? null : validation.Errors.FirstOrDefault();
            }
            catch (JsonException ex) { status = "INVALID_SCHEMA"; error = ex.Message; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException or ReasoningCompletionException or FormatException) { status = "PROVIDER_ERROR"; error = ex.GetType().Name + ":" + ex.Message; }
            stopwatch.Stop();
            var completedAttempt = new
            {
                schemaVersion = "a99-v7b-attempt-v1", attemptId, sequence, documentId = item.DocumentId,
                requestHash = item.RequestHash, requestSetSha256 = RequestSetSha256, status, httpStatus, providerCallId,
                rawResponseSha256 = rawHash, parsedResponseSha256 = parsedHash, provider = Provider, model = Model,
                latencyMs = stopwatch.ElapsedMilliseconds, reportedInputTokens = telemetry?.ReportedInputTokens,
                reportedReasoningTokens = telemetry?.ReportedReasoningTokens, reportedOutputTokens = telemetry?.ReportedOutputTokens,
                finishReason = telemetry?.FinishReason, error, retryCount = 0,
                goldReadBeforeFreeze = false, historicalPredictionReadBeforeFreeze = false, evaluationReadBeforeFreeze = false,
                autoCollapse = false, connectedComponentsAuthority = false, response = parsed,
            };
            await WriteAsync(Path.Combine(attemptsDir, $"{sequence:D3}.completed.json"), completedAttempt);
            completed.Add(completedAttempt);
            await WriteAsync(Path.Combine(execution, "attempt-manifest.json"), new
            {
                schemaVersion = "a99-v7b-attempt-manifest-v1", immutableAttemptRecords = true,
                expectedAttemptCount = 3, actualAttemptCount = completed.Count, actualModelCalls = model.ProviderCalls,
                actualProviderCalls = model.ProviderCalls, retryCount = 0, goldReadCount = 0,
                historicalPredictionReadCount = 0, evaluationReadCount = 0, autoCollapse = false,
                connectedComponentsAuthority = false, attempts = completed,
            });
            Console.WriteLine($"V7B_ATTEMPT={sequence}/3 DOCUMENT={item.DocumentId} STATUS={status} PROVIDER_CALLS={model.ProviderCalls}");
        }
        Require(completed.Count == 3 && model.ProviderCalls == 3, "V7B_PROVIDER_CALL_COUNT");
        await WriteAsync(Path.Combine(execution, "prediction-freeze.json"), new
        {
            schemaVersion = "a99-v7b-prediction-freeze-v1",
            status = "V7B_VERIFIER_OUTPUTS_FROZEN_BEFORE_CLUSTERING_OR_EVALUATION",
            requestSetSha256 = RequestSetSha256, completedAttempts = 3,
            modelCalls = model.ProviderCalls, providerCalls = model.ProviderCalls,
            goldReadCount = 0, historicalPredictionReadCount = 0, evaluationReadCount = 0,
            autoCollapse = false, connectedComponentsAuthority = false, defaultPolicy = "REJECT_AND_UNRESOLVED_KEEP_SPLIT",
            attempts = completed,
        });
        Console.WriteLine($"V7B_STATUS=VERIFIER_OUTPUTS_FROZEN REQUESTS=3 MODEL_CALLS={model.ProviderCalls} PROVIDER_CALLS={model.ProviderCalls} GOLD_READ_COUNT=0 AUTO_COLLAPSE=false");
        return 0;
    }

    private static async Task<int> BlockExecutionAsync(string execution)
    {
        Directory.CreateDirectory(execution);
        await WriteAsync(Path.Combine(execution, "execution-manifest.json"), new
        {
            schemaVersion = "a99-v7b-execution-manifest-v1", status = "BLOCKED_ON_PROVIDER_API_KEY",
            scheduledCalls = 3, modelCalls = 0, providerCalls = 0, retryCount = 0,
            goldReadCount = 0, historicalPredictionReadCount = 0, evaluationReadCount = 0,
            autoCollapse = false, connectedComponentsAuthority = false,
        });
        return 1;
    }

    private static async Task<int> FinalizeAsync(string root)
    {
        var execution = Full(root, ExecutionRelative);
        using var manifest = Read(Path.Combine(execution, "attempt-manifest.json"));
        var m = manifest.RootElement;
        Require(m.GetProperty("actualAttemptCount").GetInt32() == 3 && m.GetProperty("actualProviderCalls").GetInt32() == 3 && m.GetProperty("retryCount").GetInt32() == 0 && m.GetProperty("goldReadCount").GetInt32() == 0 && m.GetProperty("historicalPredictionReadCount").GetInt32() == 0 && m.GetProperty("evaluationReadCount").GetInt32() == 0 && !m.GetProperty("autoCollapse").GetBoolean() && !m.GetProperty("connectedComponentsAuthority").GetBoolean(), "V7B_FINALIZE_FIREWALL");
        var valid = 0; var invalid = 0; var providerErrors = 0; var accepted = 0; var rejected = 0; var unresolved = 0; var acceptedSame = 0; var acceptedContinuation = 0; var rejectedProposals = 0; var unknownCandidate = 0; var unknownOccurrence = 0; var unknownEvidence = 0; var duplicate = 0; var missing = 0; var direction = 0; var schemaFailures = 0;
        foreach (var attempt in m.GetProperty("attempts").EnumerateArray())
        {
            var status = attempt.GetProperty("status").GetString()!;
            if (status == "VALID") valid++; else if (status is "INVALID_VALIDATION" or "INVALID_SCHEMA") invalid++; else providerErrors++;
            if (status == "INVALID_SCHEMA") schemaFailures++;
            if (status != "VALID") continue;
            var envelope = attempt.GetProperty("response");
            var response = envelope.GetProperty("response");
            var decisions = response.GetProperty("decisions").EnumerateArray().ToArray();
            foreach (var decision in decisions)
            {
                var outcome = decision.GetProperty("decision").GetString();
                if (outcome == "ACCEPT")
                {
                    accepted++;
                    if (decision.GetProperty("relation").GetString() == "SAME_SEMANTIC_REPEAT") acceptedSame++;
                    if (decision.GetProperty("relation").GetString() == "CONTINUATION_OF") acceptedContinuation++;
                }
                else if (outcome == "REJECT") { rejected++; rejectedProposals++; }
                else if (outcome == "UNRESOLVED") unresolved++;
            }
            var validation = envelope.GetProperty("validation");
            unknownCandidate += validation.GetProperty("unknownCandidateCount").GetInt32();
            unknownOccurrence += validation.GetProperty("unknownOccurrenceCount").GetInt32();
            unknownEvidence += validation.GetProperty("unknownEvidenceCount").GetInt32();
            duplicate += validation.GetProperty("duplicateDecisionCount").GetInt32();
            missing += validation.GetProperty("missingDecisionCount").GetInt32();
            direction += validation.GetProperty("directionErrorCount").GetInt32();
        }
        var summary = new
        {
            schemaVersion = "a99-v7b-verifier-summary-v1", status = "V7B_VERIFIER_OUTPUTS_FROZEN_BEFORE_CLUSTERING_OR_EVALUATION",
            scheduledRequests = 3, valid, invalidValidation = invalid, invalidSchema = schemaFailures, providerErrors,
            accepted, rejected, unresolved, acceptedSameSemanticRepeat = acceptedSame, acceptedContinuation,
            rejectedProposals, unknownCandidateIds = unknownCandidate, unknownOccurrenceRefs = unknownOccurrence,
            unknownEvidenceRefs = unknownEvidence, duplicateDecisions = duplicate, missingDecisions = missing,
            directionErrors = direction, modelCalls = 3, providerCalls = 3, goldReadCount = 0,
            historicalPredictionReadCount = 0, evaluationReadCount = 0, autoCollapse = false,
            connectedComponentsAuthority = false, defaultPolicy = "REJECT_AND_UNRESOLVED_KEEP_SPLIT",
        };
        await WriteAsync(Path.Combine(execution, "prediction-summary.json"), summary);
        await WriteAsync(Path.Combine(execution, "execution-final.json"), new
        {
            schemaVersion = "a99-v7b-execution-final-v1", status = "V7B_VERIFIER_FREEZE_COMPLETE",
            valid, invalidValidation = invalid, providerErrors, modelCalls = 3, providerCalls = 3,
            goldReadCount = 0, historicalPredictionReadCount = 0, evaluationReadCount = 0,
            autoCollapse = false, connectedComponentsAuthority = false, frozenBeforeClusteringOrEvaluation = true,
        });
        Console.WriteLine($"V7B_OFFLINE_SUMMARY_COMPLETE VALID={valid} INVALID={invalid} PROVIDER_ERRORS={providerErrors} ACCEPTED={accepted} REJECTED={rejected} UNRESOLVED={unresolved} MODEL_CALLS=3 PROVIDER_CALLS=3 GOLD_READ_COUNT=0");
        return 0;
    }

    private static async Task PrepareAsync(string root)
    {
        var v7aPreflight = Full(root, V7APreflightRelative);
        var v7aExecution = Full(root, V7AExecutionRelative);
        var output = Full(root, OutputRelative);
        Require(!Directory.Exists(output) || !Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories).Any(), "V7B_PREFLIGHT_ALREADY_EXISTS_NO_OVERWRITE");

        using var v7aRequestsDoc = Read(Path.Combine(v7aPreflight, "requests.json"));
        using var v7aPredictionDoc = Read(Path.Combine(v7aExecution, "prediction-freeze.json"));
        using var v7aSummaryDoc = Read(Path.Combine(v7aExecution, "prediction-summary.json"));
        ValidateV7AInputs(v7aRequestsDoc.RootElement, v7aPredictionDoc.RootElement, v7aSummaryDoc.RootElement);

        var records = v7aRequestsDoc.RootElement.GetProperty("requests").EnumerateArray().ToArray();
        var attempts = v7aPredictionDoc.RootElement.GetProperty("attempts").EnumerateArray().ToDictionary(x => x.GetProperty("documentId").GetString()!, StringComparer.Ordinal);
        var verifierRequests = new List<VerifierRequest>();
        foreach (var record in records.OrderBy(x => x.GetProperty("request").GetProperty("documentId").GetString(), StringComparer.Ordinal))
        {
            var request = record.GetProperty("request");
            var documentId = request.GetProperty("documentId").GetString()!;
            var attempt = attempts[documentId];
            Require(attempt.GetProperty("status").GetString() == "VALID", $"V7B_V7A_ATTEMPT_NOT_VALID_{documentId}");
            var response = attempt.GetProperty("response").GetProperty("response");
            var proposals = response.GetProperty("positiveProposals").EnumerateArray().ToArray();
            var candidateMap = request.GetProperty("candidatePairs").EnumerateArray().ToDictionary(x => x.GetProperty("pairId").GetString()!, x => x, StringComparer.Ordinal);
            var evidence = new List<object>();
            var verifierProposals = new List<object>();
            foreach (var proposal in proposals)
            {
                var pairId = proposal.GetProperty("pairId").GetString()!;
                Require(candidateMap.TryGetValue(pairId, out var candidate), $"V7B_UNKNOWN_V7A_PAIR_{documentId}_{pairId}");
                var left = proposal.GetProperty("left").GetString()!;
                var right = proposal.GetProperty("right").GetString()!;
                var reasons = candidate.GetProperty("reasons").EnumerateArray().Select(x => x.GetString()!).ToArray();
                var evidenceStart = evidence.Count;
                var evidenceRefs = reasons.Select((_, i) => $"E{evidenceStart + i + 1:000}").ToArray();
                for (var i = 0; i < reasons.Length; i++) evidence.Add(new { @ref = evidenceRefs[i], kind = reasons[i], pairId });
                verifierProposals.Add(new
                {
                    proposalId = pairId,
                    left,
                    right,
                    proposedRelation = proposal.GetProperty("relation").GetString(),
                    proposedDirection = proposal.TryGetProperty("from", out var from) && proposal.TryGetProperty("to", out var to)
                        ? new { from = from.GetString(), to = to.GetString() }
                        : null,
                    evidenceRefs,
                });
            }

            var verifierRequest = new
            {
                schemaVersion = "a99-v7b-independent-positive-edge-verification-v1",
                documentId,
                verifierMode = "INDEPENDENT_SOURCE_BACKED_EDGE_VERIFICATION",
                independence = new
                {
                    v7aRationaleIncluded = false,
                    v7aDecisionExplanationIncluded = false,
                    v7aProposalRelationIncluded = true,
                    goldDerivedInput = false,
                    historicalPredictionIncluded = false,
                    evaluationIncluded = false,
                },
                occurrences = request.GetProperty("occurrences").Clone(),
                sourceEvidence = new { evidence = evidence.ToArray() },
                proposedEdges = verifierProposals.ToArray(),
                outputContract = new
                {
                    decisions = "exactly one decision per proposedEdges.proposalId",
                    decisionsAllowed = new[] { "ACCEPT", "REJECT", "UNRESOLVED" },
                    acceptedRelations = new[] { "SAME_SEMANTIC_REPEAT", "CONTINUATION_OF" },
                    rejectedRelation = "NONE",
                    confidence = new[] { "HIGH", "MEDIUM", "LOW" },
                    evidenceRefs = "must reference supplied evidence refs",
                    defaultPolicy = "REJECT_AND_UNRESOLVED_KEEP_SPLIT",
                    autoCollapse = false,
                    connectedComponentsAuthority = false,
                    clusteringRequested = false,
                },
                forbidden = new[] { "Gold", "historical predictions", "evaluation artifacts", "connected-component collapse", "automatic merge", "parent", "ROOT", "level", "new proposal IDs" },
            };
            var json = JsonSerializer.Serialize(verifierRequest, JsonOptions);
            verifierRequests.Add(new VerifierRequest(documentId, Sha256Text(json), JsonDocument.Parse(json).RootElement.Clone(), proposals.Length));
        }

        Require(verifierRequests.Count == 3 && verifierRequests.Sum(x => x.ProposalCount) == ExpectedProposals, "V7B_REQUEST_COUNTS");
        var requestHashes = verifierRequests.Select(x => x.RequestHash).ToArray();
        var requestSetHash = Sha256Text(string.Join("\n", requestHashes));
        Directory.CreateDirectory(output);
        await WriteAsync(Path.Combine(output, "request-contract.json"), new
        {
            schemaVersion = "a99-v7b-independent-positive-edge-verification-contract-v1",
            input = new[] { "frozen V7A positive proposals", "exact source occurrences", "source-derived evidence" },
            output = new[] { "ACCEPT", "REJECT", "UNRESOLVED" },
            acceptedRelationPolicy = "ACCEPT is eligible for a later constrained graph only; it is not auto-collapse authority.",
            defaultPolicy = "REJECT_AND_UNRESOLVED_KEEP_SPLIT",
            confidencePolicy = "audit-only; no threshold promotion",
            independence = "V7A rationale and decision explanation are excluded; only frozen proposal identity/relation is shown.",
            forbidden = new[] { "Gold", "V4H/V5/V6 predictions", "evaluation artifacts", "connected-component collapse", "automatic merge", "parent", "ROOT", "level" },
        });
        await WriteAsync(Path.Combine(output, "manifest.json"), new
        {
            schemaVersion = "a99-v7b-independent-positive-edge-verification-preflight-v1",
            status = "READY_FOR_SEPARATE_PROVIDER_AUTHORIZATION",
            requestCount = verifierRequests.Count,
            occurrenceCount = ExpectedOccurrences,
            frozenV7AProposalCount = ExpectedProposals,
            requestHashes,
            requestSetSha256 = requestSetHash,
            sourcePreflightPath = V7APreflightRelative,
            v7aPredictionFreezePath = V7AExecutionRelative + "/prediction-freeze.json",
            goldReadCount = 0,
            historicalPredictionReadCount = 0,
            evaluationReadCount = 0,
            providerCalls = 0,
            modelCalls = 0,
            autoCollapse = false,
            connectedComponentsAuthority = false,
            generalizationClaim = false,
        });
        await WriteAsync(Path.Combine(output, "requests.json"), new
        {
            schemaVersion = "a99-v7b-independent-positive-edge-verification-requests-v1",
            status = "FROZEN_SOURCE_BACKED_VERIFIER_REQUESTS",
            requestCount = verifierRequests.Count,
            occurrenceCount = ExpectedOccurrences,
            frozenV7AProposalCount = ExpectedProposals,
            requestSetSha256 = requestSetHash,
            goldDerivedInput = false,
            v7aRationaleIncluded = false,
            requests = verifierRequests.Select(x => new { request = x.Request, requestHash = x.RequestHash, proposalCount = x.ProposalCount }).ToArray(),
        });
        await File.WriteAllTextAsync(Path.Combine(output, "report.md"), $"# A99 V7B — independent positive-edge verification preflight\n\nFrozen V7A proposals: **{ExpectedProposals}** across **{verifierRequests.Count}** document-global requests. The verifier sees exact source occurrences and source-derived evidence, but not V7A rationale, Gold, historical predictions, evaluation artifacts, clustering consequences, or hierarchy. Provider calls: **0**.\n\nAccepted decisions are only eligible input to a later constrained graph; they are not automatic collapse authority. `REJECT` and `UNRESOLVED` keep split.\n\nRequest-set SHA256: `{requestSetHash}`.\n");
    }

    private static IReadOnlyList<FrozenRequest> LoadFrozenRequests(string preflight)
    {
        using var manifest = Read(Path.Combine(preflight, "manifest.json"));
        var m = manifest.RootElement;
        Require(m.GetProperty("status").GetString() == "READY_FOR_SEPARATE_PROVIDER_AUTHORIZATION", "V7B_PREFLIGHT_STATUS");
        Require(m.GetProperty("requestCount").GetInt32() == 3 && m.GetProperty("occurrenceCount").GetInt32() == ExpectedOccurrences && m.GetProperty("frozenV7AProposalCount").GetInt32() == ExpectedProposals && m.GetProperty("providerCalls").GetInt32() == 0 && m.GetProperty("goldReadCount").GetInt32() == 0 && m.GetProperty("historicalPredictionReadCount").GetInt32() == 0 && m.GetProperty("evaluationReadCount").GetInt32() == 0 && !m.GetProperty("autoCollapse").GetBoolean() && !m.GetProperty("connectedComponentsAuthority").GetBoolean(), "V7B_PREFLIGHT_FIREWALL");
        Require(m.GetProperty("requestSetSha256").GetString() == RequestSetSha256, "V7B_REQUEST_SET_SHA");
        using var requests = Read(Path.Combine(preflight, "requests.json"));
        var items = requests.RootElement.GetProperty("requests").EnumerateArray().Select(record =>
        {
            var request = record.GetProperty("request").Clone();
            var hash = record.GetProperty("requestHash").GetString()!;
            Require(hash == Sha256Text(JsonSerializer.Serialize(request, JsonOptions)), "V7B_REQUEST_HASH");
            Require(request.GetProperty("documentId").GetString() is "DOC-0123" or "DOC-0133" or "DOC-0252", "V7B_DOCUMENT_ID");
            Require(!request.GetProperty("independence").GetProperty("goldDerivedInput").GetBoolean() && !request.GetProperty("independence").GetProperty("historicalPredictionIncluded").GetBoolean() && !request.GetProperty("independence").GetProperty("evaluationIncluded").GetBoolean(), "V7B_REQUEST_FIREWALL");
            var proposals = request.GetProperty("proposedEdges").EnumerateArray().ToArray();
            var proposalIds = proposals.Select(x => x.GetProperty("proposalId").GetString()!).ToHashSet(StringComparer.Ordinal);
            Require(proposalIds.Count == proposals.Length, "V7B_PROPOSAL_ID_DUPLICATE");
            var evidenceCount = request.GetProperty("sourceEvidence").GetProperty("evidence").GetArrayLength();
            return new FrozenRequest(request.GetProperty("documentId").GetString()!, hash, request, request.GetProperty("occurrences").GetArrayLength(), proposals.Length, evidenceCount, proposalIds);
        }).OrderBy(x => x.DocumentId, StringComparer.Ordinal).ToArray();
        Require(items.Select(x => x.DocumentId).SequenceEqual(ExpectedDocuments, StringComparer.Ordinal) && items.Sum(x => x.OccurrenceCount) == ExpectedOccurrences && items.Sum(x => x.ProposalCount) == ExpectedProposals, "V7B_REQUEST_SET");
        return items;
    }

    private static object ResponseSchemaV7B() => new
    {
        type = "object", additionalProperties = false, required = new[] { "decisions" }, properties = new
        {
            decisions = new
            {
                type = "array", items = new
                {
                    type = "object", additionalProperties = false,
                    required = new[] { "proposalId", "decision", "relation", "confidence", "evidenceRefs" },
                    properties = new
                    {
                        proposalId = new { type = "string", pattern = "^P[0-9]{4}$" },
                        decision = new { type = "string", @enum = new[] { "ACCEPT", "REJECT", "UNRESOLVED" } },
                        relation = new { type = "string", @enum = new[] { "SAME_SEMANTIC_REPEAT", "CONTINUATION_OF", "NONE" } },
                        confidence = new { type = "string", @enum = new[] { "HIGH", "MEDIUM", "LOW" } },
                        evidenceRefs = new { type = "array", items = new { type = "string", pattern = "^E[0-9]{3}$" } },
                        from = new { type = "string", pattern = "^U[0-9]{3}$" },
                        to = new { type = "string", pattern = "^U[0-9]{3}$" },
                    },
                },
            },
        },
    };

    private const string SystemPromptV7B = """
You are the A99 V7B independent source-backed positive-edge verifier. Verify only the supplied
proposedEdges. Do not create new proposals and do not infer omitted relations. For each proposal
return exactly one decision: ACCEPT only when the proposed relation is sufficiently supported by
the supplied source text and parser-owned evidence; REJECT when evidence contradicts it; or
UNRESOLVED when evidence is insufficient. REJECT and UNRESOLVED mean KEEP_SPLIT. ACCEPT is only
a verified edge candidate and is not permission to merge, build connected components, or cluster.
For ACCEPT, relation must confirm the proposed relation and CONTINUATION_OF must preserve the
supplied direction. For REJECT or UNRESOLVED use relation NONE and omit from/to. Evidence refs
must come only from the supplied evidence universe. Do not use Gold, historical predictions,
evaluation artifacts, parent, ROOT, level, or clustering consequences. Return exactly the
requested JSON object with one decision per proposed edge.
""";

    private static V7BValidation ValidateResponse(JsonElement request, JsonElement response)
    {
        var errors = new List<string>();
        if (response.ValueKind != JsonValueKind.Object || !response.TryGetProperty("decisions", out var decisions) || decisions.ValueKind != JsonValueKind.Array) return V7BValidation.Invalid("V7B_REQUIRED_FIELDS");
        var proposalMap = request.GetProperty("proposedEdges").EnumerateArray().ToDictionary(x => x.GetProperty("proposalId").GetString()!, x => x, StringComparer.Ordinal);
        var occurrenceRefs = request.GetProperty("occurrences").EnumerateArray().Select(x => x.GetProperty("ref").GetString()!).ToHashSet(StringComparer.Ordinal);
        var evidenceRefs = request.GetProperty("sourceEvidence").GetProperty("evidence").EnumerateArray().Select(x => x.GetProperty("@ref").GetString()!).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal); var unknownCandidate = 0; var unknownOccurrence = 0; var unknownEvidence = 0; var duplicate = 0; var direction = 0;
        foreach (var item in decisions.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("proposalId", out var idElement) || !item.TryGetProperty("decision", out var decisionElement) || !item.TryGetProperty("relation", out var relationElement) || !item.TryGetProperty("confidence", out var confidenceElement) || !item.TryGetProperty("evidenceRefs", out var evidenceElement) || idElement.ValueKind != JsonValueKind.String || decisionElement.ValueKind != JsonValueKind.String || relationElement.ValueKind != JsonValueKind.String || confidenceElement.ValueKind != JsonValueKind.String || evidenceElement.ValueKind != JsonValueKind.Array) { errors.Add("V7B_DECISION_SHAPE"); continue; }
            var id = idElement.GetString()!; var decision = decisionElement.GetString()!; var relation = relationElement.GetString()!;
            if (!proposalMap.TryGetValue(id, out var proposal)) { unknownCandidate++; continue; }
            if (!seen.Add(id)) { duplicate++; continue; }
            foreach (var evidence in evidenceElement.EnumerateArray()) if (evidence.ValueKind != JsonValueKind.String || !evidenceRefs.Contains(evidence.GetString()!)) unknownEvidence++;
            var proposedRelation = proposal.GetProperty("proposedRelation").GetString()!;
            var expectedDecisionRelation = decision == "ACCEPT" ? proposedRelation : "NONE";
            if (relation != expectedDecisionRelation) errors.Add("V7B_DECISION_RELATION_MISMATCH");
            var left = proposal.GetProperty("left").GetString()!; var right = proposal.GetProperty("right").GetString()!;
            if (!occurrenceRefs.Contains(left) || !occurrenceRefs.Contains(right)) unknownOccurrence += (!occurrenceRefs.Contains(left) ? 1 : 0) + (!occurrenceRefs.Contains(right) ? 1 : 0);
            var hasFrom = item.TryGetProperty("from", out var from); var hasTo = item.TryGetProperty("to", out var to);
            if (decision == "ACCEPT" && relation == "CONTINUATION_OF")
            {
                if (!hasFrom || !hasTo || from.ValueKind != JsonValueKind.String || to.ValueKind != JsonValueKind.String) direction++;
                else
                {
                    var expected = proposal.TryGetProperty("proposedDirection", out var directionObject) && directionObject.ValueKind == JsonValueKind.Object ? directionObject : default;
                    var expectedFrom = expected.ValueKind == JsonValueKind.Object && expected.TryGetProperty("from", out var ef) ? ef.GetString() : null;
                    var expectedTo = expected.ValueKind == JsonValueKind.Object && expected.TryGetProperty("to", out var et) ? et.GetString() : null;
                    if (from.GetString() != expectedFrom || to.GetString() != expectedTo) direction++;
                }
            }
            else if (hasFrom || hasTo) direction++;
        }
        if (seen.Count != proposalMap.Count) errors.Add("V7B_MISSING_DECISIONS");
        if (unknownCandidate > 0) errors.Add("V7B_UNKNOWN_CANDIDATE");
        if (unknownOccurrence > 0) errors.Add("V7B_UNKNOWN_OCCURRENCE");
        if (unknownEvidence > 0) errors.Add("V7B_UNKNOWN_EVIDENCE");
        if (duplicate > 0) errors.Add("V7B_DUPLICATE_DECISION");
        if (direction > 0) errors.Add("V7B_DIRECTION_ERROR");
        return new V7BValidation(errors.Count == 0, decisions.GetArrayLength(), unknownCandidate, unknownOccurrence, unknownEvidence, duplicate, proposalMap.Count - seen.Count, direction, errors.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static void ValidateV7AInputs(JsonElement requests, JsonElement prediction, JsonElement summary)
    {
        Require(requests.GetProperty("status").GetString() == "FROZEN_SOURCE_ONLY_SPARSE_POSITIVE_REQUESTS", "V7B_V7A_REQUEST_STATUS");
        Require(requests.GetProperty("requestCount").GetInt32() == 3 && requests.GetProperty("occurrenceCount").GetInt32() == ExpectedOccurrences && requests.GetProperty("candidatePairCount").GetInt32() == 291, "V7B_V7A_REQUEST_COUNTS");
        Require(!requests.GetProperty("goldDerivedInput").GetBoolean() && !requests.GetProperty("v4hEvaluationIncluded").GetBoolean() && !requests.GetProperty("v5PredictionIncluded").GetBoolean() && !requests.GetProperty("v6dPredictionIncluded").GetBoolean(), "V7B_V7A_INPUT_FIREWALL");
        Require(prediction.GetProperty("status").GetString() == "V7A_PROPOSALS_FROZEN_BEFORE_EVALUATION_OR_PROMOTION" && prediction.GetProperty("providerCalls").GetInt32() == 3 && prediction.GetProperty("goldReadCount").GetInt32() == 0 && prediction.GetProperty("historicalPredictionReadCount").GetInt32() == 0 && prediction.GetProperty("evaluationReadCount").GetInt32() == 0 && prediction.GetProperty("modelProposedOnly").GetBoolean() && !prediction.GetProperty("autoCollapse").GetBoolean(), "V7B_V7A_PREDICTION_FIREWALL");
        Require(summary.GetProperty("valid").GetInt32() == 3 && summary.GetProperty("invalidValidation").GetInt32() == 0 && summary.GetProperty("providerErrors").GetInt32() == 0 && summary.GetProperty("proposedPositiveEdges").GetInt32() == ExpectedProposals, "V7B_V7A_SUMMARY");
    }

    private sealed record VerifierRequest(string DocumentId, string RequestHash, JsonElement Request, int ProposalCount);
    private sealed record FrozenRequest(string DocumentId, string RequestHash, JsonElement Request, int OccurrenceCount, int ProposalCount, int EvidenceCount, IReadOnlySet<string> ProposalIds);
    private sealed record V7BValidation(bool Accepted, int DecisionCount, int UnknownCandidateCount, int UnknownOccurrenceCount, int UnknownEvidenceCount, int DuplicateDecisionCount, int MissingDecisionCount, int DirectionErrorCount, IReadOnlyList<string> Errors)
    {
        public static V7BValidation Invalid(string error) => new(false, 0, 0, 0, 0, 0, 0, 0, [error]);
    }
    private static string Full(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    private static JsonDocument Read(string path) => JsonDocument.Parse(File.ReadAllText(path));
    private static string Sha256Text(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static async Task WriteAsync(string path, object value) => await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false));
    private static void Require(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
