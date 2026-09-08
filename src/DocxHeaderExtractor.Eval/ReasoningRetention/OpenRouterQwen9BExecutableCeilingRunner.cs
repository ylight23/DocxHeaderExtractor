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
/// Executable-reasoning-ceiling recovery campaign. The semantic contract, model, and reasoning
/// mode are exactly the frozen Qwen3.5-9B ceiling route -- this runner changes only EXECUTION
/// SHAPE. A document first gets one bounded full-context attempt (section 2); a workload-shape
/// failure (timeout or provider output limit) never retries that identical giant request -- it
/// steps down a deterministic, capability-derived segment-size ladder (section 5) reusing the
/// existing character-ownership context builder, until a step succeeds or the floor is reached.
/// Exactly DOC-0205 and DOC-0264 -- DOC-0258's frozen FULL_CONTEXT SUCCESS result is untouched
/// and is never rerun here. Writes to its own artifact root so the true-ceiling campaign's
/// frozen evidence is never overwritten.
/// </summary>
public static class OpenRouterQwen9BExecutableCeilingRunner
{
    private const string OutputRoot = "eval/a99-closed-loop/openrouter-qwen35-9b-executable-ceiling";
    private const string InventoryPath = "eval/a99-dataset/document-inventory.v1.json";
    private const string Model = "qwen/qwen3.5-9b";
    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";
    private static readonly string[] SelectedIds = ["DOC-0205", "DOC-0264"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private const int MaxTransientAttempts = 2;

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
            ContextSize = 262_144, MaxOutputTokens = 48_000, RequestTimeoutSeconds = 600,
            TransientRequestRetries = 2, MaxParallelRequests = 1, SendChatTemplateKwargs = false,
        };

        if (string.IsNullOrWhiteSpace(key))
            return await WriteBlockedAsync(output, "BLOCKED_OPENROUTER_MODEL_UNAVAILABLE", "OPENROUTER_API_KEY_MISSING", ct);

        using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = Timeout.InfiniteTimeSpan };
        var preflight = await OpenRouterModelCapabilityResolver.ResolveAsync(options, http, ct);
        Console.WriteLine($"MODEL={Model}");
        if (preflight.Capability is { } capLog)
            Console.WriteLine($"CONTEXT_LENGTH={capLog.ContextLength} REASONING_SUPPORTED={capLog.ReasoningSupported}");

        await WriteJsonAsync(Path.Combine(output, "config.v1.json"), new
        {
            schemaVersion = "a99-openrouter-qwen35-9b-executable-ceiling-v1",
            requestedModel = Model, endpoint = Endpoint, provider = "OpenRouter",
            selectedDocuments = SelectedIds, apiKeyPresent = true, modelFallback = "NONE",
            reasoningMode = "CEILING_NEVER_DISABLED", goldReadBeforeFreeze = false,
            capability = preflight.Capability, preflightAvailable = preflight.Available,
            preflightClassification = preflight.Classification, preflightReason = preflight.Reason,
            note = "recovery-only run: DOC-0258 full-context ceiling result is frozen and not rerun here",
            startedUtc = DateTimeOffset.UtcNow,
        }, ct);

        if (!preflight.Available || preflight.Capability is null)
            return await WriteBlockedAsync(output, preflight.Classification, preflight.Reason, ct);
        if (!preflight.Capability.ReasoningSupported)
            return await WriteBlockedAsync(output, "BLOCKED_PROVIDER_CAPABILITY_MISMATCH", "REASONING_NOT_REPORTED_SUPPORTED", ct);
        if (!string.Equals(preflight.Capability.ModelId, Model, StringComparison.Ordinal))
            return await WriteBlockedAsync(output, "BLOCKED_PROVIDER_CAPABILITY_MISMATCH", "MODEL_IDENTITY_MISMATCH", ct);

        using var model = new OpenRouterCeilingReasoningModel(options, preflight.Capability, http);
        var documents = new List<DocumentMetric>();
        var providerFailure = false;
        foreach (var item in selected)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                documents.Add(await RunDocumentAsync(repoRoot, output, item, model, ct));
            }
            catch (Exception ex)
            {
                providerFailure |= IsProviderFailure(ex);
                var docDir = Path.Combine(output, "documents", item.DocumentId);
                Directory.CreateDirectory(docDir);
                await WriteJsonAsync(Path.Combine(docDir, "execution.v1.json"), new
                {
                    documentId = item.DocumentId, sourceSha256 = item.SourceSha256, requestedModel = Model,
                    status = "DOCUMENT_FAILURE", error = ex.Message, goldReadBeforeFreeze = false,
                }, ct);
                Console.Error.WriteLine($"EXECUTABLE_CEILING_DOCUMENT_FAILURE={item.DocumentId}:{ex.Message}");
            }
        }

        var exact = documents.Where(x => x.exactStatus == "EVALUABLE").ToArray();
        var micro = Score(exact.SelectMany(x => x.GoldKeys).ToArray(), exact.SelectMany(x => x.PredictionKeys).ToArray());
        var systemLossTotal = documents.Sum(x => x.systemLossCount);
        var incompleteRun = documents.Count < selected.Length;
        var anyStillBlocked = documents.Any(x => x.finalExecutionMode == "BLOCKED");
        var classification = providerFailure || incompleteRun || anyStillBlocked
            ? "QWEN9B_RECOVERY_EXECUTION_STILL_BLOCKED"
            : systemLossTotal > 0
                ? "QWEN9B_RECOVERY_WORKS_SYSTEM_LOSS_REMAINS"
                : "QWEN9B_EXECUTABLE_REASONING_CEILING_EXPOSED";

        await WriteJsonAsync(Path.Combine(output, "summary.v1.json"), new
        {
            schemaVersion = "a99-openrouter-qwen35-9b-executable-ceiling-v1",
            status = classification, requestedModel = Model, provider = "OpenRouter", selectedDocuments = SelectedIds,
            documents, microExact = micro, systemInducedLossTotal = systemLossTotal,
            openRouterCalls = model.ProviderCalls,
            inputTokens = model.Telemetry.Sum(x => x.ReportedInputTokens ?? 0),
            outputTokens = model.Telemetry.Sum(x => x.ReportedOutputTokens ?? 0),
            reasoningTokens = model.Telemetry.Sum(x => x.ReportedReasoningTokens ?? 0),
            capability = preflight.Capability, requestTelemetry = model.Telemetry, goldReadBeforeFreeze = false,
            completedUtc = DateTimeOffset.UtcNow,
        }, ct);
        Console.WriteLine($"FINAL_CLASSIFICATION={classification}");
        return classification == "QWEN9B_RECOVERY_EXECUTION_STILL_BLOCKED" ? 1 : 0;
    }

    private static async Task<DocumentMetric> RunDocumentAsync(
        string repoRoot, string output, InventoryItem item, OpenRouterCeilingReasoningModel model, CancellationToken ct)
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
        var configurationSignature = ConfigurationSignature(model.Capability);

        // Section 5: capability-derived window, then the whole-document character count decide
        // the ladder. Neither Gold nor the model chooses segmentation.
        var semanticBudget = model.MaxPromptTokens(model.SemanticMaxCompletionTokens);
        var capabilityWindowCharacters = Math.Max(4_000, (int)(semanticBudget * ReasoningTokenBudget.CharactersPerToken) / 2);
        var totalCharactersPack = ReasoningContextBuilder.Build(source, policy, int.MaxValue / 2, int.MaxValue / 2, expandOwnedPerOccurrence: false);
        var totalCharacters = totalCharactersPack.SourceCharacters;
        var ladder = ReasoningSegmentSizeLadder.Steps(capabilityWindowCharacters, totalCharacters);

        string? fullContextAttemptStatus = null;
        string finalExecutionMode = "BLOCKED";
        var recoveryAttempted = false;
        var proposals = new List<ReasoningHeadingProposal>();
        var provenance = new List<ReasoningRecoveryProvenance>();
        var segmentOutcomes = new List<ReasoningRecoverySegmentOutcome>();
        var rawProposalCount = 0;
        var spanErrorCount = 0;
        var semanticCalls = 0;
        var stopwatch = Stopwatch.StartNew();
        var succeededAtStep = -1;

        for (var stepIndex = 0; stepIndex < ladder.Count; stepIndex++)
        {
            var stepSize = ladder[stepIndex];
            var isFullContextStep = stepIndex == 0;
            var stepMode = isFullContextStep ? RecoveryExecutionMode.FullContext : RecoveryExecutionMode.SegmentedReasoningRecovery;
            var pack = ReasoningContextBuilder.Build(source, policy, stepSize, stepSize, expandOwnedPerOccurrence: false);
            var occurrenceById = pack.Occurrences.ToDictionary(x => x.SourceOccurrenceId, StringComparer.Ordinal);

            var stepProposals = new List<ReasoningHeadingProposal>();
            var stepProvenance = new List<ReasoningRecoveryProvenance>();
            var stepOutcomes = new List<ReasoningRecoverySegmentOutcome>();
            var stepRaw = 0;
            var stepSpanErrors = 0;
            var stepCalls = 0;
            string? stepFailure = null;

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
                var requestId = $"{CeilingSemanticPrompt.ProtocolVersion}:{source.DocumentId}:{stepSize}:{segment.ContextSegmentId}:{configurationSignature}";
                var segmentSw = Stopwatch.StartNew();
                var (attemptResult, failureClass, attempts) = await TryCompleteSegmentAsync(
                    model, source.DocumentId, ReasoningRoute.ModelCapabilityCeiling.ToString(), requestId, packetResult, ownedSet.Count, visibleIds.Count, ct);
                segmentSw.Stop();
                stepCalls += attempts;

                if (attemptResult is null)
                {
                    stepFailure = failureClass;
                    stepOutcomes.Add(new ReasoningRecoverySegmentOutcome(segment.ContextSegmentId, segment.Ordinal, stepSize, attempts, false, failureClass, null, null, null, null, segmentSw.ElapsedMilliseconds));
                    break; // section 2: never keep hammering this step's shape -- abandon the step, drop to the next ladder size.
                }

                var (response, telemetry) = attemptResult.Value;
                stepCalls = stepCalls; // calls already counted via attempts
                stepRaw += response.Headings.Count;
                var rawResponseHash = Sha256Text(JsonSerializer.Serialize(response));
                stepOutcomes.Add(new ReasoningRecoverySegmentOutcome(segment.ContextSegmentId, segment.Ordinal, stepSize, attempts, true, null, rawResponseHash,
                    telemetry.ReportedReasoningTokens, telemetry.ReportedOutputTokens, telemetry.ReportedInputTokens, segmentSw.ElapsedMilliseconds));

                foreach (var heading in response.Headings)
                {
                    if (heading.I < 0 || heading.I >= packetResult.Bindings.Count) { stepSpanErrors++; continue; }
                    var binding = packetResult.Bindings[heading.I];
                    if (!ownedSet.Contains(binding.SourceOccurrenceId)) { stepSpanErrors++; continue; }
                    if (!binding.TryBind(heading.Start, heading.End, out var globalStart, out var globalEnd, out var owned) || !owned)
                    {
                        stepSpanErrors++; continue;
                    }
                    var occurrence = occurrenceById[binding.SourceOccurrenceId];
                    var proposal = new ReasoningHeadingProposal
                    {
                        SourceId = occurrence.SourceId,
                        HeadingSpan = new StructuralSpan(globalStart, globalEnd),
                        Text = occurrence.RawText[globalStart..globalEnd],
                        SemanticRole = heading.Role,
                        Confidence = 1,
                    };
                    stepProposals.Add(proposal);
                    stepProvenance.Add(new ReasoningRecoveryProvenance($"{proposal.SourceId}:{globalStart}:{globalEnd}", segment.ContextSegmentId, rawResponseHash));
                }
            }

            if (stepFailure is null)
            {
                // Section 8: conflict-aware union across segments -- dedupe by exact canonical
                // identity (sourceId + span). A proposal never disappears because another segment
                // failed to re-emit it; a genuine duplicate keeps the first-seen payload.
                var merged = stepProposals
                    .GroupBy(p => $"{p.SourceId}:{p.HeadingSpan.Start}:{p.HeadingSpan.End}", StringComparer.Ordinal)
                    .Select(g => g.First())
                    .ToList();
                proposals = merged;
                provenance = stepProvenance
                    .GroupBy(p => p.ProposalKey, StringComparer.Ordinal)
                    .Select(g => g.First())
                    .ToList();
                rawProposalCount = stepRaw;
                spanErrorCount = stepSpanErrors;
                semanticCalls = stepCalls;
                segmentOutcomes = stepOutcomes;
                finalExecutionMode = stepMode;
                recoveryAttempted = !isFullContextStep;
                succeededAtStep = stepIndex;
                break;
            }

            if (isFullContextStep)
                fullContextAttemptStatus = ReasoningRecoveryRetryPolicy.ToFullContextStatus(stepFailure);
            segmentOutcomes.AddRange(stepOutcomes);

            if (ReasoningRecoveryRetryPolicy.Classify(stepFailure) == ReasoningRecoveryRetryClass.NonRecoverable)
                break; // an auth-style failure is not fixed by a smaller request; stop the ladder.
        }

        fullContextAttemptStatus ??= FullContextAttemptStatus.Success;

        var hierarchyCalls = 0;
        var hierarchyValidation = new ReasoningHierarchyValidation([], [], []);
        var hierarchyDegraded = false;
        if (succeededAtStep >= 0 && proposals.Count > 0)
        {
            var globalInventory = ReasoningGlobalHierarchyPass.BuildInventory(source.DocumentId, source, proposals);
            var (packetJson, bindings) = CeilingHierarchyPacketBuilder.Build(globalInventory);
            var requestId = $"{CeilingHierarchyPrompt.ProtocolVersion}:{source.DocumentId}:global-hierarchy:{configurationSignature}";
            var (hResult, hFailure, hAttempts) = await TryCompleteHierarchyAsync(model, source.DocumentId, ReasoningRoute.ModelCapabilityCeiling.ToString(), requestId, packetJson, globalInventory.Count, ct);
            hierarchyCalls += hAttempts;
            if (hResult is { } response)
            {
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
            else
            {
                // Section 9: the hierarchy pass hitting its own execution budget must never delete
                // heading existence. Fall back to a flat, harness-derived level rather than losing
                // any heading.
                hierarchyDegraded = true;
                proposals = proposals.Select(p => p with { ProposedParent = null, ProposedLevel = 1 }).ToList();
            }
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

        var recoverySegmentPlanHash = Sha256Text(string.Join('|', segmentOutcomes.Select(x => $"{x.SegmentId}:{x.WindowCharacters}")));
        var segmentResponseHashes = segmentOutcomes.Where(x => x.RawResponseHash is not null).Select(x => x.RawResponseHash!).ToArray();

        var predictionPath = Path.Combine(docDir, "prediction.v1.json");
        await WriteJsonAsync(predictionPath, new
        {
            documentId = item.DocumentId, sourceSha256 = item.SourceSha256, requestedModel = Model,
            sourceCharacters = totalCharacters, ladder, fullContextAttemptStatus, recoveryAttempted, finalExecutionMode,
            hierarchyDegraded, semanticProtocolVersion = CeilingSemanticPrompt.ProtocolVersion, hierarchyProtocolVersion = CeilingHierarchyPrompt.ProtocolVersion,
            semanticCalls, hierarchyCalls, rawProposalCount, boundProposalCount = proposals.Count, spanErrorCount,
            validatedSemanticCount = materialized.Validated.Count(x => x.Accepted), proposals, provenance, projection,
            hierarchyValidation, segmentOutcomes, goldReadBeforeFreeze = false,
        }, ct);
        var resultPath = Path.Combine(docDir, "result.v1.json");
        await WriteJsonAsync(resultPath, new
        {
            documentId = item.DocumentId, status = finalExecutionMode == "BLOCKED" ? "BLOCKED" : "SUCCESS",
            fullContextAttemptStatus, finalExecutionMode, headings = finalElements, goldReadBeforeFreeze = false,
        }, ct);

        var docTelemetry = model.Telemetry.Where(x => string.Equals(x.DocumentId, source.DocumentId, StringComparison.Ordinal)).ToArray();
        var runtimeTracePath = Path.Combine(docDir, "runtime-trace.v1.json");
        await WriteJsonAsync(runtimeTracePath, new
        {
            documentId = item.DocumentId, semanticCalls, hierarchyCalls, rawProposalCount, spanErrorCount,
            fullContextAttemptStatus, finalExecutionMode, segmentOutcomes, requests = docTelemetry, goldReadBeforeFreeze = false,
        }, ct);

        var predictionHash = Sha256(predictionPath);
        var resultHash = Sha256(resultPath);
        var runtimeTraceHash = Sha256(runtimeTracePath);
        var freezePath = Path.Combine(docDir, "freeze.v1.json");
        await WriteJsonAsync(freezePath, new
        {
            documentId = item.DocumentId, sourceSha256 = item.SourceSha256,
            predictionSha256 = predictionHash, resultSha256 = resultHash, runtimeTraceSha256 = runtimeTraceHash,
            model = Model, reasoningMode = "CEILING_NEVER_DISABLED", modelCapabilityDigest = CapabilityDigest(model.Capability),
            executionMode = finalExecutionMode, fullContextAttemptStatus, recoveryAttempted,
            recoverySegmentPlanHash, segmentResponseHashes,
            semanticProtocolVersion = CeilingSemanticPrompt.ProtocolVersion, hierarchyProtocolVersion = CeilingHierarchyPrompt.ProtocolVersion,
            reasoningEffort = model.Capability.SelectedReasoningEffort, contextLength = model.Capability.ContextLength,
            configurationSignature, goldReadBeforeFreeze = false, frozenUtc = DateTimeOffset.UtcNow,
        }, ct);

        var eligibility = ReasoningGoldEligibilityEvaluator.Evaluate(repoRoot, item.DocumentId);
        var goldKeys = Array.Empty<string>();
        var tp = 0; var fp = 0; var fn = 0; var exactStatus = "NOT_EVALUABLE"; var systemLoss = 0;
        var lossCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        if (eligibility.Eligible && finalExecutionMode != "BLOCKED")
        {
            var goldPath = Path.Combine(repoRoot, "eval", "a99-closed-loop", "strict-gold-occurrence-v1", $"{item.DocumentId}.occurrence-gold-v1.json");
            var gold = ReasoningGoldArtifactLoader.LoadOccurrence(goldPath);
            goldKeys = gold.Where(x => x.HeadingSpan is not null).Select(x => Key(x.SourceId, x.HeadingSpan!)).ToArray();
            var score = Score(goldKeys, predictionKeys);
            tp = score.TP; fp = score.FP; fn = score.FN; exactStatus = "EVALUABLE";
            foreach (var loss in ClassifyLosses(gold, proposals, materialized, finalElements, projection, segmentOutcomes))
                lossCounts[loss] = lossCounts.GetValueOrDefault(loss) + 1;
            systemLoss = lossCounts.Where(x => x.Key.StartsWith("SYSTEM_", StringComparison.Ordinal)).Sum(x => x.Value);
            await WriteJsonAsync(Path.Combine(docDir, "score.v1.json"), new
            {
                documentId = item.DocumentId, exactStatus, goldCount = goldKeys.Length, tp, fp, fn,
                precision = score.P, recall = score.R, f1 = score.F1, lossCounts, systemLossCount = systemLoss,
                finalExecutionMode, recoveryAttempted, goldReadBeforeFreeze = false,
            }, ct);
        }
        else
        {
            var occurrencePath = Path.Combine(repoRoot, "eval", "a99-closed-loop", "strict-gold-occurrence-v1", $"{item.DocumentId}.occurrence-gold-v1.json");
            var semanticTotal = File.Exists(occurrencePath) ? JsonDocument.Parse(File.ReadAllText(occurrencePath)).RootElement.GetProperty("semanticHeadingTotal").GetInt32() : 0;
            await WriteJsonAsync(Path.Combine(docDir, "semantic-evaluation.v1.json"), new
            {
                documentId = item.DocumentId, exactStatus, eligibilityReason = eligibility.Eligible ? "EXECUTION_BLOCKED" : eligibility.Reason,
                semanticHeadingTotal = semanticTotal, rawProposalCount, boundProposalCount = proposals.Count, spanErrorCount,
                finalHeadingCount = finalElements.Length, systemLossCount = 0, finalExecutionMode, recoveryAttempted, goldReadBeforeFreeze = false,
            }, ct);
        }
        stopwatch.Stop();
        var scoreFinal = Score(goldKeys, predictionKeys);
        var metric = new DocumentMetric(item.DocumentId, totalCharacters, segmentOutcomes.Count, semanticCalls, hierarchyCalls,
            docTelemetry.Length, docTelemetry.Sum(x => x.ReportedInputTokens ?? 0), docTelemetry.Sum(x => x.ReportedOutputTokens ?? 0),
            docTelemetry.Sum(x => x.ReportedReasoningTokens ?? 0), stopwatch.ElapsedMilliseconds, rawProposalCount, proposals.Count,
            materialized.Validated.Count(x => x.Accepted), finalElements.Length, systemLoss, exactStatus, tp, fp, fn,
            scoreFinal.P, scoreFinal.R, scoreFinal.F1, goldKeys, predictionKeys, fullContextAttemptStatus, finalExecutionMode, recoveryAttempted);
        await WriteJsonAsync(Path.Combine(docDir, "execution.v1.json"), metric with { sourceSha256 = item.SourceSha256, status = finalExecutionMode == "BLOCKED" ? "BLOCKED" : "SUCCESS", freezePath = freezePath, goldReadBeforeFreeze = false }, ct);
        return metric;
    }

    /// <summary>Section 2: exactly one attempt per segment shape. Transient transport failures
    /// get a small bounded number of identical retries; a workload-shape failure (timeout,
    /// output limit) returns immediately with no retry so the caller can step down the ladder
    /// instead of re-sending the same oversized request.</summary>
    private static async Task<((CeilingSemanticResponse Response, RequestPacketTelemetry Telemetry)? Result, string? FailureClass, int Attempts)> TryCompleteSegmentAsync(
        OpenRouterCeilingReasoningModel model, string documentId, string route, string requestId,
        CeilingPacketResult packet, int ownedOccurrences, int visibleOccurrences, CancellationToken ct)
    {
        string? lastFailure = null;
        for (var attempt = 1; attempt <= MaxTransientAttempts; attempt++)
        {
            try
            {
                var result = await model.CompleteSemanticAsync(documentId, route, $"{requestId}:attempt-{attempt}", packet.SerializedJson,
                    packet.SourceTextCharacters, ownedOccurrences, visibleOccurrences, ct).ConfigureAwait(false);
                return (result, null, attempt);
            }
            catch (ReasoningCompletionException ex)
            {
                lastFailure = ex.FailureClass;
                if (ReasoningRecoveryRetryPolicy.Classify(ex.FailureClass) != ReasoningRecoveryRetryClass.TransientTransportRetry || attempt == MaxTransientAttempts)
                    return (null, lastFailure, attempt);
                await Task.Delay(250, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or FormatException or JsonException)
            {
                // A malformed/empty JSON body that was not already classified as an output-limit
                // failure by the model adapter (e.g. finish_reason=stop with empty content) is
                // schema corruption, not a semantic omission -- give it one bounded retry rather
                // than crashing the whole document.
                lastFailure = ex is JsonException ? ReasoningCompletionFailureClass.CompleteResponseSchemaInvalid : ReasoningCompletionFailureClass.OtherProviderFailure;
                if (attempt == MaxTransientAttempts) return (null, lastFailure, attempt);
                await Task.Delay(250, ct).ConfigureAwait(false);
            }
        }
        return (null, lastFailure, MaxTransientAttempts);
    }

    private static async Task<(CeilingHierarchyResponse? Result, string? FailureClass, int Attempts)> TryCompleteHierarchyAsync(
        OpenRouterCeilingReasoningModel model, string documentId, string route, string requestId,
        string packetJson, int inventoryCount, CancellationToken ct)
    {
        string? lastFailure = null;
        for (var attempt = 1; attempt <= MaxTransientAttempts; attempt++)
        {
            try
            {
                var (response, _) = await model.CompleteHierarchyAsync(documentId, route, $"{requestId}:attempt-{attempt}", packetJson, inventoryCount, ct).ConfigureAwait(false);
                var distinctChildren = response.Parents.Select(edge => edge.Child).ToHashSet();
                if (response.Parents.Count != inventoryCount || distinctChildren.Count != inventoryCount)
                    throw new FormatException($"ceiling-hierarchy-response-incomplete:expected={inventoryCount}:got={distinctChildren.Count}");
                return (response, null, attempt);
            }
            catch (ReasoningCompletionException ex)
            {
                lastFailure = ex.FailureClass;
                if (ReasoningRecoveryRetryPolicy.Classify(ex.FailureClass) != ReasoningRecoveryRetryClass.TransientTransportRetry || attempt == MaxTransientAttempts)
                    return (null, lastFailure, attempt);
                await Task.Delay(250, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or FormatException or JsonException)
            {
                // A malformed/empty JSON body that was not already classified as an output-limit
                // failure by the model adapter (e.g. finish_reason=stop with empty content) is
                // schema corruption, not a semantic omission -- give it one bounded retry rather
                // than crashing the whole document.
                lastFailure = ex is JsonException ? ReasoningCompletionFailureClass.CompleteResponseSchemaInvalid : ReasoningCompletionFailureClass.OtherProviderFailure;
                if (attempt == MaxTransientAttempts) return (null, lastFailure, attempt);
                await Task.Delay(250, ct).ConfigureAwait(false);
            }
        }
        return (null, lastFailure, MaxTransientAttempts);
    }

    private static IEnumerable<string> ClassifyLosses(
        IReadOnlyList<ReasoningGoldOccurrence> gold, IReadOnlyList<ReasoningHeadingProposal> proposals,
        (ValidatedStructure Structure, IReadOnlyList<ReasoningValidatedProposal> Validated) materialized,
        IReadOnlyList<ValidatedStructuralElement> finalElements, IReadOnlyList<ReasoningProjectionDecision> projectionById,
        IReadOnlyList<ReasoningRecoverySegmentOutcome> segmentOutcomes)
    {
        var predicted = finalElements.Select(Key).ToHashSet(StringComparer.Ordinal);
        var raw = proposals.ToArray();
        var rows = materialized.Validated.ToDictionary(x => x.ElementId, StringComparer.Ordinal);
        var anySegmentFailedAfterSuccess = segmentOutcomes.Any(x => !x.Succeeded);
        foreach (var item in gold.Where(x => x.HeadingSpan is not null))
        {
            var key = Key(item.SourceId, item.HeadingSpan!);
            if (predicted.Contains(key)) continue;
            var exact = raw.FirstOrDefault(p => Key(p) == key);
            if (exact is null)
            {
                var near = raw.Any(p => p.SourceId == item.SourceId && (p.HeadingSpan.Start == item.HeadingSpan!.Start || p.Text.Contains(item.ExactText, StringComparison.Ordinal)));
                if (near) { yield return "MODEL_SPAN_ERROR"; continue; }
                // An execution failure on the way to producing this heading is a system-induced
                // loss, never a genuine model omission -- section 13.
                yield return anySegmentFailedAfterSuccess ? "SYSTEM_SEGMENTATION_LOSS" : "MODEL_OMISSION";
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
            yield return raw.Any(p => Key(p) == key) ? "MODEL_FALSE_POSITIVE" : "SYSTEM_BINDING_LOSS";
        }
    }

    private static string Key(ReasoningHeadingProposal p) => Key(p.SourceId, p.HeadingSpan);
    private static string Key(ValidatedStructuralElement e) => Key(e.Sources.Single().SourceId, e.Sources.Single().Span);
    private static string Key(string sourceId, StructuralSpan span) => $"{sourceId}:{span.Start}:{span.End}";
    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Sha256Text(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string ConfigurationSignature(OpenRouterModelCapability capability) => Sha256Text(
        $"A99_OPENROUTER_QWEN35_9B_EXECUTABLE_CEILING|{Model}|{CeilingSemanticPrompt.ProtocolVersion}|{CeilingHierarchyPrompt.ProtocolVersion}|temperature=0|reasoning={capability.SelectedReasoningEffort}|exclude=true|fallbacks=false|context={capability.ContextLength}");

    private static string CapabilityDigest(OpenRouterModelCapability capability) => Sha256Text(
        $"{capability.ModelId}|{capability.ContextLength}|{capability.ReasoningSupported}|{string.Join(',', capability.SupportedReasoningEfforts)}|{capability.SelectedReasoningEffort}|{capability.StructuredOutputSupported}|{capability.MaxCompletionTokens}");

    private static async Task WriteJsonAsync(string path, object value, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine);
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, FileOptions.SequentialScan);
        await stream.WriteAsync(bytes, ct); await stream.FlushAsync(ct); stream.Flush(true);
    }

    private static async Task<int> WriteBlockedAsync(string output, string classification, string reason, CancellationToken ct)
    {
        await WriteJsonAsync(Path.Combine(output, "summary.v1.json"), new
        {
            schemaVersion = "a99-openrouter-qwen35-9b-executable-ceiling-v1", status = classification, reason,
            requestedModel = Model, provider = "OpenRouter", selectedDocuments = SelectedIds,
            openRouterCalls = 0, inputTokens = 0, outputTokens = 0, goldReadBeforeFreeze = false,
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

    private sealed record InventoryItem(string DocumentId, string SourcePath, string SourceSha256);
    private sealed record ScoreResult(int TP, int FP, int FN, double P, double R, double F1);
    private sealed record DocumentMetric(string documentId, int sourceCharacters, int segments, int semanticCalls, int hierarchyCalls,
        int totalModelCalls, int inputTokens, int outputTokens, int reasoningTokens, long wallTime, int rawProposalCount,
        int boundProposalCount, int validatedSemanticCount, int finalHeadingCount, int systemLossCount, string exactStatus,
        int TP, int FP, int FN, double P, double R, double F1, IReadOnlyList<string> GoldKeys, IReadOnlyList<string> PredictionKeys,
        string? fullContextAttemptStatus, string finalExecutionMode, bool recoveryAttempted)
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
