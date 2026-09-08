using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Features;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Policy;
using DocxHeaderExtractor.Eval.StrictGoldOccurrence;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>Three-document, no-fallback OpenRouter smoke campaign for the Qwen 9B ceiling.</summary>
public static class OpenRouterQwen35CeilingSmokeRunner
{
    private const string OutputRoot = "eval/a99-closed-loop/openrouter-qwen35-9b-ceiling-smoke";
    private const string InventoryPath = "eval/a99-dataset/document-inventory.v1.json";
    private const string Model = "qwen/qwen3.5-9b";
    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";
    private static readonly string[] SelectedIds = ["DOC-0258", "DOC-0205", "DOC-0264"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.Combine(output, "documents"));
        var inventory = ReadInventory(Path.Combine(repoRoot, InventoryPath));
        var selected = SelectedIds.Select(id => inventory.Single(item => item.DocumentId == id)).ToArray();
        Console.WriteLine($"SELECTED_DOCS={string.Join(',', SelectedIds)}");

        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        var options = new RemoteInferenceOptions
        {
            Endpoint = new Uri(Endpoint), Model = Model, ApiKey = key ?? "",
            ContextSize = 32_768, MaxOutputTokens = 16_384, RequestTimeoutSeconds = 180,
            TransientRequestRetries = 2, MaxParallelRequests = 1, SendChatTemplateKwargs = false,
            RequireJsonObjectResponse = true,
        };
        options.Validate();
        var configurationSignature = ConfigurationSignature(options);
        await WriteJsonAsync(Path.Combine(output, "config.v1.json"), new
        {
            schemaVersion = "a99-openrouter-qwen35-9b-smoke-v1",
            requestedModel = Model,
            endpoint = Endpoint,
            provider = "OpenRouter",
            selectedDocuments = SelectedIds,
            apiKeyPresent = !string.IsNullOrWhiteSpace(key),
            modelFallback = "NONE",
            goldReadBeforeFreeze = false,
            sourceFaithfulContext = true,
            configurationSignature,
            startedUtc = DateTimeOffset.UtcNow,
        }, ct);

        if (string.IsNullOrWhiteSpace(key))
            return await WriteBlockedAsync(output, "BLOCKED_OPENROUTER_MODEL_UNAVAILABLE", "OPENROUTER_API_KEY_MISSING", 0, ct);

        using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = Timeout.InfiniteTimeSpan };
        var preflight = await VerifyModelIdentityAsync(options, http, ct);
        if (!preflight.Available)
            return await WriteBlockedAsync(output, preflight.Classification, preflight.Reason, 0, ct);

        using var model = new OpenRouterReasoningSemanticModel(options, http);
        var documents = new List<DocumentMetric>();
        var providerFailure = false;
        foreach (var item in selected)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                documents.Add(await RunDocumentAsync(repoRoot, output, item, model, preflight.ReportedModel ?? Model, configurationSignature, ct));
            }
            catch (Exception ex)
            {
                providerFailure |= IsProviderFailure(ex);
                var docDir = Path.Combine(output, "documents", item.DocumentId);
                Directory.CreateDirectory(docDir);
                await WriteJsonAsync(Path.Combine(docDir, "execution.v1.json"), new
                {
                    documentId = item.DocumentId, sourceSha256 = item.SourceSha256,
                    requestedModel = Model, reportedModel = preflight.ReportedModel ?? Model,
                    configurationSignature, status = providerFailure ? "PROVIDER_OR_SCHEMA_FAILURE" : "DOCUMENT_FAILURE",
                    error = ex.Message, goldReadBeforeFreeze = false,
                }, ct);
                Console.Error.WriteLine($"OPENROUTER_DOCUMENT_FAILURE={item.DocumentId}:{ex.Message}");
            }
        }

        var exact = documents.Where(x => x.exactStatus == "EVALUABLE").ToArray();
        var micro = Score(exact.SelectMany(x => x.GoldKeys).ToArray(), exact.SelectMany(x => x.PredictionKeys).ToArray());
        var classification = providerFailure
            ? "BLOCKED_SCHEMA_OR_PROVIDER_FAILURE"
            : documents.Sum(x => x.systemLossCount) > 0
                ? "OPENROUTER_QWEN9B_SYSTEM_LOSS_REMAINS"
                : documents.Any(x => x.exactStatus == "EVALUABLE" && x.FN > 0)
                    ? "OPENROUTER_QWEN9B_MODEL_GAP_AFTER_SYSTEM_CLEAN"
                    : "OPENROUTER_QWEN9B_CEILING_SMOKE_COMPLETE";
        var telemetry = model.CompletionTelemetry;
        await WriteJsonAsync(Path.Combine(output, "summary.v1.json"), new
        {
            schemaVersion = "a99-openrouter-qwen35-9b-smoke-v1",
            status = classification,
            requestedModel = Model, reportedModel = preflight.ReportedModel ?? Model, provider = "OpenRouter",
            selectedDocuments = SelectedIds, documents,
            microExact = micro,
            openRouterCalls = model.ProviderCalls,
            inputTokens = telemetry.Sum(x => x.ReportedInputTokens ?? 0),
            outputTokens = telemetry.Sum(x => x.ReportedOutputTokens ?? 0),
            costIfAvailable = telemetry.Where(x => x.ReportedCost is not null).Sum(x => x.ReportedCost ?? 0),
            providerTelemetry = telemetry,
            goldReadBeforeFreeze = false,
            completedUtc = DateTimeOffset.UtcNow,
        }, ct);
        Console.WriteLine($"FINAL_CLASSIFICATION={classification}");
        return classification.StartsWith("BLOCKED_", StringComparison.Ordinal) ? 1 : 0;
    }

    private static async Task<DocumentMetric> RunDocumentAsync(
        string repoRoot,
        string output,
        InventoryItem item,
        OpenRouterReasoningSemanticModel model,
        string reportedModel,
        string configurationSignature,
        CancellationToken ct)
    {
        var docDir = Path.Combine(output, "documents", item.DocumentId);
        Directory.CreateDirectory(docDir);
        var sourcePath = Path.Combine(repoRoot, item.SourcePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(sourcePath) || !string.Equals(Sha256(sourcePath), item.SourceSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SOURCE_HASH_MISMATCH");

        var source = new OpenXmlDocumentSource().Read(sourcePath) with { DocumentId = item.DocumentId };
        var features = NumberingStyleFeatures.FromSourceDocument(source);
        var derived = new DocumentFeatureDeriver().Derive(source);
        var policy = DocxPolicyStateBuilder.Build(source, features, derived, new PipelineOptions { DisableLlm = false }.Extraction);
        var pack = ReasoningContextBuilder.Build(source, policy, 80_000, 48_000, expandOwnedPerOccurrence: false);
        var occurrenceById = pack.Occurrences.ToDictionary(x => x.SourceOccurrenceId, StringComparer.Ordinal);
        var proposals = new List<ReasoningHeadingProposal>();
        var rawProposalCount = 0;
        var spanErrorCount = 0;
        var semanticCalls = 0;
        var stopwatch = Stopwatch.StartNew();

        foreach (var segment in pack.Segments)
        {
            var owned = segment.OwnedSourceOccurrenceIds.Select(id => occurrenceById[id]).ToArray();
            var scopes = owned.Select((occurrence, index) => ScopeForSegment(segment, occurrence, index)).ToArray();
            if (scopes.Length == 0) continue;
            var request = new ReasoningModelRequest
            {
                RequestId = ReasoningPrompt.BuildRequestId(source.DocumentId, segment.ContextSegmentId, configurationSignature),
                DocumentId = source.DocumentId, Route = ReasoningRoute.ModelCapabilityCeiling.ToString(),
                SemanticPassId = "semantic-heading-extraction-v2-batched", ContextSegmentId = segment.ContextSegmentId,
                SystemPrompt = ReasoningPrompt.System + ReasoningPrompt.BatchedInstruction,
                UserPrompt = ReasoningPrompt.BuildUserBatched(segment, false, scopes),
                SourceOccurrenceIds = segment.SourceOccurrenceIds, OwnedSourceOccurrenceIds = segment.OwnedSourceOccurrenceIds,
                OwnedOutputScope = scopes[0], OwnedOutputScopes = scopes,
                AttemptId = $"{source.DocumentId}:{segment.ContextSegmentId}:attempt-1",
                ConfigurationSignature = configurationSignature,
            };
            var response = await CompleteWithRetryAsync(model, request, ct);
            semanticCalls++;
            rawProposalCount += response.Headings.Count;
            foreach (var heading in response.Headings)
            {
                var index = heading.OwnedIndex ?? (owned.Length == 1 ? 0 : -1);
                if (index < 0 || index >= owned.Length) throw new FormatException("MODEL_RESPONSE_INVALID_OWNED_INDEX");
                var occurrence = owned[index];
                var scope = scopes[index];
                var globalStart = scope.VisibleStart + heading.Start;
                var globalEnd = scope.VisibleStart + heading.End;
                if (globalStart < scope.VisibleStart || globalStart >= globalEnd || globalEnd > scope.VisibleEnd ||
                    globalEnd > scope.RawTextLength || globalStart < scope.OwnedStart || globalStart >= scope.OwnedEnd)
                {
                    spanErrorCount++;
                    continue;
                }
                proposals.Add(new ReasoningHeadingProposal
                {
                    SourceId = occurrence.SourceId,
                    HeadingSpan = new StructuralSpan(globalStart, globalEnd),
                    Text = occurrence.RawText[globalStart..globalEnd],
                    SemanticRole = heading.SemanticRole,
                    ProposedLevel = heading.ProposedLevel,
                    ProposedParent = heading.ProposedParentLocalId,
                    Confidence = heading.Confidence,
                    DecisionEvidence = heading.DecisionEvidence,
                });
            }
        }

        var hierarchyCalls = 0;
        var hierarchyValidation = new ReasoningHierarchyValidation([], [], []);
        if (proposals.Count > 0)
        {
            var inventory = ReasoningGlobalHierarchyPass.BuildInventory(source.DocumentId, source, proposals);
            var request = new ReasoningHierarchyModelRequest(
                source.DocumentId, ReasoningRoute.ModelCapabilityCeiling.ToString(),
                $"{source.DocumentId}:global-hierarchy", inventory, ReasoningHierarchyPrompt.System,
                ReasoningHierarchyPrompt.BuildUser(inventory), configurationSignature);
            var hierarchy = await CompleteHierarchyWithRetryAsync(model, request, ct);
            hierarchyCalls++;
            hierarchyValidation = ReasoningGlobalHierarchyPass.Validate(inventory, hierarchy.Edges);
            var levels = ReasoningGlobalHierarchyPass.DeriveLevels(inventory, hierarchyValidation.AcceptedEdges);
            var byGlobalId = proposals.ToDictionary(p => ReasoningGlobalHierarchyPass.ProposalId(source.DocumentId, p.SourceId, p.HeadingSpan), StringComparer.Ordinal);
            var parents = hierarchyValidation.AcceptedEdges.ToDictionary(
                edge => edge.ChildProposalId,
                edge => edge.ParentProposalId is not null && byGlobalId.TryGetValue(edge.ParentProposalId, out var parent)
                    ? ReasoningProposalMaterializer.ElementId(parent) : null,
                StringComparer.Ordinal);
            proposals = proposals.Select(proposal =>
            {
                var id = ReasoningGlobalHierarchyPass.ProposalId(source.DocumentId, proposal.SourceId, proposal.HeadingSpan);
                return proposal with { ProposedParent = parents.GetValueOrDefault(id), ProposedLevel = levels.GetValueOrDefault(id, 1) };
            }).ToList();
        }

        var materialized = ReasoningProposalMaterializer.Materialize(source, policy, proposals);
        var structureProjection = ReasoningTaskProjection.Project(materialized.Structure);
        var structureProjectionById = structureProjection.ToDictionary(x => x.ProposalId, StringComparer.Ordinal);
        var projection = materialized.Validated.Select(row => structureProjectionById.GetValueOrDefault(row.ElementId) ??
            new ReasoningProjectionDecision(row.ElementId, ReasoningTaskProjection.Excluded, "VALIDATION_REJECTED")).ToArray();
        var finalElements = ReasoningTaskProjection.ProjectContentHeadings(materialized.Structure)
            .OrderBy(x => x.Sources.Single().SourceOrdinal).ThenBy(x => x.Sources.Single().Span.Start).ThenBy(x => x.Id, StringComparer.Ordinal)
            .ToArray();
        var predictionKeys = finalElements.Select(Key).ToArray();
        var predictionPath = Path.Combine(docDir, "prediction.v1.json");
        await WriteJsonAsync(predictionPath, new
        {
            documentId = item.DocumentId, sourceSha256 = item.SourceSha256, requestedModel = Model,
            reportedModel, sourceCharacters = pack.SourceCharacters, segments = pack.Segments.Count,
            semanticCalls, hierarchyCalls, rawProposalCount, boundProposalCount = proposals.Count, spanErrorCount,
            validatedSemanticCount = materialized.Validated.Count(x => x.Accepted), proposals, projection,
            hierarchyValidation, goldReadBeforeFreeze = false,
        }, ct);
        var resultPath = Path.Combine(docDir, "result.v1.json");
        await WriteJsonAsync(resultPath, new { documentId = item.DocumentId, status = "SUCCESS", headings = finalElements, goldReadBeforeFreeze = false }, ct);
        var predictionHash = Sha256(predictionPath);
        var resultHash = Sha256(resultPath);
        var freezePath = Path.Combine(docDir, "freeze.v1.json");
        await WriteJsonAsync(freezePath, new
        {
            documentId = item.DocumentId, sourceSha256 = item.SourceSha256, predictionSha256 = predictionHash,
            resultSha256 = resultHash, configurationSignature, requestedModel = Model, reportedModel,
            runtimeContractVersion = ReasoningPrompt.Version, goldReadBeforeFreeze = false, frozenUtc = DateTimeOffset.UtcNow,
        }, ct);

        var eligibility = ReasoningGoldEligibilityEvaluator.Evaluate(repoRoot, item.DocumentId);
        var goldKeys = Array.Empty<string>();
        var tp = 0; var fp = 0; var fn = 0; var exactStatus = "NOT_EVALUABLE"; var systemLoss = 0;
        var lossCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        if (eligibility.Eligible)
        {
            var goldPath = Path.Combine(repoRoot, "eval", "a99-closed-loop", "strict-gold-occurrence-v1", $"{item.DocumentId}.occurrence-gold-v1.json");
            var gold = ReasoningGoldArtifactLoader.LoadOccurrence(goldPath);
            goldKeys = gold.Where(x => x.HeadingSpan is not null).Select(x => Key(x.SourceId, x.HeadingSpan!)).ToArray();
            var score = Score(goldKeys, predictionKeys);
            tp = score.TP; fp = score.FP; fn = score.FN; exactStatus = "EVALUABLE";
            foreach (var loss in ClassifyLosses(source, gold, proposals, materialized, finalElements, projection))
                lossCounts[loss] = lossCounts.GetValueOrDefault(loss) + 1;
            systemLoss = lossCounts.Where(x => x.Key.StartsWith("SYSTEM_", StringComparison.Ordinal)).Sum(x => x.Value);
            await WriteJsonAsync(Path.Combine(docDir, "score.v1.json"), new
            {
                documentId = item.DocumentId, exactStatus, goldCount = goldKeys.Length, tp, fp, fn,
                precision = score.P, recall = score.R, f1 = score.F1, lossCounts, systemLossCount = systemLoss,
                goldReadBeforeFreeze = false,
            }, ct);
            await WriteTraceAsync(docDir, source, gold, proposals, materialized, finalElements, projection, ct);
        }
        else
        {
            var occurrencePath = Path.Combine(repoRoot, "eval", "a99-closed-loop", "strict-gold-occurrence-v1", $"{item.DocumentId}.occurrence-gold-v1.json");
            var semanticTotal = File.Exists(occurrencePath) ? JsonDocument.Parse(File.ReadAllText(occurrencePath)).RootElement.GetProperty("semanticHeadingTotal").GetInt32() : 0;
            await WriteJsonAsync(Path.Combine(docDir, "semantic-evaluation.v1.json"), new
            {
                documentId = item.DocumentId, exactStatus, eligibilityReason = eligibility.Reason,
                semanticHeadingTotal = semanticTotal, rawProposalCount, boundProposalCount = proposals.Count, spanErrorCount,
                finalHeadingCount = finalElements.Length, systemLossCount = 0, goldReadBeforeFreeze = false,
            }, ct);
            await WriteTraceAsync(docDir, source, [], proposals, materialized, finalElements, projection, ct);
        }
        stopwatch.Stop();
        var telemetry = model.CompletionTelemetry;
        var docTelemetry = telemetry.Where(x => string.Equals(x.DocumentId, source.DocumentId, StringComparison.Ordinal)).ToArray();
        var metric = new DocumentMetric(item.DocumentId, pack.SourceCharacters, pack.Segments.Count, semanticCalls, hierarchyCalls,
            docTelemetry.Length, docTelemetry.Sum(x => x.ReportedInputTokens ?? 0), docTelemetry.Sum(x => x.ReportedOutputTokens ?? 0),
            stopwatch.ElapsedMilliseconds, rawProposalCount, proposals.Count, materialized.Validated.Count(x => x.Accepted),
            finalElements.Length, systemLoss, exactStatus, tp, fp, fn, Score(goldKeys, predictionKeys).P, Score(goldKeys, predictionKeys).R,
            Score(goldKeys, predictionKeys).F1, goldKeys, predictionKeys);
        await WriteJsonAsync(Path.Combine(docDir, "execution.v1.json"), metric with { sourceSha256 = item.SourceSha256, status = "SUCCESS", configurationSignature = configurationSignature, requestedModel = Model, reportedModel = reportedModel, freezePath = freezePath, goldReadBeforeFreeze = false }, ct);
        return metric;
    }

    private static async Task WriteTraceAsync(string docDir, SourceDocument source, IReadOnlyList<ReasoningGoldOccurrence> gold,
        IReadOnlyList<ReasoningHeadingProposal> proposals, (ValidatedStructure Structure, IReadOnlyList<ReasoningValidatedProposal> Validated) materialized,
        IReadOnlyList<ValidatedStructuralElement> finalElements, IReadOnlyList<ReasoningProjectionDecision> projection, CancellationToken ct)
    {
        var validated = materialized.Validated.ToDictionary(x => x.ElementId, StringComparer.Ordinal);
        var included = finalElements.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var projectionById = projection.ToDictionary(x => x.ProposalId, StringComparer.Ordinal);
        var traces = proposals.Select(proposal =>
        {
            var id = ReasoningProposalMaterializer.ElementId(proposal);
            var row = validated.GetValueOrDefault(id);
            return ReasoningFirstLossTraceBuilder.Build(source.DocumentId, proposal, true, true, true, true, true, row?.Accepted == true, true, row?.Accepted == true,
                projectionById.GetValueOrDefault(id) ?? new ReasoningProjectionDecision(id, ReasoningTaskProjection.Excluded, "VALIDATION_REJECTED"), included.Contains(id));
        }).ToList();
        var predicted = finalElements.Select(Key).ToHashSet(StringComparer.Ordinal);
        foreach (var missing in gold.Where(item => item.HeadingSpan is not null && !predicted.Contains(Key(item.SourceId, item.HeadingSpan!))))
        {
            var proposal = new ReasoningHeadingProposal
            {
                SourceId = missing.SourceId, HeadingSpan = missing.HeadingSpan!, Text = missing.ExactText,
                SemanticRole = "CONTENT_HEADING", Confidence = 1,
            };
            var id = ReasoningProposalMaterializer.ElementId(proposal);
            traces.Add(ReasoningFirstLossTraceBuilder.Build(source.DocumentId, proposal, true, true, false, false, true, false, true, false,
                new ReasoningProjectionDecision(id, ReasoningTaskProjection.Excluded, "MODEL_OMISSION"), false));
        }
        await WriteJsonAsync(Path.Combine(docDir, "trace.v1.json"), new { goldReadBeforeFreeze = false, traces }, ct);
    }

    private static async Task<ReasoningModelResponse> CompleteWithRetryAsync(OpenRouterReasoningSemanticModel model, ReasoningModelRequest request, CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try { return await model.CompleteAsync(request with { AttemptId = $"{request.RequestId}:attempt-{attempt}" }, TimeSpan.FromSeconds(180), ct).ConfigureAwait(false); }
            catch (Exception ex) when (attempt < 3 && ex is ReasoningCompletionException or HttpRequestException)
            { last = ex; await Task.Delay(250, ct).ConfigureAwait(false); }
        }
        throw last ?? new InvalidOperationException("OPENROUTER_SEMANTIC_FAILURE");
    }

    private static async Task<ReasoningHierarchyModelResponse> CompleteHierarchyWithRetryAsync(OpenRouterReasoningSemanticModel model, ReasoningHierarchyModelRequest request, CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try { return await model.CompleteHierarchyAsync(request, ct).ConfigureAwait(false); }
            catch (Exception ex) when (attempt < 3 && ex is ReasoningCompletionException or HttpRequestException or FormatException)
            { last = ex; await Task.Delay(250, ct).ConfigureAwait(false); }
        }
        throw last ?? new InvalidOperationException("OPENROUTER_HIERARCHY_FAILURE");
    }

    private static IEnumerable<string> ClassifyLosses(SourceDocument source, IReadOnlyList<ReasoningGoldOccurrence> gold,
        IReadOnlyList<ReasoningHeadingProposal> proposals, (ValidatedStructure Structure, IReadOnlyList<ReasoningValidatedProposal> Validated) materialized,
        IReadOnlyList<ValidatedStructuralElement> finalElements, IReadOnlyList<ReasoningProjectionDecision> projectionById)
    {
        var predicted = finalElements.Select(Key).ToHashSet(StringComparer.Ordinal);
        var raw = proposals.ToArray();
        var rows = materialized.Validated.ToDictionary(x => x.ElementId, StringComparer.Ordinal);
        foreach (var item in gold.Where(x => x.HeadingSpan is not null))
        {
            var key = Key(item.SourceId, item.HeadingSpan!);
            if (predicted.Contains(key)) continue;
            var exact = raw.FirstOrDefault(p => Key(p) == key);
            if (exact is null)
            {
                var near = raw.Any(p => p.SourceId == item.SourceId && (p.HeadingSpan.Start == item.HeadingSpan!.Start || p.Text.Contains(item.ExactText, StringComparison.Ordinal)));
                yield return near ? "MODEL_SPAN_ERROR" : "MODEL_OMISSION";
                continue;
            }
            var id = ReasoningProposalMaterializer.ElementId(exact);
            if (!rows.TryGetValue(id, out var row) || !row.Accepted) yield return "SYSTEM_VALIDATOR_LOSS";
            else if (projectionById.Single(x => x.ProposalId == id).Status == ReasoningTaskProjection.Excluded) yield return "SYSTEM_PROJECTION_LOSS";
            else yield return "SYSTEM_HIERARCHY_LOSS";
        }
        foreach (var element in finalElements)
        {
            var key = Key(element);
            if (gold.Any(item => item.HeadingSpan is not null && Key(item.SourceId, item.HeadingSpan!) == key)) continue;
            yield return raw.Any(p => Key(p) == key) ? "MODEL_FALSE_POSITIVE" : "BINDING_ERROR";
        }
    }

    private static ReasoningOwnedOutputScope ScopeForSegment(ReasoningContextSegment segment, ReasoningSourceOccurrence occurrence, int index) => new()
    {
        CanonicalSourceId = occurrence.SourceId, SourceOccurrenceId = occurrence.SourceOccurrenceId, RawTextLength = occurrence.RawText.Length,
        VisibleStart = segment.VisibleStartCharacter ?? 0, VisibleEnd = segment.VisibleEndCharacter ?? occurrence.RawText.Length,
        OwnedStart = segment.OwnedSourceOccurrenceId == occurrence.SourceOccurrenceId ? segment.OwnedStartCharacter ?? 0 : 0,
        OwnedEnd = segment.OwnedSourceOccurrenceId == occurrence.SourceOccurrenceId ? segment.OwnedEndCharacter ?? occurrence.RawText.Length : occurrence.RawText.Length,
        OwnedIndex = index,
    };

    private static string Key(ReasoningHeadingProposal p) => Key(p.SourceId, p.HeadingSpan);
    private static string Key(ValidatedStructuralElement e) => Key(e.Sources.Single().SourceId, e.Sources.Single().Span);
    private static string Key(string sourceId, StructuralSpan span) => $"{sourceId}:{span.Start}:{span.End}";
    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string ConfigurationSignature(RemoteInferenceOptions o) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"A99_OPENROUTER_QWEN35_9B|{o.Endpoint}|{o.Model}|{ReasoningPrompt.Version}|temperature=0|reasoning=none|fallbacks=false|batched=true"))).ToLowerInvariant();
    private static async Task WriteJsonAsync(string path, object value, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine);
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, FileOptions.SequentialScan);
        await stream.WriteAsync(bytes, ct); await stream.FlushAsync(ct); stream.Flush(true);
    }

    private static async Task<int> WriteBlockedAsync(string output, string classification, string reason, int calls, CancellationToken ct)
    {
        await WriteJsonAsync(Path.Combine(output, "summary.v1.json"), new { schemaVersion = "a99-openrouter-qwen35-9b-smoke-v1", status = classification, reason, requestedModel = Model, provider = "OpenRouter", selectedDocuments = SelectedIds, openRouterCalls = calls, inputTokens = 0, outputTokens = 0, costIfAvailable = (decimal?)null, goldReadBeforeFreeze = false }, ct);
        Console.WriteLine($"FINAL_CLASSIFICATION={classification}");
        return 1;
    }

    private static async Task<(bool Available, string Classification, string Reason, string? ReportedModel)> VerifyModelIdentityAsync(RemoteInferenceOptions options, HttpClient http, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var request = new HttpRequestMessage(HttpMethod.Get, options.ModelsEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
            using var response = await http.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode) return (false, "BLOCKED_SCHEMA_OR_PROVIDER_FAILURE", $"OPENROUTER_MODELS_HTTP_{(int)response.StatusCode}", null);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            var ids = document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
                ? data.EnumerateArray().Where(x => x.TryGetProperty("id", out _)).Select(x => x.GetProperty("id").GetString()).Where(x => x is not null).Cast<string>().ToArray()
                : [];
            return ids.Contains(Model, StringComparer.Ordinal)
                ? (true, "", "MODEL_AVAILABLE", Model)
                : (false, "BLOCKED_OPENROUTER_MODEL_UNAVAILABLE", "MODEL_IDENTITY_MISMATCH", null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return (false, "BLOCKED_OPENROUTER_MODEL_UNAVAILABLE", "MODEL_PREFLIGHT_TIMEOUT", null); }
        catch (HttpRequestException ex) { return (false, "BLOCKED_SCHEMA_OR_PROVIDER_FAILURE", ex.GetType().Name, null); }
        catch (JsonException) { return (false, "BLOCKED_SCHEMA_OR_PROVIDER_FAILURE", "MODEL_METADATA_SCHEMA_INVALID", null); }
    }

    private static bool IsProviderFailure(Exception ex) => ex is ReasoningCompletionException or HttpRequestException or JsonException or FormatException || ex.Message.Contains("OPENROUTER", StringComparison.OrdinalIgnoreCase);

    private static ScoreResult Score(IReadOnlyList<string> gold, IReadOnlyList<string> predicted)
    {
        var g = gold.ToHashSet(StringComparer.Ordinal); var p = predicted.ToHashSet(StringComparer.Ordinal); var tp = g.Intersect(p).Count(); var fp = p.Except(g).Count(); var fn = g.Except(p).Count();
        var precision = tp + fp == 0 ? 0d : (double)tp / (tp + fp); var recall = tp + fn == 0 ? 0d : (double)tp / (tp + fn); var f1 = precision + recall == 0 ? 0d : 2 * precision * recall / (precision + recall);
        return new ScoreResult(tp, fp, fn, precision, recall, f1);
    }

    private sealed record InventoryItem(string DocumentId, string SourcePath, string SourceSha256);
    private sealed record ScoreResult(int TP, int FP, int FN, double P, double R, double F1);
    private sealed record DocumentMetric(string documentId, int sourceCharacters, int segments, int semanticCalls, int hierarchyCalls, int totalModelCalls, int inputTokens, int outputTokens, long wallTime, int rawProposalCount, int boundProposalCount, int validatedSemanticCount, int finalHeadingCount, int systemLossCount, string exactStatus, int TP, int FP, int FN, double P, double R, double F1, IReadOnlyList<string> GoldKeys, IReadOnlyList<string> PredictionKeys)
    {
        public string? sourceSha256 { get; init; }
        public string? status { get; init; }
        public string? configurationSignature { get; init; }
        public string? requestedModel { get; init; }
        public string? reportedModel { get; init; }
        public string? freezePath { get; init; }
        public bool goldReadBeforeFreeze { get; init; }
    }

    private static InventoryItem[] ReadInventory(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("documents").EnumerateArray().Select(x => new InventoryItem(x.GetProperty("documentId").GetString()!, x.GetProperty("sourcePath").GetString()!, x.GetProperty("sourceSha256").GetString()!)).ToArray();
    }
}
