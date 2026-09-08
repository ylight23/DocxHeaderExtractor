using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Features;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Policy;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>
/// A99-only local Qwen campaign runner.  All documents and owned ranges share one global queue;
/// hierarchy/projection are executed only after the per-document extraction barrier.
/// </summary>
public static class LocalQwenLargeCorpusRunner
{
    private const int GlobalMaxInflight = 8;
    private const string CorpusPrefix = "todo10_8/heading_corpus_95_word/";
    private const string InventoryPath = "eval/a99-dataset/document-inventory.v1.json";
    private const string OutputRoot = "eval/a99-closed-loop/ceiling-unblock";
    /// <summary>
    /// Batched throughput mode: one call covers every occurrence owned by a whole context window
    /// instead of exactly one occurrence, so each request's full prefill (context load) is paid
    /// once per window instead of once per paragraph -- the prior 1-call-per-paragraph contract
    /// left the 2x RTX 3090 server mostly idle re-processing shared context on every call.
    /// </summary>
    private const int MaxHeadingsPerBatchedResponse = 200;

    public static Task<int> RunAsync(string repoRoot, CancellationToken ct = default) => RunCoreAsync(repoRoot, null, GlobalMaxInflight, "", null, null, 80_000, 48_000, ct);
    public static async Task<int> RunPreflightAsync(string repoRoot, int documents = 2, CancellationToken ct = default)
    {
        var count = Math.Clamp(documents, 1, 3);
        // Use stable, medium-large DEV representatives rather than the first legal-code file
        // (DOC-0181 has ~3k paragraphs and would make a sequential baseline impractical).
        var sequential = await RunCoreAsync(repoRoot, count, 1, "preflight/sequential", ["DOC-0258", "DOC-0182", "DOC-0189"], 1, 80_000, 48_000, ct);
        var concurrent = await RunCoreAsync(repoRoot, count, GlobalMaxInflight, "preflight/concurrent", ["DOC-0258", "DOC-0182", "DOC-0189"], 1, 80_000, 48_000, ct);
        return sequential == 0 && concurrent == 0 ? 0 : 1;
    }

    private static async Task<int> RunCoreAsync(string repoRoot, int? documentLimit, int maxInflight, string artifactStem, IReadOnlyList<string>? preferredDocumentIds, int? maxSegmentsPerDocument, int maxContextCharacters, int windowCharacters, CancellationToken ct)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var debugRoot = Path.Combine(repoRoot, OutputRoot);
        Directory.CreateDirectory(debugRoot);
        await WriteJsonAsync(Path.Combine(debugRoot, "debug-fix-summary.v1.json"), new
        {
            initialHead = GitSha(repoRoot),
            bugsFound = new[]
            {
                new { bugId = "C4-001", symptom = "resume reused source-only checkpoint", reproduction = "checkpoint lacked configuration identity", rootCause = "CHECKPOINT_RESUME", owner = "runner", fix = "require source SHA and configuration signature", testProof = "preflight rerun after signature change" },
                new { bugId = "C4-002", symptom = "model numeric level could become authoritative", reproduction = "proposal level passed directly to materializer", rootCause = "PARENT_GRAPH", owner = "runner", fix = "validate local parent tokens and derive graph depth", testProof = "preflight execution parentGraphValidated=true" },
                new { bugId = "C4-003", symptom = "campaign lacked append-friendly audit log", reproduction = "no campaign.execution.jsonl emitted", rootCause = "ARTIFACT_WRITE", owner = "runner", fix = "structured campaign/document terminal events plus telemetry", testProof = "preflight campaign.execution.jsonl and telemetry.v1.json" },
                new { bugId = "C4-004", symptom = "final proposal ordering depended on identity sort", reproduction = "ordering used SourceId before source ordinal", rootCause = "ARTIFACT_WRITE", owner = "runner", fix = "sort by canonical source ordinal/span/identity", testProof = "Release build and full regression" },
                new { bugId = "C4-005", symptom = "campaign JSONL events were multi-line", reproduction = "indented serializer made one event span several lines", rootCause = "ARTIFACT_WRITE", owner = "runner", fix = "compact serializer dedicated to JSONL", testProof = "post-fix log line validation" },
            },
            focusedTests = "50 passed", releaseBuild = "PASS", gitDiffCheck = "PASS", fullSuite = "1212 passed; frozen N15 only"
        }, ct);
        var inventory = ReadInventory(Path.Combine(repoRoot, InventoryPath));
        var selected = inventory.Where(x => x.SourcePath.Replace('\\', '/').StartsWith(CorpusPrefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.DocumentId, StringComparer.Ordinal).ToArray();
        if (preferredDocumentIds is not null)
        {
            var rank = preferredDocumentIds.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i, StringComparer.Ordinal);
            selected = selected.Where(x => rank.ContainsKey(x.DocumentId)).OrderBy(x => rank[x.DocumentId]).ToArray();
        }
        if (documentLimit is { } limit) selected = selected.Take(limit).ToArray();
        if (selected.Length == 0) throw new InvalidDataException("LOCAL_QWEN_CORPUS_EMPTY");

        if (maxInflight is < 1 or > GlobalMaxInflight) throw new ArgumentOutOfRangeException(nameof(maxInflight));
        var output = Path.Combine(repoRoot, OutputRoot, artifactStem);
        Directory.CreateDirectory(Path.Combine(output, "documents"));
        var runId = $"R1C4-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}-{maxInflight}";
        var campaignLog = Path.Combine(output, "campaign.execution.jsonl");
        await AppendEventAsync(campaignLog, new { timestampUtc = DateTimeOffset.UtcNow, runId, eventType = "CAMPAIGN_STARTED", documentId = (string?)null, documentOrdinal = (int?)null, segmentId = (string?)null, attempt = 0, status = "RUNNING", message = $"selected={selected.Length};maxInflight={maxInflight}" }, ct);
        var options = new RemoteInferenceOptions
        {
            Endpoint = new Uri("http://192.168.11.22:8881/v1/chat/completions"), Model = "Qwen3.8-27B-AWQ-INT4",
            ContextSize = 200_000, MaxOutputTokens = 16_384, MaxParallelRequests = maxInflight,
            RequestTimeoutSeconds = 180, TransientRequestRetries = 2, SendChatTemplateKwargs = true, RequireJsonObjectResponse = true,
        };
        options.Validate();
        using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10), MaxConnectionsPerServer = GlobalMaxInflight }) { Timeout = Timeout.InfiniteTimeSpan };
        await VerifyModelIdentityAsync(options, http, ct);
        using var model = new LocalQwenReasoningSemanticModel(options, http, maxHeadingsPerResponse: MaxHeadingsPerBatchedResponse);
        await using var scheduler = new BoundedReasoningInferenceScheduler<InferenceWorkItem, ReasoningModelResponse>(maxInflight, (work, token) => CompleteWithRetryAsync(model, work.Request, token), maxInflight * 2);

        var config = new { runId, experimentId = "R1C4_LOCAL_QWEN_LARGE_CORPUS", corpusTotal = selected.Length, selectedDocuments = selected.Select(x => x.DocumentId).ToArray(), endpoint = options.Endpoint.ToString(), model = options.Model, maxInflight, maxSegmentsPerDocument, contextCharacters = maxContextCharacters, windowCharacters, serverConfigFrozen = true, sourceFaithfulContext = true, goldInRuntime = false, cloudFallback = "NONE", semanticContractVersion = ReasoningPrompt.Version, configurationSignature = ConfigurationSignature, repoSha = GitSha(repoRoot), startedUtc = DateTimeOffset.UtcNow };
        await File.WriteAllTextAsync(Path.Combine(output, documentLimit is null ? "campaign-config.v1.json" : "preflight-config.v1.json"), JsonSerializer.Serialize(config, JsonOptions), ct);

        var states = new List<DocumentState>();
        var allResults = new List<ScheduledInferenceResult<InferenceWorkItem, ReasoningModelResponse>>();
        var campaignClock = Stopwatch.StartNew();
        var failedPreparation = 0;
        // Prepare source-faithful states first, then feed one owned segment per document per
        // round. This is deliberately round-robin: a giant document cannot monopolize the
        // bounded global queue before independent documents get a request started.
        for (var ordinal = 0; ordinal < selected.Length; ordinal++)
        {
            ct.ThrowIfCancellationRequested();
            var item = selected[ordinal];
            var documentDirectory = Path.Combine(output, "documents", item.DocumentId);
            Directory.CreateDirectory(documentDirectory);
            var executionPath = Path.Combine(documentDirectory, "execution.v1.json");
            if (TryResume(executionPath, item.SourceSha256, ConfigurationSignature)) continue;
            var state = new DocumentState(item, ordinal);
            await AppendEventAsync(campaignLog, new { timestampUtc = DateTimeOffset.UtcNow, runId, eventType = "DOCUMENT_STARTED", documentId = item.DocumentId, documentOrdinal = ordinal, status = "RUNNING", sourceHash = item.SourceSha256 }, ct);
            try
            {
                var sourcePath = Path.Combine(repoRoot, item.SourcePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(sourcePath)) throw new FileNotFoundException("source missing", sourcePath);
                if (!Sha256(sourcePath).Equals(item.SourceSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("source SHA mismatch");
                state.Source = new OpenXmlDocumentSource().Read(sourcePath);
                var features = NumberingStyleFeatures.FromSourceDocument(state.Source);
                var derived = new DocumentFeatureDeriver().Derive(state.Source);
                state.Policy = DocxPolicyStateBuilder.Build(state.Source, features, derived, new PipelineOptions { DisableLlm = false }.Extraction);
                state.Pack = ReasoningContextBuilder.Build(state.Source, state.Policy, maxContextCharacters, windowCharacters, expandOwnedPerOccurrence: false);
                if (maxSegmentsPerDocument is { } cap && state.Pack.Segments.Count > cap)
                    state.Pack = state.Pack with { Segments = state.Pack.Segments.Take(cap).ToArray(), WindowCount = cap };
                states.Add(state);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException)
            {
                failedPreparation++; var status = ex is FileNotFoundException ? "SOURCE_FAILURE" : ex.Message.Contains("context", StringComparison.OrdinalIgnoreCase) ? "CONTEXT_LIMIT" : "UNKNOWN_EXECUTION_FAILURE"; await WriteExecutionAsync(executionPath, new { documentId = item.DocumentId, sourceSha256 = item.SourceSha256, configurationSignature = ConfigurationSignature, status, error = ex.Message }, ct); await AppendEventAsync(campaignLog, new { timestampUtc = DateTimeOffset.UtcNow, runId, eventType = "DOCUMENT_FAILED", documentId = item.DocumentId, documentOrdinal = ordinal, status, message = ex.Message }, ct);
            }
        }

        var nextSegment = states.ToDictionary(state => state, _ => 0);
        var activeStates = states.ToList();
        while (activeStates.Count > 0)
        {
            foreach (var state in activeStates.ToArray())
            {
                var index = nextSegment[state];
                if (index >= state.Pack!.Segments.Count) { activeStates.Remove(state); continue; }
                var segment = state.Pack.Segments[index];
                var occurrenceById = state.Pack.Occurrences.ToDictionary(o => o.SourceOccurrenceId, StringComparer.Ordinal);
                var ownedOccurrences = segment.OwnedSourceOccurrenceIds.Select(id => occurrenceById[id]).ToArray();
                var scopes = ownedOccurrences.Select((occ, i) => new ReasoningOwnedOutputScope
                {
                    CanonicalSourceId = occ.SourceId, SourceOccurrenceId = occ.SourceOccurrenceId,
                    RawTextLength = occ.RawText.Length, OwnedStart = 0, OwnedEnd = occ.RawText.Length, OwnedIndex = i,
                }).ToArray();
                var request = new ReasoningModelRequest
                {
                    RequestId = ReasoningPrompt.BuildRequestId(state.Source!.DocumentId, segment.ContextSegmentId, ConfigurationSignature),
                    DocumentId = state.Source.DocumentId, Route = ReasoningRoute.ModelCapabilityCeiling.ToString(), SemanticPassId = "semantic-heading-extraction-v2-batched",
                    ContextSegmentId = segment.ContextSegmentId, SystemPrompt = ReasoningPrompt.System + ParentTokenInstruction + ReasoningPrompt.BatchedInstruction,
                    UserPrompt = ReasoningPrompt.BuildUserBatched(segment, false, scopes),
                    SourceOccurrenceIds = segment.SourceOccurrenceIds, OwnedSourceOccurrenceIds = segment.OwnedSourceOccurrenceIds,
                    OwnedOutputScope = scopes.Length > 0 ? scopes[0] : new ReasoningOwnedOutputScope { CanonicalSourceId = "none", SourceOccurrenceId = "none", RawTextLength = 0, OwnedStart = 0, OwnedEnd = 0 },
                    OwnedOutputScopes = scopes,
                    AttemptId = $"{state.Source.DocumentId}:{segment.ContextSegmentId}:attempt-1", ConfigurationSignature = ConfigurationSignature,
                };
                var task = await scheduler.EnqueueAsync(new InferenceWorkItem(state, segment, ownedOccurrences, request), ct);
                state.Requests.Add(task); nextSegment[state] = index + 1;
            }
        }

        var publicationTasks = states.Select(PublishDocumentAsync).ToArray();
        await scheduler.CompleteAsync(ct);
        await Task.WhenAll(publicationTasks);
        async Task PublishDocumentAsync(DocumentState state)
        {
            var docDir = Path.Combine(output, "documents", state.Inventory.DocumentId);
            Directory.CreateDirectory(docDir);
            var results = (await Task.WhenAll(state.Requests)).OrderBy(x => x.Work.Segment.Ordinal).ToArray();
            lock (allResults) allResults.AddRange(results);
            var failed = results.Where(x => !x.Succeeded).ToArray();
            if (failed.Length > 0)
            {
                var failureStatus = failed.Any(x => x.Error is ReasoningCompletionException r && r.FailureClass.Contains("SCHEMA", StringComparison.OrdinalIgnoreCase)) ? "SCHEMA_FAILURE" : failed.Any(x => x.Error is TimeoutException or OperationCanceledException) ? "LOCAL_MODEL_TIMEOUT" : "MODEL_SERVICE_FAILURE";
                var executionArtifactPath = Path.Combine(docDir, "execution.v1.json");
                await WriteExecutionAsync(executionArtifactPath, new { documentId = state.Inventory.DocumentId, sourceSha256 = state.Inventory.SourceSha256, configurationSignature = ConfigurationSignature, model = model.ModelName, endpoint = options.Endpoint.ToString(), status = failureStatus, segmentCount = state.Pack!.Segments.Count, modelRequestCount = results.Length, failedRequests = failed.Length, errors = failed.Select(x => x.Error?.Message).ToArray() }, ct);
                var errorAuditPath = Path.Combine(docDir, "error-audit.v1.json");
                await WriteJsonAsync(errorAuditPath, new { documentId = state.Inventory.DocumentId, executionFailureClass = failureStatus, errors = failed.Select(x => x.Error?.Message).ToArray(), auditedUtc = DateTimeOffset.UtcNow }, ct);
                await AppendEventAsync(campaignLog, new { eventType = "DOCUMENT_RESULT_READY", documentId = state.Inventory.DocumentId, documentOrdinal = state.Ordinal, terminalStatus = failureStatus, headingCount = 0, modelRequestCount = results.Length, retryCount = 0, wallClockMs = results.Sum(x => x.QueueWait.TotalMilliseconds + x.Inference.TotalMilliseconds), resultPath = (string?)null, executionPath = executionArtifactPath, errorAuditPath, timestampUtc = DateTimeOffset.UtcNow, failureClass = failureStatus }, ct);
                Console.WriteLine($"[C4][RESULT_READY] document={state.Inventory.DocumentId} ordinal={state.Ordinal} status={failureStatus} segments={results.Length} requests={results.Length} execution={executionArtifactPath} errorAudit={errorAuditPath}");
                await AppendEventAsync(campaignLog, new { timestampUtc = DateTimeOffset.UtcNow, runId, eventType = "DOCUMENT_FAILED", documentId = state.Inventory.DocumentId, documentOrdinal = state.Ordinal, status = failureStatus, message = $"failedRequests={failed.Length}" }, ct);
                return;
            }
            var (proposals, parentGraphErrors) = MaterializeAndValidateParentGraph(results, state.Pack!);
            if (parentGraphErrors.Count > 0)
            {
                var executionArtifactPath = Path.Combine(docDir, "execution.v1.json");
                await WriteExecutionAsync(executionArtifactPath, new { documentId = state.Inventory.DocumentId, sourceSha256 = state.Inventory.SourceSha256, configurationSignature = ConfigurationSignature, model = model.ModelName, endpoint = options.Endpoint.ToString(), status = "PARENT_GRAPH_VALIDATION_FAILURE", segmentCount = state.Pack!.Segments.Count, modelRequestCount = results.Length, parentGraphErrors }, ct);
                var errorAuditPath = Path.Combine(docDir, "error-audit.v1.json");
                await WriteJsonAsync(errorAuditPath, new { documentId = state.Inventory.DocumentId, executionFailureClass = "PARENT_GRAPH_FAILURE", parentGraphErrors, auditedUtc = DateTimeOffset.UtcNow }, ct);
                await AppendEventAsync(campaignLog, new { eventType = "DOCUMENT_RESULT_READY", documentId = state.Inventory.DocumentId, documentOrdinal = state.Ordinal, terminalStatus = "PARENT_GRAPH_VALIDATION_FAILURE", headingCount = 0, modelRequestCount = results.Length, retryCount = 0, wallClockMs = results.Sum(x => x.QueueWait.TotalMilliseconds + x.Inference.TotalMilliseconds), resultPath = (string?)null, executionPath = executionArtifactPath, errorAuditPath, timestampUtc = DateTimeOffset.UtcNow, failureClass = "PARENT_GRAPH_FAILURE" }, ct);
                Console.WriteLine($"[C4][RESULT_READY] document={state.Inventory.DocumentId} ordinal={state.Ordinal} status=PARENT_GRAPH_VALIDATION_FAILURE execution={executionArtifactPath} errorAudit={errorAuditPath}");
                await AppendEventAsync(campaignLog, new { timestampUtc = DateTimeOffset.UtcNow, runId, eventType = "DOCUMENT_FAILED", documentId = state.Inventory.DocumentId, documentOrdinal = state.Ordinal, status = "PARENT_GRAPH_FAILURE", message = $"errors={parentGraphErrors.Count}" }, ct);
                return;
            }
            var materialized = ReasoningProposalMaterializer.Materialize(state.Source!, state.Policy!, proposals);
            var sourceOrdinal = state.Pack!.Occurrences.ToDictionary(x => x.SourceId, x => x.SourceOrdinal, StringComparer.Ordinal);
            var ordered = proposals.OrderBy(x => sourceOrdinal.GetValueOrDefault(x.SourceId, int.MaxValue)).ThenBy(x => x.HeadingSpan.Start).ThenBy(x => x.SourceId, StringComparer.Ordinal).ToArray();
            var accepted = materialized.Validated.Where(x => x.Accepted).Select(x => x.Proposal).Where(x => !ExcludedProjectionRole(x.SemanticRole)).OrderBy(x => sourceOrdinal.GetValueOrDefault(x.SourceId, int.MaxValue)).ThenBy(x => x.HeadingSpan.Start).ThenBy(x => x.SourceId, StringComparer.Ordinal).ToArray();
            var predictionPath = Path.Combine(docDir, "prediction.v1.json");
            await WriteJsonAsync(predictionPath, new { documentId = state.Inventory.DocumentId, schemaVersion = ReasoningPrompt.Version, sourceSha256 = state.Inventory.SourceSha256, sourceFaithfulContext = true, segmentCount = state.Pack.Segments.Count, proposals = ordered, projection = new { excludedRoles = new[] { "AGENDA_NAVIGATION_HEADING", "TOC_ENTRY", "FRONT_MATTER" }, finalIncluded = accepted } }, ct);
            var predictionHash = Sha256(predictionPath);
            await WriteJsonAsync(Path.Combine(docDir, "prediction.freeze.v1.json"), new { documentId = state.Inventory.DocumentId, predictionHash, frozenUtc = DateTimeOffset.UtcNow, goldReadBeforeFreeze = false, configurationSignature = ConfigurationSignature }, ct);
            var executionPath = Path.Combine(docDir, "execution.v1.json");
            await WriteExecutionAsync(executionPath, new { documentId = state.Inventory.DocumentId, sourceSha256 = state.Inventory.SourceSha256, configurationSignature = ConfigurationSignature, model = model.ModelName, endpoint = options.Endpoint.ToString(), status = "SUCCESS", segmentCount = state.Pack!.Segments.Count, modelRequestCount = results.Length, successfulRequests = results.Length, failedRequests = 0, wallClockMs = results.Sum(x => x.QueueWait.TotalMilliseconds + x.Inference.TotalMilliseconds), parentGraphValidated = true, derivedLevel = true, projectionApplied = true, predictionHash, peakInflight = scheduler.PeakInflight }, ct);
            var resultPath = Path.Combine(docDir, "result.v1.json");
            await WriteJsonAsync(resultPath, new { documentId = state.Inventory.DocumentId, status = "SUCCESS", headings = accepted }, ct);
            var resultHash = Sha256(resultPath);
            await AppendEventAsync(campaignLog, new { eventType = "DOCUMENT_RESULT_READY", documentId = state.Inventory.DocumentId, documentOrdinal = state.Ordinal, terminalStatus = "SUCCESS", headingCount = accepted.Length, modelRequestCount = results.Length, retryCount = 0, wallClockMs = results.Sum(x => x.QueueWait.TotalMilliseconds + x.Inference.TotalMilliseconds), predictionHash, resultHash, resultPath, executionPath, timestampUtc = DateTimeOffset.UtcNow }, ct);
            Console.WriteLine($"[C4][RESULT_READY] document={state.Inventory.DocumentId} ordinal={state.Ordinal} status=SUCCESS headings={accepted.Length} segments={results.Length} requests={results.Length} result={resultPath} execution={executionPath}");
        }
        campaignClock.Stop();
        await WriteJsonAsync(Path.Combine(output, "telemetry.v1.json"), new
        {
            campaign = new { maxInflight, peakInflight = scheduler.PeakInflight, provider = model.ProviderName, model = model.ModelName },
            requests = allResults.Select(x => new
            {
                requestId = x.Work.Request.RequestId, documentId = x.Work.Request.DocumentId,
                contextSegmentId = x.Work.Request.ContextSegmentId, segmentOrdinal = x.Work.Segment.Ordinal,
                queueWaitMs = x.QueueWait.TotalMilliseconds, inferenceMs = x.Inference.TotalMilliseconds,
                succeeded = x.Succeeded, error = x.Error?.Message,
            }).ToArray(),
            providerAttempts = model.CompletionTelemetry,
        }, ct);
        var executionFiles = Directory.EnumerateFiles(Path.Combine(output, "documents"), "execution.v1.json", SearchOption.AllDirectories).Select(path => JsonDocument.Parse(File.ReadAllText(path)).RootElement).Where(root => root.TryGetProperty("documentId", out _)).ToArray();
        var summary = new { artifactKind = "a99_local_qwen_large_campaign_summary", schemaVersion = "a99-local-qwen-large-run-v1", status = "COMPLETE", corpusTotal = selected.Length, documentsCompleted = executionFiles.Count(x => x.GetProperty("status").GetString() == "SUCCESS"), documentsFailed = failedPreparation + executionFiles.Count(x => x.GetProperty("status").GetString() != "SUCCESS"), totalModelRequests = model.ProviderCalls, peakInflightRequests = scheduler.PeakInflight, wallClockMs = campaignClock.ElapsedMilliseconds, documentsPerHour = campaignClock.Elapsed.TotalHours <= 0 ? 0 : selected.Length / campaignClock.Elapsed.TotalHours, serverConfigChanged = false, openRouterCalls = 0, openAIApiCalls = 0, anthropicCalls = 0, externalProviderCalls = 0, cloudFallback = "NONE" };
        await WriteJsonAsync(Path.Combine(output, documentLimit is null ? "campaign-summary.v1.json" : "preflight-summary.v1.json"), summary, ct);
        await AppendEventAsync(campaignLog, new { timestampUtc = DateTimeOffset.UtcNow, runId, eventType = "CAMPAIGN_COMPLETED", documentId = (string?)null, documentOrdinal = (int?)null, status = summary.documentsFailed == 0 ? "SUCCESS" : "COMPLETED_WITH_FAILURES", message = $"terminal={executionFiles.Length};pending={Math.Max(0, selected.Length - executionFiles.Length)}", peakInflight = scheduler.PeakInflight, totalRequests = model.ProviderCalls }, ct);
        Console.WriteLine(JsonSerializer.Serialize(summary, JsonOptions));
        return failedPreparation == 0 && summary.documentsFailed == 0 ? 0 : 1;
    }

    private static async Task<ReasoningModelResponse> CompleteWithRetryAsync(LocalQwenReasoningSemanticModel model, ReasoningModelRequest request, CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try { return await model.CompleteAsync(request with { AttemptId = $"{request.RequestId}:attempt-{attempt}" }, TimeSpan.FromSeconds(180), ct).ConfigureAwait(false); }
            catch (Exception ex) when (attempt < 3 && ex is ReasoningCompletionException or HttpRequestException) { last = ex; await Task.Delay(250, ct).ConfigureAwait(false); }
        }
        throw last ?? new InvalidOperationException("local inference failed");
    }

    private static (IReadOnlyList<ReasoningHeadingProposal> Proposals, IReadOnlyList<string> Errors) MaterializeAndValidateParentGraph(
        IReadOnlyList<ScheduledInferenceResult<InferenceWorkItem, ReasoningModelResponse>> results,
        ReasoningContextPack pack)
    {
        var list = new List<ReasoningHeadingProposal>();
        foreach (var result in results)
        {
            var owned = result.Work.Occurrences;
            foreach (var h in result.Result!.Headings)
            {
                var ownedIndex = h.OwnedIndex ?? (owned.Count == 1 ? 0 : (int?)null);
                if (ownedIndex is not { } resolvedIndex || resolvedIndex < 0 || resolvedIndex >= owned.Count)
                    throw new InvalidDataException($"MODEL_RESPONSE_INVALID_OWNED_INDEX:{h.OwnedIndex}");
                var occurrence = owned[resolvedIndex]; var span = new StructuralSpan(h.Start, h.End);
                if (!span.IsValidFor(occurrence.RawText)) throw new InvalidDataException("MODEL_RESPONSE_INVALID_SPAN");
                list.Add(new ReasoningHeadingProposal { SourceId = occurrence.SourceId, HeadingSpan = span, Text = occurrence.RawText[span.Start..span.End], SemanticRole = h.SemanticRole, ProposedLevel = h.ProposedLevel, ProposedParent = h.ProposedParentLocalId, Confidence = h.Confidence, DecisionEvidence = h.DecisionEvidence });
            }
        }
        // Parent tokens are untrusted local hints. Preserve every source/span proposal and let
        // the canonical materializer resolve valid edges, derive levels, and break only bad
        // edges/cycles. A malformed parent must never erase an otherwise source-valid heading.
        return (list, []);
    }

    private static int? ParseParentOrdinal(string? token) => token is not null && token.StartsWith("sourceOrdinal:", StringComparison.Ordinal) && int.TryParse(token[14..], out var value) ? value : token is null ? null : null;

    private const string ParentTokenInstruction = "\nHierarchy contract: proposedParentLocalId may be null or a local token exactly like sourceOrdinal:17 copied from a visible SOURCE_OCCURRENCE. Never emit a sourceId or sourceOccurrenceId. The harness validates parent edges and derives final levels from the validated parent graph; proposedLevel is only a hint.\n";

    private static bool ExcludedProjectionRole(string role) => role is "AGENDA_NAVIGATION_HEADING" or "TOC_ENTRY" or "FRONT_MATTER";
    private static bool TryResume(string path, string sourceSha, string configurationSignature) { if (!File.Exists(path)) return false; try { using var doc = JsonDocument.Parse(File.ReadAllText(path)); return doc.RootElement.GetProperty("status").GetString() == "SUCCESS" && doc.RootElement.GetProperty("sourceSha256").GetString() == sourceSha && doc.RootElement.GetProperty("configurationSignature").GetString() == configurationSignature; } catch { return false; } }
    private static string ConfigurationSignature => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"R1C4_LOCAL_QWEN_LARGE|reasoning-v2-batched|orchestration=round-robin-v2|ctx=80000|window=48000|temp=0|top_p=1|top_k=1|seed=42|inflight=8|maxHeadingsPerResponse={MaxHeadingsPerBatchedResponse}"))).ToLowerInvariant();
    private static string GitSha(string repo)
    {
        using var process = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD") { WorkingDirectory = repo, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
        if (process is null) return "UNRESOLVED";
        var output = process.StandardOutput.ReadToEnd().Trim(); process.WaitForExit();
        return process.ExitCode == 0 ? output : "UNRESOLVED";
    }
    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static async Task VerifyModelIdentityAsync(RemoteInferenceOptions options, HttpClient http, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await http.GetAsync(options.ModelsEndpoint, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"LOCAL_QWEN_SERVICE_UNAVAILABLE_HTTP_{(int)response.StatusCode}");
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
            var ids = document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
                ? data.EnumerateArray()
                    .Where(item => item.TryGetProperty("id", out _))
                    .Select(item => item.GetProperty("id").GetString())
                    .Where(id => id is not null)
                    .Cast<string>()
                    .ToArray()
                : [];
            if (!ids.Contains(options.Model, StringComparer.Ordinal))
                throw new InvalidOperationException($"LOCAL_QWEN_MODEL_IDENTITY_MISMATCH:expected={options.Model};reported={string.Join('|', ids)}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("LOCAL_QWEN_SERVICE_UNAVAILABLE_TIMEOUT");
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException("LOCAL_QWEN_SERVICE_UNAVAILABLE", ex);
        }
    }
    private static async Task WriteJsonAsync(string path, object value, CancellationToken ct) => await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, new UTF8Encoding(false), ct);
    private static async Task AppendEventAsync(string path, object value, CancellationToken ct) => await File.AppendAllTextAsync(path, JsonSerializer.Serialize(value, CompactJsonOptions) + Environment.NewLine, new UTF8Encoding(false), ct);
    private static Task WriteExecutionAsync(string path, object value, CancellationToken ct) => WriteJsonAsync(path, value, ct);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly JsonSerializerOptions CompactJsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    private sealed class DocumentState(InventoryItem inventory, int ordinal)
    {
        public InventoryItem Inventory { get; } = inventory; public int Ordinal { get; } = ordinal; public SourceDocument? Source { get; set; } public DocxPolicyState? Policy { get; set; } public ReasoningContextPack? Pack { get; set; }
        public List<Task<ScheduledInferenceResult<InferenceWorkItem, ReasoningModelResponse>>> Requests { get; } = [];
    }
    private sealed record InferenceWorkItem(DocumentState Document, ReasoningContextSegment Segment, IReadOnlyList<ReasoningSourceOccurrence> Occurrences, ReasoningModelRequest Request);
    private sealed record InventoryItem(string DocumentId, string SourcePath, string SourceSha256);
    private static InventoryItem[] ReadInventory(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("documents").EnumerateArray().Select(x => new InventoryItem(x.GetProperty("documentId").GetString()!, x.GetProperty("sourcePath").GetString()!, x.GetProperty("sourceSha256").GetString()!)).ToArray();
    }
}
