using System.Diagnostics;
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

/// <summary>
/// OpenRouter single-pass ceiling campaign: optimized compact request packet, provider-resolved
/// context/reasoning capability, semantic extraction, compact global hierarchy, canonical
/// projection, freeze, then Gold. The default Qwen3.5-9B campaign and the Qwen3.7 Flash control
/// use separate settings and artifact roots; neither overwrites the other.
/// </summary>
public static class OpenRouterQwen9BTrueCeilingRunner
{
    private const string DefaultOutputRoot = "eval/a99-closed-loop/openrouter-qwen35-9b-true-ceiling";
    private const string InventoryPath = "eval/a99-dataset/document-inventory.v1.json";
    private const string DefaultModel = "qwen/qwen3.5-9b";
    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";
    private static readonly string[] DefaultSelectedIds = ["DOC-0258", "DOC-0205", "DOC-0264"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static Task<int> RunAsync(string repoRoot, CancellationToken ct = default) =>
        RunAsync(repoRoot, new RunSettings(DefaultModel, DefaultOutputRoot, DefaultSelectedIds,
            "a99-openrouter-qwen35-9b-true-ceiling-v1", "QWEN9B_CEILING_ON_SELECTED_DEV"), ct);

    public static Task<int> RunFlashControlAsync(string repoRoot, CancellationToken ct = default) =>
        RunAsync(repoRoot, new RunSettings("qwen/qwen3.7-flash", "eval/a99-closed-loop/qwen37-flash-control", ["DOC-0205", "DOC-0258"],
            "a99-qwen37-flash-control-v1", "QWEN37_FLASH_CLEAN_SINGLE_PASS"), ct);

    private static async Task<int> RunAsync(string repoRoot, RunSettings settings, CancellationToken ct)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Path.Combine(repoRoot, settings.OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.Combine(output, "documents"));
        var inventory = ReadInventory(Path.Combine(repoRoot, InventoryPath));
        var selected = settings.SelectedIds.Select(id => inventory.Single(item => item.DocumentId == id)).ToArray();
        Console.WriteLine($"SELECTED_DOCS={string.Join(',', settings.SelectedIds)}");

        // Section 17: offline packet-overhead gate. Runs unconditionally, no model calls.
        var overheadGate = await RunPacketOverheadGateAsync(repoRoot, output, selected, ct);

        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        var options = new RemoteInferenceOptions
        {
            Endpoint = new Uri(Endpoint), Model = settings.Model, ApiKey = key ?? "",
            ContextSize = 262_144, MaxOutputTokens = 48_000, RequestTimeoutSeconds = 600,
            TransientRequestRetries = 2, MaxParallelRequests = 1, SendChatTemplateKwargs = false,
        };

        if (string.IsNullOrWhiteSpace(key))
            return await WriteBlockedAsync(output, settings, "BLOCKED_OPENROUTER_MODEL_UNAVAILABLE", "OPENROUTER_API_KEY_MISSING", overheadGate, ct);

        using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = Timeout.InfiniteTimeSpan };
        var preflight = await OpenRouterModelCapabilityResolver.ResolveAsync(options, http, ct);
        Console.WriteLine($"MODEL={settings.Model}");
        if (preflight.Capability is { } cap)
        {
            Console.WriteLine($"CONTEXT_LENGTH={cap.ContextLength}");
            Console.WriteLine($"REASONING_SUPPORTED={cap.ReasoningSupported}");
            Console.WriteLine($"SUPPORTED_EFFORTS={string.Join(',', cap.SupportedReasoningEfforts)}");
            Console.WriteLine($"SELECTED_REASONING_EFFORT={cap.SelectedReasoningEffort}");
            Console.WriteLine($"STRUCTURED_OUTPUT_SUPPORTED={cap.StructuredOutputSupported}");
            Console.WriteLine($"MAX_COMPLETION_IF_KNOWN={cap.MaxCompletionTokens?.ToString() ?? "unknown"}");
        }
        await WriteJsonAsync(Path.Combine(output, "config.v1.json"), new
        {
            schemaVersion = settings.SchemaVersion,
            requestedModel = settings.Model, endpoint = Endpoint, provider = "OpenRouter",
            selectedDocuments = settings.SelectedIds, apiKeyPresent = true, modelFallback = "NONE",
            goldReadBeforeFreeze = false, sourceFaithfulContext = true,
            capability = preflight.Capability, preflightAvailable = preflight.Available,
            preflightClassification = preflight.Classification, preflightReason = preflight.Reason,
            packetOverheadGate = overheadGate,
            resultLabel = settings.ResultLabel,
            startedUtc = DateTimeOffset.UtcNow,
        }, ct);

        if (!preflight.Available || preflight.Capability is null)
            return await WriteBlockedAsync(output, settings, preflight.Classification, preflight.Reason, overheadGate, ct);

        if (!preflight.Capability.ReasoningSupported)
            return await WriteBlockedAsync(output, settings, "BLOCKED_PROVIDER_CAPABILITY_MISMATCH", "REASONING_NOT_REPORTED_SUPPORTED", overheadGate, ct);

        // Fail closed rather than silently accepting a provider substitution: the resolved
        // capability must be for exactly the requested model id.
        if (!string.Equals(preflight.Capability.ModelId, settings.Model, StringComparison.Ordinal))
            return await WriteBlockedAsync(output, settings, "BLOCKED_PROVIDER_CAPABILITY_MISMATCH", "MODEL_IDENTITY_MISMATCH", overheadGate, ct);

        using var liveLease = await A99OpenRouterLiveProviderLease.AcquireAsync(repoRoot, settings.ResultLabel, string.Join(',', settings.SelectedIds), ct);
        Console.WriteLine($"LIVE_PROVIDER_LOCK=acquired concurrentCampaignsDetected={liveLease.ConcurrentCampaignsDetected} providerConcurrency={liveLease.ProviderConcurrency}");
        using var model = new OpenRouterCeilingReasoningModel(options, preflight.Capability, http);
        var documents = new List<DocumentMetric>();
        var providerFailure = false;
        foreach (var item in selected)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                documents.Add(await RunDocumentAsync(repoRoot, output, item, model, settings.Model, ct));
            }
            catch (Exception ex)
            {
                providerFailure |= IsProviderFailure(ex);
                var docDir = Path.Combine(output, "documents", item.DocumentId);
                Directory.CreateDirectory(docDir);
                await WriteJsonAsync(Path.Combine(docDir, "execution.v1.json"), new
                {
                    documentId = item.DocumentId, sourceSha256 = item.SourceSha256, requestedModel = settings.Model,
                    status = "DOCUMENT_FAILURE", error = ex.Message, goldReadBeforeFreeze = false,
                }, ct);
                Console.Error.WriteLine($"CEILING_DOCUMENT_FAILURE={item.DocumentId}:{ex.Message}");
            }
        }

        var exact = documents.Where(x => x.exactStatus == "EVALUABLE").ToArray();
        var micro = Score(exact.SelectMany(x => x.GoldKeys).ToArray(), exact.SelectMany(x => x.PredictionKeys).ToArray());
        var systemLossTotal = documents.Sum(x => x.systemLossCount);
        // A run where any selected document failed to complete (provider failure, cancellation,
        // or any other execution fault) can never be reported as a ceiling result -- an empty or
        // partial `documents` list must not silently read as "zero system loss".
        var incompleteRun = documents.Count < selected.Length;
        var classification = providerFailure || incompleteRun
            ? "BLOCKED_OPENROUTER_MODEL_UNAVAILABLE"
            : systemLossTotal > 0
                ? "QWEN9B_CEILING_IMPROVED_SYSTEM_LOSS_REMAINS"
                : documents.Any(x => x.exactStatus == "EVALUABLE" && x.FN > 0)
                    ? "QWEN9B_MODEL_GAP_AFTER_SYSTEM_CLEAN"
                    // Not gated on any F1 threshold: zero system-induced loss IS the valid ceiling
                    // result, even when genuine model errors remain.
                    : "QWEN9B_TRUE_CEILING_EXPOSED";
        await WriteJsonAsync(Path.Combine(output, "summary.v1.json"), new
        {
            schemaVersion = settings.SchemaVersion,
            status = classification, resultLabel = settings.ResultLabel,
            requestedModel = settings.Model, provider = "OpenRouter", selectedDocuments = settings.SelectedIds,
            documents, microExact = micro, systemInducedLossTotal = systemLossTotal,
            openRouterCalls = model.ProviderCalls,
            inputTokens = model.Telemetry.Sum(x => x.ReportedInputTokens ?? 0),
            outputTokens = model.Telemetry.Sum(x => x.ReportedOutputTokens ?? 0),
            reasoningTokens = model.Telemetry.Sum(x => x.ReportedReasoningTokens ?? 0),
            capability = preflight.Capability, requestTelemetry = model.Telemetry,
            packetOverheadGate = overheadGate, goldReadBeforeFreeze = false,
            completedUtc = DateTimeOffset.UtcNow,
        }, ct);
        Console.WriteLine($"FINAL_CLASSIFICATION={classification}");
        return classification.StartsWith("BLOCKED_", StringComparison.Ordinal) ? 1 : 0;
    }

    private static async Task<DocumentMetric> RunDocumentAsync(
        string repoRoot, string output, InventoryItem item, OpenRouterCeilingReasoningModel model, string modelId, CancellationToken ct)
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

        // Section 4/5: token-aware budget from real provider capability, not a fixed 48K window.
        var semanticBudget = model.MaxPromptTokens(model.SemanticMaxCompletionTokens);
        var maxContextCharacters = (int)(semanticBudget * ReasoningTokenBudget.CharactersPerToken);
        var windowCharacters = Math.Max(4_000, maxContextCharacters / 2);
        var pack = ReasoningContextBuilder.Build(source, policy, Math.Max(4_000, maxContextCharacters), windowCharacters, expandOwnedPerOccurrence: false);
        var occurrenceById = pack.Occurrences.ToDictionary(x => x.SourceOccurrenceId, StringComparer.Ordinal);

        var configurationSignature = ConfigurationSignature(model.Capability, modelId);
        var proposals = new List<ReasoningHeadingProposal>();
        var rawProposalCount = 0;
        var spanErrorCount = 0;
        var semanticCalls = 0;
        var stopwatch = Stopwatch.StartNew();

        foreach (var segment in pack.Segments)
        {
            var visibleIds = segment.SourceOccurrenceIds;
            if (visibleIds.Count == 0) continue;
            var ownedSet = segment.OwnedSourceOccurrenceIds.ToHashSet(StringComparer.Ordinal);
            var visibleWindow = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
            var ownedWindow = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
            foreach (var id in visibleIds)
            {
                var occurrence = occurrenceById[id];
                var visible = segment.VisibleStartCharacter is { } vs && segment.VisibleEndCharacter is { } ve && segment.OwnedSourceOccurrenceId == id
                    ? (vs, ve) : (0, occurrence.RawText.Length);
                visibleWindow[id] = visible;
                ownedWindow[id] = ownedSet.Contains(id)
                    ? (segment.OwnedSourceOccurrenceId == id && segment.OwnedStartCharacter is { } os && segment.OwnedEndCharacter is { } oe
                        ? (os, oe) : (0, occurrence.RawText.Length))
                    : (visible.Item1, visible.Item1);
            }

            var visibleOccurrences = visibleIds.Select(id => occurrenceById[id]).ToArray();
            var packetResult = CeilingPacketBuilder.Build(visibleOccurrences, ownedSet, visibleWindow, ownedWindow);
            var requestId = $"{CeilingSemanticPrompt.ProtocolVersion}:{source.DocumentId}:{segment.ContextSegmentId}:{configurationSignature}";
            var (response, _) = await CompleteSemanticWithRetryAsync(model, source.DocumentId, ReasoningRoute.ModelCapabilityCeiling.ToString(),
                requestId, packetResult, ownedSet.Count, visibleIds.Count, ct);
            semanticCalls++;
            rawProposalCount += response.Headings.Count;

            foreach (var heading in response.Headings)
            {
                if (heading.I < 0 || heading.I >= packetResult.Bindings.Count) { spanErrorCount++; continue; }
                var binding = packetResult.Bindings[heading.I];
                if (!ownedSet.Contains(binding.SourceOccurrenceId)) { spanErrorCount++; continue; }
                if (!binding.TryBind(heading.Start, heading.End, out var globalStart, out var globalEnd, out var owned) || !owned)
                {
                    spanErrorCount++; continue;
                }
                var occurrence = occurrenceById[binding.SourceOccurrenceId];
                proposals.Add(new ReasoningHeadingProposal
                {
                    SourceId = occurrence.SourceId,
                    HeadingSpan = new StructuralSpan(globalStart, globalEnd),
                    Text = occurrence.RawText[globalStart..globalEnd],
                    SemanticRole = heading.Role,
                    Confidence = 1,
                });
            }
        }

        var hierarchyCalls = 0;
        var hierarchyValidation = new ReasoningHierarchyValidation([], [], []);
        if (proposals.Count > 0)
        {
            var globalInventory = ReasoningGlobalHierarchyPass.BuildInventory(source.DocumentId, source, proposals);
            var (packetJson, bindings) = CeilingHierarchyPacketBuilder.Build(globalInventory);
            var requestId = $"{CeilingHierarchyPrompt.ProtocolVersion}:{source.DocumentId}:global-hierarchy:{configurationSignature}";
            var response = await CompleteHierarchyWithRetryAsync(model, source.DocumentId, ReasoningRoute.ModelCapabilityCeiling.ToString(),
                requestId, packetJson, globalInventory.Count, ct);
            hierarchyCalls++;
            var edges = response.Parents
                .Select(edge => new ReasoningHierarchyEdge(bindings[edge.Child].ProposalId, edge.Parent is { } p ? bindings[p].ProposalId : null))
                .ToArray();
            hierarchyValidation = ReasoningGlobalHierarchyPass.Validate(globalInventory, edges);
            var levels = ReasoningGlobalHierarchyPass.DeriveLevels(globalInventory, hierarchyValidation.AcceptedEdges);
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
            documentId = item.DocumentId, sourceSha256 = item.SourceSha256, requestedModel = modelId,
            sourceCharacters = pack.SourceCharacters, segments = pack.Segments.Count,
            semanticProtocolVersion = CeilingSemanticPrompt.ProtocolVersion, hierarchyProtocolVersion = CeilingHierarchyPrompt.ProtocolVersion,
            semanticCalls, hierarchyCalls, rawProposalCount, boundProposalCount = proposals.Count, spanErrorCount,
            validatedSemanticCount = materialized.Validated.Count(x => x.Accepted), proposals, projection,
            hierarchyValidation, goldReadBeforeFreeze = false,
        }, ct);
        var resultPath = Path.Combine(docDir, "result.v1.json");
        await WriteJsonAsync(resultPath, new { documentId = item.DocumentId, status = "SUCCESS", headings = finalElements, goldReadBeforeFreeze = false }, ct);

        var docTelemetry = model.Telemetry.Where(x => string.Equals(x.DocumentId, source.DocumentId, StringComparison.Ordinal)).ToArray();
        var runtimeTracePath = Path.Combine(docDir, "runtime-trace.v1.json");
        await WriteJsonAsync(runtimeTracePath, new
        {
            documentId = item.DocumentId, semanticCalls, hierarchyCalls, rawProposalCount, spanErrorCount,
            requests = docTelemetry, goldReadBeforeFreeze = false,
        }, ct);
        var requestMetricsPath = Path.Combine(docDir, "request-metrics.v1.json");
        var sourcePayloadRatioMean = docTelemetry.Length == 0 ? 0 : docTelemetry.Average(x => x.SourcePayloadRatio);
        await WriteJsonAsync(requestMetricsPath, new
        {
            documentId = item.DocumentId,
            semanticCalls, hierarchyCalls,
            inputTokens = docTelemetry.Sum(x => x.ReportedInputTokens ?? 0),
            outputTokens = docTelemetry.Sum(x => x.ReportedOutputTokens ?? 0),
            reasoningTokens = docTelemetry.Sum(x => x.ReportedReasoningTokens ?? 0),
            sourcePayloadRatioMean, goldReadBeforeFreeze = false,
        }, ct);

        var predictionHash = Sha256(predictionPath);
        var resultHash = Sha256(resultPath);
        var runtimeTraceHash = Sha256(runtimeTracePath);
        var requestMetricsHash = Sha256(requestMetricsPath);
        var freezePath = Path.Combine(docDir, "freeze.v1.json");
        await WriteJsonAsync(freezePath, new
        {
            documentId = item.DocumentId, sourceSha256 = item.SourceSha256,
            predictionSha256 = predictionHash, resultSha256 = resultHash,
            runtimeTraceSha256 = runtimeTraceHash, requestMetricsSha256 = requestMetricsHash,
            model = modelId, modelCapabilityDigest = CapabilityDigest(model.Capability),
            semanticProtocolVersion = CeilingSemanticPrompt.ProtocolVersion,
            hierarchyProtocolVersion = CeilingHierarchyPrompt.ProtocolVersion,
            reasoningEffort = model.Capability.SelectedReasoningEffort, contextLength = model.Capability.ContextLength,
            configurationSignature, goldReadBeforeFreeze = false, frozenUtc = DateTimeOffset.UtcNow,
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
            foreach (var loss in ClassifyLosses(gold, proposals, materialized, finalElements, projection))
                lossCounts[loss] = lossCounts.GetValueOrDefault(loss) + 1;
            systemLoss = lossCounts.Where(x => x.Key.StartsWith("SYSTEM_", StringComparison.Ordinal)).Sum(x => x.Value);
            await WriteJsonAsync(Path.Combine(docDir, "score.v1.json"), new
            {
                documentId = item.DocumentId, exactStatus, goldCount = goldKeys.Length, tp, fp, fn,
                precision = score.P, recall = score.R, f1 = score.F1, lossCounts, systemLossCount = systemLoss,
                goldReadBeforeFreeze = false,
            }, ct);
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
        }
        stopwatch.Stop();
        var scoreFinal = Score(goldKeys, predictionKeys);
        var metric = new DocumentMetric(item.DocumentId, pack.SourceCharacters, pack.Segments.Count, semanticCalls, hierarchyCalls,
            docTelemetry.Length, docTelemetry.Sum(x => x.ReportedInputTokens ?? 0), docTelemetry.Sum(x => x.ReportedOutputTokens ?? 0),
            docTelemetry.Sum(x => x.ReportedReasoningTokens ?? 0), stopwatch.ElapsedMilliseconds, rawProposalCount, proposals.Count,
            materialized.Validated.Count(x => x.Accepted), finalElements.Length, systemLoss, exactStatus, tp, fp, fn,
            scoreFinal.P, scoreFinal.R, scoreFinal.F1, goldKeys, predictionKeys);
        await WriteJsonAsync(Path.Combine(docDir, "execution.v1.json"), metric with { sourceSha256 = item.SourceSha256, status = "SUCCESS", freezePath = freezePath, goldReadBeforeFreeze = false }, ct);
        return metric;
    }

    private static async Task<(CeilingSemanticResponse Response, RequestPacketTelemetry Telemetry)> CompleteSemanticWithRetryAsync(
        OpenRouterCeilingReasoningModel model, string documentId, string route, string requestId,
        CeilingPacketResult packet, int ownedOccurrences, int visibleOccurrences, CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                return await model.CompleteSemanticAsync(documentId, route, $"{requestId}:attempt-{attempt}", packet.SerializedJson,
                    packet.SourceTextCharacters, ownedOccurrences, visibleOccurrences, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < 3 && ex is ReasoningCompletionException or HttpRequestException or FormatException)
            {
                last = ex;
                await Task.Delay(250, ct).ConfigureAwait(false);
            }
        }
        throw last ?? new InvalidOperationException("CEILING_SEMANTIC_FAILURE");
    }

    private static async Task<CeilingHierarchyResponse> CompleteHierarchyWithRetryAsync(
        OpenRouterCeilingReasoningModel model, string documentId, string route, string requestId,
        string packetJson, int inventoryCount, CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                var (response, _) = await model.CompleteHierarchyAsync(documentId, route, $"{requestId}:attempt-{attempt}", packetJson, inventoryCount, ct).ConfigureAwait(false);
                // Every heading must appear exactly once as a child -- validate the row COUNT,
                // not just each row's individual validity, before accepting the response.
                var distinctChildren = response.Parents.Select(edge => edge.Child).ToHashSet();
                if (response.Parents.Count != inventoryCount || distinctChildren.Count != inventoryCount)
                    throw new FormatException($"ceiling-hierarchy-response-incomplete:expected={inventoryCount}:got={distinctChildren.Count}");
                return response;
            }
            catch (Exception ex) when (attempt < 3 && ex is ReasoningCompletionException or HttpRequestException or FormatException)
            {
                last = ex;
                await Task.Delay(250, ct).ConfigureAwait(false);
            }
        }
        throw last ?? new InvalidOperationException("CEILING_HIERARCHY_FAILURE");
    }

    /// <summary>Section 17: offline comparison (no model calls) of the OLD verbose per-occurrence
    /// serialization against the NEW compact ceiling packet, for exactly the three selected
    /// documents. Reduction must come from metadata/instruction noise, never from dropping source
    /// text -- source coverage stays 1.0 in both.</summary>
    private static async Task<object[]> RunPacketOverheadGateAsync(string repoRoot, string output, IReadOnlyList<InventoryItem> selected, CancellationToken ct)
    {
        var rows = new List<object>();
        foreach (var item in selected)
        {
            var sourcePath = Path.Combine(repoRoot, item.SourcePath.Replace('/', Path.DirectorySeparatorChar));
            var source = new OpenXmlDocumentSource().Read(sourcePath) with { DocumentId = item.DocumentId };
            var features = NumberingStyleFeatures.FromSourceDocument(source);
            var derived = new DocumentFeatureDeriver().Derive(source);
            var policy = DocxPolicyStateBuilder.Build(source, features, derived, new PipelineOptions { DisableLlm = false }.Extraction);
            var pack = ReasoningContextBuilder.Build(source, policy, int.MaxValue / 2, int.MaxValue / 2, expandOwnedPerOccurrence: false);

            var oldChars = pack.Occurrences.Sum(o => ReasoningContextBuilder.SerializeOccurrence(o).Length);
            var newPacket = CeilingPacketBuilder.Build(pack.Occurrences, pack.Occurrences.Select(o => o.SourceOccurrenceId).ToHashSet(StringComparer.Ordinal));
            var sourceChars = pack.SourceCharacters;
            var oldRatio = oldChars == 0 ? 0 : (double)sourceChars / oldChars;
            var newRatio = newPacket.PacketCharacters == 0 ? 0 : (double)sourceChars / newPacket.PacketCharacters;
            var reduction = oldChars == 0 ? 0 : 1d - (double)newPacket.PacketCharacters / oldChars;
            rows.Add(new
            {
                documentId = item.DocumentId,
                sourceTextChars = sourceChars,
                oldPacketChars = oldChars,
                newPacketChars = newPacket.PacketCharacters,
                reductionPercent = Math.Round(reduction * 100, 2),
                oldSourcePayloadRatio = oldRatio,
                newSourcePayloadRatio = newRatio,
                sourceOccurrenceCoverage = pack.SourceOccurrenceCoverage,
            });
        }
        await WriteJsonAsync(Path.Combine(output, "packet-overhead-gate.v1.json"), new { schemaVersion = "a99-ceiling-packet-overhead-gate-v1", rows }, ct);
        return rows.ToArray();
    }

    private static IEnumerable<string> ClassifyLosses(
        IReadOnlyList<ReasoningGoldOccurrence> gold, IReadOnlyList<ReasoningHeadingProposal> proposals,
        (ValidatedStructure Structure, IReadOnlyList<ReasoningValidatedProposal> Validated) materialized,
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

    private static string Key(ReasoningHeadingProposal p) => Key(p.SourceId, p.HeadingSpan);
    private static string Key(ValidatedStructuralElement e) => Key(e.Sources.Single().SourceId, e.Sources.Single().Span);
    private static string Key(string sourceId, StructuralSpan span) => $"{sourceId}:{span.Start}:{span.End}";
    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Sha256Text(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string ConfigurationSignature(OpenRouterModelCapability capability, string modelId) => Sha256Text(
        $"A99_OPENROUTER_SINGLE_PASS_S0|{modelId}|{CeilingSemanticPrompt.ProtocolVersion}|{CeilingHierarchyPrompt.ProtocolVersion}|temperature=0|reasoning={capability.SelectedReasoningEffort}|exclude=true|fallbacks=false|context={capability.ContextLength}");

    private static string CapabilityDigest(OpenRouterModelCapability capability) => Sha256Text(
        $"{capability.ModelId}|{capability.ContextLength}|{capability.ReasoningSupported}|{string.Join(',', capability.SupportedReasoningEfforts)}|{capability.SelectedReasoningEffort}|{capability.StructuredOutputSupported}|{capability.MaxCompletionTokens}");

    private static async Task WriteJsonAsync(string path, object value, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine);
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, FileOptions.SequentialScan);
        await stream.WriteAsync(bytes, ct); await stream.FlushAsync(ct); stream.Flush(true);
    }

    private static async Task<int> WriteBlockedAsync(string output, RunSettings settings, string classification, string reason, object[] overheadGate, CancellationToken ct)
    {
        await WriteJsonAsync(Path.Combine(output, "summary.v1.json"), new
        {
            schemaVersion = settings.SchemaVersion, status = classification, reason,
            requestedModel = settings.Model, provider = "OpenRouter", selectedDocuments = settings.SelectedIds,
            packetOverheadGate = overheadGate, openRouterCalls = 0, inputTokens = 0, outputTokens = 0,
            goldReadBeforeFreeze = false,
        }, ct);
        Console.WriteLine($"FINAL_CLASSIFICATION={classification}");
        return 1;
    }

    private static bool IsProviderFailure(Exception ex) => ex is ReasoningCompletionException or HttpRequestException or JsonException or FormatException
        or OperationCanceledException or TaskCanceledException
        || ex.Message.Contains("OPENROUTER", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("CEILING", StringComparison.OrdinalIgnoreCase);

    private static ScoreResult Score(IReadOnlyList<string> gold, IReadOnlyList<string> predicted)
    {
        var g = gold.ToHashSet(StringComparer.Ordinal); var p = predicted.ToHashSet(StringComparer.Ordinal);
        var tp = g.Intersect(p).Count(); var fp = p.Except(g).Count(); var fn = g.Except(p).Count();
        var precision = tp + fp == 0 ? 0d : (double)tp / (tp + fp); var recall = tp + fn == 0 ? 0d : (double)tp / (tp + fn);
        var f1 = precision + recall == 0 ? 0d : 2 * precision * recall / (precision + recall);
        return new ScoreResult(tp, fp, fn, precision, recall, f1);
    }

    private sealed record RunSettings(string Model, string OutputRoot, IReadOnlyList<string> SelectedIds,
        string SchemaVersion, string ResultLabel);

    private sealed record InventoryItem(string DocumentId, string SourcePath, string SourceSha256);
    private sealed record ScoreResult(int TP, int FP, int FN, double P, double R, double F1);
    private sealed record DocumentMetric(string documentId, int sourceCharacters, int segments, int semanticCalls, int hierarchyCalls,
        int totalModelCalls, int inputTokens, int outputTokens, int reasoningTokens, long wallTime, int rawProposalCount,
        int boundProposalCount, int validatedSemanticCount, int finalHeadingCount, int systemLossCount, string exactStatus,
        int TP, int FP, int FN, double P, double R, double F1, IReadOnlyList<string> GoldKeys, IReadOnlyList<string> PredictionKeys)
    {
        public string? sourceSha256 { get; init; }
        public string? status { get; init; }
        public string? freezePath { get; init; }
        public bool goldReadBeforeFreeze { get; init; }
    }

    private static InventoryItem[] ReadInventory(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("documents").EnumerateArray()
            .Select(x => new InventoryItem(x.GetProperty("documentId").GetString()!, x.GetProperty("sourcePath").GetString()!, x.GetProperty("sourceSha256").GetString()!))
            .ToArray();
    }
}
