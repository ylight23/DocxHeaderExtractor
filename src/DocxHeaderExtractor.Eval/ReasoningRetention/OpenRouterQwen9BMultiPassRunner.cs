using System.Diagnostics;
using System.Globalization;
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
/// Live wiring for the offline-only <see cref="MultiPassReasoningProtocol"/> primitives, scoped to
/// DOC-0205 only (section 1: DOC-0258 is a later, separate step). This runner never re-runs S0 --
/// it reuses the frozen single-leaf full-context DOC-0205 result from
/// openrouter-qwen35-9b-per-segment-recovery/documents/DOC-0205/ (that run's segment tree has
/// exactly one leaf covering the whole document, so DOC-0205 is a full-context single-packet case).
/// S1/S2/S3 each make exactly one fresh provider call, are Gold-firewalled (freeze before any Gold
/// read), and never bypass the existing hard validator/materializer/projection pipeline.
/// </summary>
public static class OpenRouterQwen9BMultiPassRunner
{
    private const string OutputRoot = "eval/a99-closed-loop/qwen9b-multipass-doc0205";
    private const string S0Root = "eval/a99-closed-loop/openrouter-qwen35-9b-per-segment-recovery";
    private const string InventoryPath = "eval/a99-dataset/document-inventory.v1.json";
    private const string DocumentId = "DOC-0205";
    private const string Model = "qwen/qwen3.5-9b";
    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";
    private const string ReasoningMode = "CEILING_NEVER_DISABLED";
    private const int MinimumSegmentCharacters = 12_000;
    private const int MinimumVisibleContextCharacters = 6_000;
    private const int HaloOccurrences = 1;
    private const int MaxTransientAttemptsPerLeaf = 2;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default, Qwen9BExecutionProfile? executionProfile = null)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.Combine(output, "s0"));
        Directory.CreateDirectory(Path.Combine(output, "s1"));
        Directory.CreateDirectory(Path.Combine(output, "s2"));
        Directory.CreateDirectory(Path.Combine(output, "s3"));

        var inventory = ReadInventory(Path.Combine(repoRoot, InventoryPath));
        var item = inventory.Single(x => x.DocumentId == DocumentId);

        var sourcePath = Path.Combine(repoRoot, item.SourcePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(sourcePath) || !string.Equals(Sha256(sourcePath), item.SourceSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SOURCE_HASH_MISMATCH");

        var source = new OpenXmlDocumentSource().Read(sourcePath) with { DocumentId = item.DocumentId };
        var features = NumberingStyleFeatures.FromSourceDocument(source);
        var derived = new DocumentFeatureDeriver().Derive(source);
        var policy = DocxPolicyStateBuilder.Build(source, features, derived, new PipelineOptions { DisableLlm = false }.Extraction);
        var pack = ReasoningContextBuilder.Build(source, policy, int.MaxValue / 2, int.MaxValue / 2, expandOwnedPerOccurrence: false);
        var occurrences = pack.Occurrences;

        // Section 3/10: full-context, single-packet build -- identical shape to S0's single
        // successful leaf (verified below: 1 leaf, all occurrences fully owned/visible).
        var ownedSet = occurrences.Select(o => o.SourceOccurrenceId).ToHashSet(StringComparer.Ordinal);
        var packetResult = CeilingPacketBuilder.Build(occurrences, ownedSet);

        // ---- Section 3: reuse S0, zero new provider calls -------------------------------------
        var s0Dir = Path.Combine(repoRoot, S0Root.Replace('/', Path.DirectorySeparatorChar), "documents", DocumentId);
        var s0Predict = Path.Combine(s0Dir, "prediction.v1.json");
        var s0Freeze = Path.Combine(s0Dir, "freeze.v1.json");
        var s0Score = Path.Combine(s0Dir, "score.v1.json");
        if (!File.Exists(s0Predict) || !File.Exists(s0Freeze))
            throw new InvalidDataException("S0_FROZEN_ARTIFACTS_MISSING");
        using var freezeDoc = JsonDocument.Parse(File.ReadAllText(s0Freeze));
        var freezeRoot = freezeDoc.RootElement;
        var expectedPredictionSha = freezeRoot.GetProperty("predictionSha256").GetString();
        var actualPredictionSha = Sha256(s0Predict);
        if (!string.Equals(expectedPredictionSha, actualPredictionSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("S0_PREDICTION_HASH_MISMATCH");
        if (!File.Exists(s0Score))
            throw new InvalidDataException("S0_SCORE_ARTIFACT_MISSING");
        var expectedResultSha = freezeRoot.GetProperty("resultSha256").GetString();
        var s0Result = Path.Combine(s0Dir, "result.v1.json");
        if (!File.Exists(s0Result) || !string.Equals(expectedResultSha, Sha256(s0Result), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("S0_RESULT_HASH_MISMATCH");
        if (freezeRoot.GetProperty("sourceSha256").GetString() != item.SourceSha256)
            throw new InvalidDataException("S0_SOURCE_HASH_MISMATCH");
        if (freezeRoot.GetProperty("model").GetString() != Model)
            throw new InvalidDataException("S0_MODEL_MISMATCH");
        if (freezeRoot.GetProperty("reasoningMode").GetString() != ReasoningMode)
            throw new InvalidDataException("S0_REASONING_MODE_MISMATCH");
        if (string.IsNullOrWhiteSpace(freezeRoot.GetProperty("configurationSignature").GetString()))
            throw new InvalidDataException("S0_CONFIGURATION_SIGNATURE_MISSING");
        if (freezeRoot.GetProperty("leafSegments").GetInt32() != 1)
            throw new InvalidDataException("S0_NOT_SINGLE_LEAF_FULL_CONTEXT");

        var s0GlobalProposals = ReadS0GlobalProposals(s0Predict);
        var occIndexBySourceId = BuildOccIndexBySourceId(occurrences, packetResult);
        var s0LocalProposals = ToLocal(s0GlobalProposals, occIndexBySourceId, occurrences);

        File.Copy(s0Predict, Path.Combine(output, "s0", "manifest.v1.json"), overwrite: true);
        File.Copy(s0Freeze, Path.Combine(output, "s0", "freeze.v1.json"), overwrite: true);
        if (File.Exists(s0Score)) File.Copy(s0Score, Path.Combine(output, "s0", "score.v1.json"), overwrite: true);
        Console.WriteLine("S0_REUSED=true");
        Console.WriteLine("S0_PROVIDER_CALLS=0");

        // ---- Provider preflight (S1/S2/S3 only) -------------------------------------------------
        using var liveLease = await A99OpenRouterLiveProviderLease.AcquireAsync(repoRoot, "S1/S2/S3", DocumentId, ct);
        Console.WriteLine($"LIVE_PROVIDER_LOCK=acquired concurrentCampaignsDetected={liveLease.ConcurrentCampaignsDetected} providerConcurrency={liveLease.ProviderConcurrency}");
        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        var options = new RemoteInferenceOptions
        {
            Endpoint = new Uri(Endpoint), Model = Model, ApiKey = key ?? "",
            ContextSize = 262_144, MaxOutputTokens = 48_000, RequestTimeoutSeconds = 1_500,
            TransientRequestRetries = 2, MaxParallelRequests = 1, SendChatTemplateKwargs = false,
            OpenRouterProviderRoute = executionProfile?.PinnedProvider,
        };
        if (string.IsNullOrWhiteSpace(key))
            return await WriteBlockedAsync(output, "OPENROUTER_API_KEY_MISSING", ct);

        using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = Timeout.InfiniteTimeSpan };
        var preflight = await OpenRouterModelCapabilityResolver.ResolveAsync(options, http, ct);
        if (!preflight.Available || preflight.Capability is null)
            return await WriteBlockedAsync(output, preflight.Reason ?? "PROVIDER_UNAVAILABLE", ct);
        if (!preflight.Capability.ReasoningSupported)
            return await WriteBlockedAsync(output, "REASONING_NOT_REPORTED_SUPPORTED", ct);
        if (!string.Equals(preflight.Capability.ModelId, Model, StringComparison.Ordinal))
            return await WriteBlockedAsync(output, "MODEL_IDENTITY_MISMATCH", ct);

        // Bound every live leaf independently. The provider client itself is infinite-timeout;
        // this adapter deadline covers headers, body streaming, and post-body parsing.
        using var model = new OpenRouterCeilingReasoningModel(options, preflight.Capability, http,
            // Three minutes is an execution guard, not a completion-token reduction: the
            // CEILING request still advertises the resolved 48K budget. It bounds a leaf that
            // receives no usable completion while allowing small leaves to return normally.
            attemptDeadline: executionProfile?.AttemptDeadline ?? TimeSpan.FromMinutes(3),
            streamStallDeadline: executionProfile?.StreamStallDeadline ?? TimeSpan.FromMinutes(2));
        var configurationSignature = ConfigurationSignature(model.Capability);
        var route = ReasoningRoute.ModelCapabilityCeiling.ToString();
        var gitSha = CurrentGitSha(repoRoot);

        // Keep Gold as a path until each strategy has written its freeze artifact. The
        // transformation and all fresh model calls therefore remain Gold-blind.
        var goldPath = Path.Combine(repoRoot, "eval", "a99-closed-loop", "strict-gold-occurrence-v1", $"{DocumentId}.occurrence-gold-v1.json");

        var strategies = new List<StrategyMetric>();
        // S0 metrics (reference only, from the frozen score artifact, never re-scored here).
        strategies.Add(ReadS0Metric(s0Score, s0GlobalProposals.Count));

        ReasoningCompletionException? blocked = null;
        string? failedStrategy = null;
        try
        {
            var s1 = await RunS1SegmentedAsync(output, model, route, configurationSignature, gitSha, source, policy, occurrences, packetResult, ownedSet, s0LocalProposals, goldPath, item.SourceSha256, ct, executionProfile is not null);
            strategies.Add(s1);
        }
        catch (ReasoningCompletionException ex)
        {
            blocked = ex; failedStrategy = "S1";
            await WriteStrategyBlockedAsync(output, "S1", ex, model, ct);
        }

        // S2 is an independent source-only extractor and is required even when S1 reaches a
        // genuine terminal leaf block; it must never inherit S1's failure as a semantic result.
        (StrategyMetric Metric, IReadOnlyList<CeilingHeadingProposal> UnionLocalProposals)? s2Result = null;
        try
        {
            s2Result = await RunS2SegmentedAsync(output, model, route, configurationSignature, gitSha, source, policy, occurrences, packetResult, ownedSet, s0LocalProposals, goldPath, item.SourceSha256, ct, executionProfile is not null);
            strategies.Add(s2Result.Value.Metric);
        }
        catch (ReasoningCompletionException ex)
        {
            blocked ??= ex; failedStrategy ??= "S2";
            await WriteStrategyBlockedAsync(output, "S2", ex, model, ct);
        }

        if (s2Result is not null)
        {
            try
            {
                var s3 = await RunS3SegmentedAsync(output, model, route, configurationSignature, gitSha, source, policy, occurrences, packetResult, ownedSet, s2Result.Value.UnionLocalProposals, goldPath, item.SourceSha256, ct, executionProfile is not null);
                strategies.Add(s3);
            }
            catch (ReasoningCompletionException ex)
            {
                blocked ??= ex; failedStrategy ??= "S3";
                await WriteStrategyBlockedAsync(output, "S3", ex, model, ct);
            }
        }

        if (blocked is not null)
        {
            await WriteJsonAsync(Path.Combine(output, "summary.v1.json"), new
            {
                schemaVersion = "a99-qwen9b-multipass-doc0205-v1", status = "QWEN9B_MULTI_PASS_EXECUTION_BLOCKED",
                reason = blocked.FailureClass, message = blocked.Message, strategiesCompleted = strategies.Select(s => s.Strategy).ToArray(),
                failedStrategy, providerCalls = model.ProviderCalls, telemetry = model.Telemetry,
                historicalAttempts = ReadAccounting(output, "historicalAttempts"), probeAttempts = ReadAccounting(output, "probeAttempts"),
                resumeAttempts = model.ProviderCalls, reusedSuccessLeaves = strategies.Sum(s => s.ReusedLeaves),
                segmentAudits = new[] { ReadSegmentAudit(output, "S1"), ReadSegmentAudit(output, "S2"), ReadSegmentAudit(output, "S3") },
                goldReadBeforeFreeze = false, completedUtc = DateTimeOffset.UtcNow,
            }, ct);
            PrintComparison(strategies);
            Console.WriteLine("FINAL_CLASSIFICATION=QWEN9B_MULTI_PASS_EXECUTION_BLOCKED");
            Console.WriteLine("VLM_NEXT_STEP=VLM_UNDECIDED");
            return 1;
        }

        var s0m = strategies.Single(s => s.Strategy == "S0");
        var bestRecall = strategies.OrderByDescending(s => s.Recall).ThenByDescending(s => s.F1).First();
        var bestF1 = strategies.OrderByDescending(s => s.F1).ThenByDescending(s => s.Recall).First();

        string classification;
        if (bestRecall.TrueModelOmission < s0m.TrueModelOmission && strategies.All(s => s.SystemInducedLoss == 0))
        {
            classification = bestF1.Precision + 0.05 < bestRecall.Precision || bestRecall.Strategy == bestF1.Strategy
                ? "QWEN9B_MULTI_PASS_CAPABILITY_AMPLIFIED"
                : "QWEN9B_MULTI_PASS_RECALL_UP_PRECISION_TRADEOFF";
        }
        else
        {
            classification = "QWEN9B_MULTI_PASS_NO_MATERIAL_GAIN";
        }

        var comparison = strategies.Select(s => new
        {
            strategy = s.Strategy,
            metrics = s,
            deltaTP = s.TP - s0m.TP,
            deltaFP = s.FP - s0m.FP,
            deltaFN = s.FN - s0m.FN,
            deltaRecall = s.Recall - s0m.Recall,
            deltaF1 = s.F1 - s0m.F1,
            deltaTrueModelOmission = s.TrueModelOmission - s0m.TrueModelOmission,
        }).ToArray();
        await WriteJsonAsync(Path.Combine(output, "comparison.v1.json"), new { documentId = DocumentId, strategies = comparison, goldReadBeforeFreeze = false }, ct);
        await WriteJsonAsync(Path.Combine(output, "summary.v1.json"), new
        {
            schemaVersion = "a99-qwen9b-multipass-doc0205-v1", status = classification, documentId = DocumentId,
            strategies, bestRecallStrategy = bestRecall.Strategy, bestF1Strategy = bestF1.Strategy,
            historicalAttempts = ReadAccounting(output, "historicalAttempts"), probeAttempts = ReadAccounting(output, "probeAttempts"),
            resumeAttempts = model.ProviderCalls, reusedSuccessLeaves = strategies.Sum(s => s.ReusedLeaves),
            capability = preflight.Capability, goldReadBeforeFreeze = false, completedUtc = DateTimeOffset.UtcNow,
        }, ct);
        PrintComparison(strategies);
        Console.WriteLine($"FINAL_CLASSIFICATION={classification}");
        Console.WriteLine("VLM_NEXT_STEP=VLM_UNDECIDED");
        return 0;
    }

    private sealed record SegmentPassResult(
        bool Complete, string? FailureClass, IReadOnlyList<RequestPacketTelemetry> Telemetry,
        int SegmentCount, int SplitCount, int SuccessfulLeaves, int ReusedLeaves);

    private sealed record SegmentPacket(
        CeilingPacketResult Packet, IReadOnlySet<string> OwnedSet,
        IReadOnlyDictionary<string, (int Start, int End)> OwnedWindow);

    private static async Task<SegmentPassResult> ExecuteSegmentPassAsync(
        string strategy, string dir, string sourceSha256, string configurationSignature,
        IReadOnlyList<ReasoningSourceOccurrence> occurrences,
        Func<SegmentNode, Task<(string ResponseJson, RequestPacketTelemetry Telemetry)>> invoke,
        Action<SegmentNode, string> consume, OpenRouterCeilingReasoningModel model, CancellationToken ct,
        bool reopenExecutionTerminals = false)
    {
        Directory.CreateDirectory(dir);
        var lengths = occurrences.Select(o => o.RawText.Length).ToArray();
        var treePath = Path.Combine(dir, "segment-tree-state.v1.json");
        var tree = LoadSegmentTree(treePath, sourceSha256, configurationSignature, lengths);
        if (reopenExecutionTerminals)
            tree.ReopenFailedTerminalsForExecutionChange();
        var telemetry = new List<RequestPacketTelemetry>();
        var splitCount = tree.AllNodes.Count(n => n.Status == SegmentRecoveryState.SplitParent);
        var successCount = 0;
        var reusedCount = 0;
        string? failureClass = null;

        while (tree.PendingLeaves.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var leaf = tree.PendingLeaves[0];
            if (leaf.Status == SegmentRecoveryState.WorkloadSplitRequired ||
                (leaf.Status == SegmentRecoveryState.TransientRetry && leaf.Attempts >= MaxTransientAttemptsPerLeaf))
            {
                if (!tree.CanSplit(leaf))
                {
                    tree.MarkFailedTerminal(leaf.SegmentId, leaf.FailureClass ?? "WORKLOAD_SHAPE_RECOVERY_FLOOR");
                    failureClass ??= leaf.FailureClass;
                }
                else
                {
                    tree.Split(leaf.SegmentId, HaloOccurrences);
                    // If an occurrence-boundary split is grossly unbalanced, immediately migrate
                    // that unsuccessful subtree to a deterministic character midpoint so the
                    // next request is genuinely smaller (roughly 30K/30K for this source).
                    tree.RebalanceGrossSplitIfUnsuccessful(leaf.SegmentId, HaloOccurrences);
                    splitCount++;
                }
                SaveSegmentTree(treePath, tree, sourceSha256, configurationSignature);
                continue;
            }

            var planHash = SegmentLeafPersistence.PlanHash(leaf.Owned);
            var key = new SegmentLeafArtifactKey(DocumentId, leaf.SegmentId, sourceSha256, configurationSignature, planHash, Model, ReasoningMode);
            if (SegmentLeafPersistence.TryLoad(dir, key, out var cached) && cached is not null)
            {
                tree.MarkSuccess(leaf.SegmentId, cached.RequestHash, cached.ResponseHash);
                consume(leaf, cached.PredictionJson);
                reusedCount++;
                SaveSegmentTree(treePath, tree, sourceSha256, configurationSignature);
                continue;
            }

            tree.MarkRunning(leaf.SegmentId);
            SaveSegmentTree(treePath, tree, sourceSha256, configurationSignature);
            var beforeTelemetry = model.Telemetry.Count;
            try
            {
                var (responseJson, requestTelemetry) = await invoke(leaf).ConfigureAwait(false);
                telemetry.Add(requestTelemetry);
                var requestHash = Sha256Text($"{strategy}:{DocumentId}:{leaf.SegmentId}:attempt-{leaf.Attempts + 1}");
                var responseHash = Sha256Text(responseJson);
                var manifest = JsonSerializer.Serialize(new
                {
                    documentId = DocumentId, strategy, pass = strategy, segmentId = leaf.SegmentId,
                    parentSegmentId = leaf.ParentSegmentId, depth = leaf.Depth, owned = leaf.Owned,
                    visible = leaf.Visible, requestHash, inventoryHash = Sha256Text(responseJson),
                }, JsonOptions);
                var execution = JsonSerializer.Serialize(new
                {
                    documentId = DocumentId, strategy, segmentId = leaf.SegmentId, attemptOrdinal = leaf.Attempts + 1,
                    finishReason = requestTelemetry.FinishReason ?? "NOT_EXPOSED", reportedModel = requestTelemetry.Model,
                    responseContentPresent = requestTelemetry.ResponseContentPresent ?? false,
                    structuredOutputParsed = requestTelemetry.StructuredOutputParsed ?? false,
                    timeoutDetected = requestTelemetry.TimeoutDetected ?? false,
                    streamStallDetected = requestTelemetry.StreamStallDetected ?? false,
                    outputLimitDetected = string.Equals(requestTelemetry.FinishReason, "length", StringComparison.OrdinalIgnoreCase),
                    inputTokens = requestTelemetry.ReportedInputTokens, reasoningTokens = requestTelemetry.ReportedReasoningTokens,
                    outputTokens = requestTelemetry.ReportedOutputTokens, latencyMs = requestTelemetry.ElapsedMs,
                }, JsonOptions);
                SegmentLeafPersistence.Save(dir, key, manifest, responseJson, execution, requestHash, responseHash);
                tree.MarkSuccess(leaf.SegmentId, requestHash, responseHash);
                consume(leaf, responseJson);
                successCount++;
            }
            catch (ReasoningCompletionException ex)
            {
                telemetry.AddRange(model.Telemetry.Skip(beforeTelemetry));
                tree.RecordFailure(leaf.SegmentId, ex.FailureClass);
                failureClass ??= ex.FailureClass;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                telemetry.AddRange(model.Telemetry.Skip(beforeTelemetry));
                tree.RecordFailure(leaf.SegmentId, ReasoningCompletionFailureClass.ProviderTotalTimeout);
                failureClass ??= ReasoningCompletionFailureClass.ProviderTotalTimeout;
            }
            catch (Exception ex) when (ex is FormatException or JsonException or HttpRequestException)
            {
                telemetry.AddRange(model.Telemetry.Skip(beforeTelemetry));
                tree.RecordFailure(leaf.SegmentId, ReasoningCompletionFailureClass.OtherProviderFailure);
                failureClass ??= ReasoningCompletionFailureClass.OtherProviderFailure;
            }
            SaveSegmentTree(treePath, tree, sourceSha256, configurationSignature);
        }

        var complete = tree.VerifyFullCoverage();
        return new SegmentPassResult(complete, complete ? null : failureClass ?? "SEGMENTED_RECOVERY_PARTIAL_BLOCKED",
            telemetry, tree.Leaves.Count, splitCount, successCount, reusedCount);
    }

    private static SegmentPacket BuildSegmentPacket(IReadOnlyList<ReasoningSourceOccurrence> occurrences, SegmentNode leaf)
    {
        var ownedByOccurrence = leaf.Owned.GroupBy(a => a.OccurrenceIndex)
            .ToDictionary(g => g.Key, g => (g.Min(a => a.CharStart), g.Max(a => a.CharEnd)));
        var visibleByOccurrence = leaf.Visible.GroupBy(a => a.OccurrenceIndex)
            .ToDictionary(g => g.Key, g => (g.Min(a => a.CharStart), g.Max(a => a.CharEnd)));
        var visible = new List<ReasoningSourceOccurrence>();
        var visibleWindow = new Dictionary<string, (int Start, int End)>(StringComparer.Ordinal);
        var ownedWindow = new Dictionary<string, (int Start, int End)>(StringComparer.Ordinal);
        var ownedSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (index, window) in visibleByOccurrence.OrderBy(x => x.Key))
        {
            var occurrence = occurrences[index];
            visible.Add(occurrence);
            visibleWindow[occurrence.SourceOccurrenceId] = window;
            if (ownedByOccurrence.TryGetValue(index, out var owned))
            {
                ownedSet.Add(occurrence.SourceOccurrenceId);
                ownedWindow[occurrence.SourceOccurrenceId] = owned;
            }
            else ownedWindow[occurrence.SourceOccurrenceId] = (window.Item1, window.Item1);
        }
        return new SegmentPacket(CeilingPacketBuilder.Build(visible, ownedSet, visibleWindow, ownedWindow), ownedSet, ownedWindow);
    }

    private static SegmentRecoveryTree LoadSegmentTree(string path, string sourceSha256, string configurationSignature, int[] lengths)
    {
        if (File.Exists(path))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                if (root.GetProperty("sourceSha256").GetString() == sourceSha256 && root.GetProperty("configurationSignature").GetString() == configurationSignature)
                {
                    var snapshot = root.GetProperty("nodes").Deserialize<SegmentRecoveryTree.SegmentNodeSnapshot[]>(JsonOptions) ?? [];
                    var normalized = snapshot.Select(x => x.Status == SegmentRecoveryState.Running ? x with { Status = SegmentRecoveryState.Pending } : x).ToArray();
                    var restored = SegmentRecoveryTree.RestoreFromSnapshot(DocumentId, lengths, MinimumSegmentCharacters, normalized, MinimumVisibleContextCharacters);
                    // Migrate the earlier occurrence-boundary split if it produced a grossly
                    // unbalanced, wholly unsuccessful subtree. Frozen SUCCESS descendants are
                    // deliberately never touched by this repair.
                    restored.RebalanceGrossSplitIfUnsuccessful(restored.RootSegmentId, HaloOccurrences);
                    return restored;
                }
            }
            catch (JsonException) { }
        }
        return new SegmentRecoveryTree(DocumentId, lengths, MinimumSegmentCharacters, MinimumVisibleContextCharacters);
    }

    private static void SaveSegmentTree(string path, SegmentRecoveryTree tree, string sourceSha256, string configurationSignature)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new
        {
            documentId = DocumentId, sourceSha256, configurationSignature, nodes = tree.ExportSnapshot(), savedUtc = DateTimeOffset.UtcNow,
        }, JsonOptions));
        File.Move(temp, path, true);
    }

    private static IReadOnlyList<CeilingHeadingProposal> GlobalInventoryForSegment(
        SegmentPacket segment, IReadOnlyList<(string SourceId, int Start, int End, string Role)> globalInventory)
    {
        var result = new List<CeilingHeadingProposal>();
        foreach (var item in globalInventory)
        {
            var binding = segment.Packet.Bindings.FirstOrDefault(x => x.SourceId == item.SourceId);
            if (binding is null || item.Start >= binding.VisibleEnd || item.End <= binding.VisibleStart) continue;
            var start = item.Start - binding.VisibleStart;
            var end = item.End - binding.VisibleStart;
            if (start >= 0 && end > start && end <= binding.RawTextLength - binding.VisibleStart)
                result.Add(new CeilingHeadingProposal(binding.LocalIndex, start, end, item.Role));
        }
        return result;
    }

    private static bool IsOwnedStart(SegmentPacket segment, int localIndex, int start)
    {
        var binding = segment.Packet.Bindings.FirstOrDefault(x => x.LocalIndex == localIndex);
        return binding is not null && binding.TryBind(start, start + 1, out _, out _, out var owned) && owned;
    }

    private static IReadOnlyList<(string SourceId, int Start, int End, string Role)> Globalize(
        IReadOnlyList<CeilingHeadingProposal> local, CeilingPacketResult fullPacket) => local
        .Select(p => fullPacket.Bindings.FirstOrDefault(b => b.LocalIndex == p.I) is { } b
            ? (b.SourceId, p.Start + b.VisibleStart, p.End + b.VisibleStart, p.Role)
            : ("", 0, 0, ""))
        .Where(x => x.Item1.Length > 0)
        .ToArray();

    private static async Task<StrategyMetric> RunS1SegmentedAsync(
        string output, OpenRouterCeilingReasoningModel model, string route, string configurationSignature, string gitSha,
        SourceDocument source, DocxPolicyState policy, IReadOnlyList<ReasoningSourceOccurrence> occurrences,
        CeilingPacketResult packetResult, IReadOnlySet<string> ownedSet, IReadOnlyList<CeilingHeadingProposal> s0LocalProposals,
        string goldPath, string sourceSha256, CancellationToken ct, bool reopenExecutionTerminals)
    {
        var dir = Path.Combine(output, "s1");
        var inventory = Globalize(s0LocalProposals, packetResult);
        var reviewItems = new List<(CeilingHeadingProposal Proposal, string Marker, string? CorrectsKey)>();
        var run = await ExecuteSegmentPassAsync("S1", Path.Combine(dir, "segments"), sourceSha256, configurationSignature, occurrences,
            async leaf =>
            {
                var segment = BuildSegmentPacket(occurrences, leaf);
                var localInventory = GlobalInventoryForSegment(segment, inventory);
                var inventoryJson = OmissionReviewPrompt.BuildInventoryJson(localInventory);
                var requestId = $"{OmissionReviewPrompt.ProtocolVersion}:{DocumentId}:{leaf.SegmentId}:{configurationSignature}:attempt-{leaf.Attempts + 1}";
                var (review, telemetry) = await model.CompleteOmissionReviewAsync(DocumentId, route, requestId,
                    segment.Packet.SerializedJson, inventoryJson, segment.Packet.SourceTextCharacters,
                    segment.OwnedSet.Count, segment.Packet.Bindings.Count, ct).ConfigureAwait(false);
                return (JsonSerializer.Serialize(review, JsonOptions), telemetry);
            },
            (leaf, responseJson) =>
            {
                var segment = BuildSegmentPacket(occurrences, leaf);
                var review = OmissionReviewResponseParser.Parse(responseJson);
                var localInventory = GlobalInventoryForSegment(segment, inventory);
                foreach (var item in review.Items)
                {
                    if (!IsOwnedStart(segment, item.I, item.Start)) continue;
                    var binding = segment.Packet.Bindings.FirstOrDefault(b => b.LocalIndex == item.I);
                    if (binding is null || !binding.TryBind(item.Start, item.End, out var globalStart, out var globalEnd, out var owned) || !owned) continue;
                    var fullIndex = packetResult.Bindings.First(b => b.SourceId == binding.SourceId).LocalIndex;
                    string? correctsKey = null;
                    if (item.Marker == OmissionReviewMarker.SpanCorrection && item.CorrectsProposalIndex is int idx && idx >= 0 && idx < localInventory.Count)
                    {
                        var old = localInventory[idx];
                        var oldBinding = segment.Packet.Bindings.FirstOrDefault(b => b.LocalIndex == old.I);
                        if (oldBinding is not null && oldBinding.TryBind(old.Start, old.End, out var oldStart, out var oldEnd, out _))
                            correctsKey = Key(oldBinding.SourceId, oldStart, oldEnd);
                    }
                    reviewItems.Add((new CeilingHeadingProposal(fullIndex, globalStart, globalEnd, item.Role), item.Marker, correctsKey));
                }
            }, model, ct, reopenExecutionTerminals);
        if (!run.Complete)
            throw new ReasoningCompletionException(run.FailureClass!, "S1 segmented recovery did not reach complete ownership coverage.",
                new ReasoningCompletionTelemetry { DocumentId = DocumentId, FailureClass = run.FailureClass! });

        var corrected = reviewItems.Where(x => x.CorrectsKey is not null).Select(x => x.CorrectsKey!).ToHashSet(StringComparer.Ordinal);
        var combined = s0LocalProposals.Where(p => !corrected.Contains(KeyForLocal(p, packetResult)))
            .Concat(reviewItems.Select(x => x.Proposal)).ToArray();
        return await FinalizeStrategyAsync("S1", dir, source, policy, occurrences, packetResult, ownedSet, combined, goldPath, gitSha, configurationSignature,
            run.Telemetry.Count, run.Telemetry, new { s0InventoryHash = Sha256Text(JsonSerializer.Serialize(inventory)), segmented = true, segmentCount = run.SegmentCount, splitCount = run.SplitCount }, ct, sourceSha256,
            run.SegmentCount, run.SplitCount, run.SuccessfulLeaves, run.ReusedLeaves);
    }

    private static string KeyForLocal(CeilingHeadingProposal p, CeilingPacketResult fullPacket)
    {
        var binding = fullPacket.Bindings.FirstOrDefault(b => b.LocalIndex == p.I);
        return binding is null ? $"invalid:{p.I}:{p.Start}:{p.End}" : Key(binding.SourceId, p.Start + binding.VisibleStart, p.End + binding.VisibleStart);
    }

    private static async Task<(StrategyMetric Metric, IReadOnlyList<CeilingHeadingProposal> UnionLocalProposals)> RunS2SegmentedAsync(
        string output, OpenRouterCeilingReasoningModel model, string route, string configurationSignature, string gitSha,
        SourceDocument source, DocxPolicyState policy, IReadOnlyList<ReasoningSourceOccurrence> occurrences,
        CeilingPacketResult packetResult, IReadOnlySet<string> ownedSet, IReadOnlyList<CeilingHeadingProposal> extractorALocal,
        string goldPath, string sourceSha256, CancellationToken ct, bool reopenExecutionTerminals)
    {
        var dir = Path.Combine(output, "s2");
        var extractorB = new List<CeilingHeadingProposal>();
        var run = await ExecuteSegmentPassAsync("S2", Path.Combine(dir, "extractor-b-segments"), sourceSha256, configurationSignature, occurrences,
            async leaf =>
            {
                var segment = BuildSegmentPacket(occurrences, leaf);
                var requestId = $"{ExhaustiveCoverageSemanticPrompt.ProtocolVersion}:{DocumentId}:{leaf.SegmentId}:{configurationSignature}:attempt-{leaf.Attempts + 1}";
                var (response, telemetry) = await model.CompleteCoverageSemanticAsync(DocumentId, route, requestId,
                    segment.Packet.SerializedJson, segment.Packet.SourceTextCharacters, segment.OwnedSet.Count, segment.Packet.Bindings.Count, ct).ConfigureAwait(false);
                return (JsonSerializer.Serialize(response, JsonOptions), telemetry);
            },
            (leaf, responseJson) =>
            {
                var segment = BuildSegmentPacket(occurrences, leaf);
                var response = CeilingSemanticResponseParser.Parse(responseJson);
                foreach (var p in response.Headings)
                {
                    if (!IsOwnedStart(segment, p.I, p.Start)) continue;
                    var binding = segment.Packet.Bindings.FirstOrDefault(b => b.LocalIndex == p.I);
                    if (binding is null || !binding.TryBind(p.Start, p.End, out var start, out var end, out var owned) || !owned) continue;
                    var fullIndex = packetResult.Bindings.First(b => b.SourceId == binding.SourceId).LocalIndex;
                    extractorB.Add(new CeilingHeadingProposal(fullIndex, start, end, p.Role));
                }
            }, model, ct, reopenExecutionTerminals);
        if (!run.Complete)
            throw new ReasoningCompletionException(run.FailureClass!, "S2 segmented recovery did not reach complete ownership coverage.",
                new ReasoningCompletionTelemetry { DocumentId = DocumentId, FailureClass = run.FailureClass! });
        var union = MultiPassProposalCombiner.UnionExtractors(extractorALocal, extractorB);
        var metric = await FinalizeStrategyAsync("S2", dir, source, policy, occurrences, packetResult, ownedSet, union, goldPath, gitSha, configurationSignature,
            run.Telemetry.Count, run.Telemetry, new { extractorAHash = Sha256Text(JsonSerializer.Serialize(extractorALocal)), extractorBResultHash = Sha256Text(JsonSerializer.Serialize(extractorB)), segmented = true, segmentCount = run.SegmentCount, splitCount = run.SplitCount }, ct, sourceSha256,
            run.SegmentCount, run.SplitCount, run.SuccessfulLeaves, run.ReusedLeaves);
        return (metric, union);
    }

    private static async Task<StrategyMetric> RunS3SegmentedAsync(
        string output, OpenRouterCeilingReasoningModel model, string route, string configurationSignature, string gitSha,
        SourceDocument source, DocxPolicyState policy, IReadOnlyList<ReasoningSourceOccurrence> occurrences,
        CeilingPacketResult packetResult, IReadOnlySet<string> ownedSet, IReadOnlyList<CeilingHeadingProposal> s2UnionLocal,
        string goldPath, string sourceSha256, CancellationToken ct, bool reopenExecutionTerminals)
    {
        var dir = Path.Combine(output, "s3");
        var candidatesById = MultiPassProposalCombiner.AssignCandidateIds(s2UnionLocal);
        var decisions = new List<VerifierProposalDecision>();
        var run = await ExecuteSegmentPassAsync("S3", Path.Combine(dir, "verifier-segments"), sourceSha256, configurationSignature, occurrences,
            async leaf =>
            {
                var segment = BuildSegmentPacket(occurrences, leaf);
                var visibleSourceIds = segment.Packet.Bindings.Select(b => b.SourceId).ToHashSet(StringComparer.Ordinal);
                var candidates = candidatesById.Where(kv => visibleSourceIds.Contains(packetResult.Bindings[kv.Value.I].SourceId))
                    .Where(kv => IsOwnedStartForFullProposal(segment, kv.Value, packetResult)).Select(kv => new { id = kv.Key, i = ToSegmentIndex(segment, packetResult.Bindings[kv.Value.I].SourceId), start = ToSegmentStart(segment, packetResult.Bindings[kv.Value.I].SourceId, kv.Value.Start, packetResult), end = ToSegmentStart(segment, packetResult.Bindings[kv.Value.I].SourceId, kv.Value.End, packetResult), role = kv.Value.Role }).ToArray();
                var requestId = $"{VerifierPrompt.ProtocolVersion}:{DocumentId}:{leaf.SegmentId}:{configurationSignature}:attempt-{leaf.Attempts + 1}";
                var (response, telemetry) = await model.CompleteVerifierAsync(DocumentId, route, requestId,
                    ExtractOccurrencesArray(segment.Packet.SerializedJson), JsonSerializer.Serialize(candidates), segment.Packet.SourceTextCharacters, segment.OwnedSet.Count, segment.Packet.Bindings.Count, ct).ConfigureAwait(false);
                return (JsonSerializer.Serialize(response, JsonOptions), telemetry);
            },
            (leaf, responseJson) =>
            {
                var verifier = VerifierResponseParser.Parse(responseJson);
                var segment = BuildSegmentPacket(occurrences, leaf);
                foreach (var decision in verifier.Decisions)
                    if (candidatesById.TryGetValue(decision.Id, out var candidate) && IsOwnedStartForFullProposal(segment, candidate, packetResult))
                        decisions.Add(decision);
            }, model, ct, reopenExecutionTerminals);
        if (!run.Complete)
            throw new ReasoningCompletionException(run.FailureClass!, "S3 segmented recovery did not reach complete ownership coverage.",
                new ReasoningCompletionTelemetry { DocumentId = DocumentId, FailureClass = run.FailureClass! });
        var survivors = MultiPassProposalCombiner.ApplyVerifierDecisions(candidatesById, new VerifierResponse(decisions));
        return await FinalizeStrategyAsync("S3", dir, source, policy, occurrences, packetResult, ownedSet, survivors, goldPath, gitSha, configurationSignature,
            run.Telemetry.Count, run.Telemetry, new { s2UnionHash = Sha256Text(JsonSerializer.Serialize(s2UnionLocal)), verifierResponseHash = Sha256Text(JsonSerializer.Serialize(decisions)), segmented = true, segmentCount = run.SegmentCount, splitCount = run.SplitCount }, ct, sourceSha256,
            run.SegmentCount, run.SplitCount, run.SuccessfulLeaves, run.ReusedLeaves);
    }

    private static bool IsOwnedStartForFullProposal(SegmentPacket segment, CeilingHeadingProposal proposal, CeilingPacketResult fullPacket)
    {
        var sourceId = fullPacket.Bindings[proposal.I].SourceId;
        var binding = segment.Packet.Bindings.FirstOrDefault(x => x.SourceId == sourceId);
        return binding is not null && proposal.Start >= binding.VisibleStart && proposal.Start < binding.VisibleEnd &&
            proposal.Start >= binding.OwnedStart && proposal.Start < binding.OwnedEnd;
    }

    private static int ToSegmentIndex(SegmentPacket segment, string sourceId) => segment.Packet.Bindings.First(x => x.SourceId == sourceId).LocalIndex;
    private static int ToSegmentStart(SegmentPacket segment, string sourceId, int globalStart, CeilingPacketResult fullPacket)
    {
        var binding = segment.Packet.Bindings.First(x => x.SourceId == sourceId);
        var fullBinding = fullPacket.Bindings.First(x => x.SourceId == sourceId);
        return globalStart - binding.VisibleStart;
    }

    // ============================== Strategy S1 (omission review) ==============================
    private static async Task<StrategyMetric> RunS1Async(
        string output, OpenRouterCeilingReasoningModel model, string route, string configurationSignature, string gitSha,
        SourceDocument source, DocxPolicyState policy,
        IReadOnlyList<ReasoningSourceOccurrence> occurrences, CeilingPacketResult packetResult, IReadOnlySet<string> ownedSet,
        IReadOnlyList<CeilingHeadingProposal> s0LocalProposals, string goldPath,
        string sourceSha256, CancellationToken ct)
    {
        var dir = Path.Combine(output, "s1");
        var inventoryJson = OmissionReviewPrompt.BuildInventoryJson(s0LocalProposals);
        var requestId = $"{OmissionReviewPrompt.ProtocolVersion}:{DocumentId}:{configurationSignature}";
        var (review, telemetry) = await model.CompleteOmissionReviewAsync(
            DocumentId, route, requestId, packetResult.SerializedJson, inventoryJson,
            packetResult.SourceTextCharacters, ownedSet.Count, occurrences.Count, ct);

        var combinedLocal = MultiPassProposalCombiner.ApplyOmissionReview(s0LocalProposals, review);
        return await FinalizeStrategyAsync("S1", dir, source, policy, occurrences, packetResult, ownedSet, combinedLocal, goldPath, gitSha, configurationSignature,
            providerCalls: 1, telemetry: [telemetry], rawInputHashes: new { s0InventoryHash = Sha256Text(inventoryJson), reviewResponseHash = Sha256Text(JsonSerializer.Serialize(review)) }, ct, sourceSha256);
    }

    // ============================ Strategy S2 (dual extractor union) ===========================
    private static async Task<(StrategyMetric Metric, IReadOnlyList<CeilingHeadingProposal> UnionLocalProposals)> RunS2Async(
        string output, OpenRouterCeilingReasoningModel model, string route, string configurationSignature, string gitSha,
        SourceDocument source, DocxPolicyState policy,
        IReadOnlyList<ReasoningSourceOccurrence> occurrences, CeilingPacketResult packetResult, IReadOnlySet<string> ownedSet,
        IReadOnlyList<CeilingHeadingProposal> extractorALocal, string goldPath,
        string sourceSha256, CancellationToken ct)
    {
        var dir = Path.Combine(output, "s2");
        var requestId = $"{ExhaustiveCoverageSemanticPrompt.ProtocolVersion}:{DocumentId}:{configurationSignature}";
        // Section 6: Extractor B sees ONLY the source packet -- never Extractor A's proposals.
        var (bResponse, telemetry) = await model.CompleteCoverageSemanticAsync(
            DocumentId, route, requestId, packetResult.SerializedJson,
            packetResult.SourceTextCharacters, ownedSet.Count, occurrences.Count, ct);

        var union = MultiPassProposalCombiner.UnionExtractors(extractorALocal, bResponse.Headings);
        var metric = await FinalizeStrategyAsync("S2", dir, source, policy, occurrences, packetResult, ownedSet, union, goldPath, gitSha, configurationSignature,
            providerCalls: 1, telemetry: [telemetry],
            rawInputHashes: new { extractorAHash = Sha256Text(JsonSerializer.Serialize(extractorALocal)), extractorBResultHash = Sha256Text(JsonSerializer.Serialize(bResponse)) }, ct, sourceSha256);
        return (metric, union);
    }

    // ============================== Strategy S3 (verifier/critic) ==============================
    private static async Task<StrategyMetric> RunS3Async(
        string output, OpenRouterCeilingReasoningModel model, string route, string configurationSignature, string gitSha,
        SourceDocument source, DocxPolicyState policy,
        IReadOnlyList<ReasoningSourceOccurrence> occurrences, CeilingPacketResult packetResult, IReadOnlySet<string> ownedSet,
        IReadOnlyList<CeilingHeadingProposal> s2UnionLocal, string goldPath,
        string sourceSha256, CancellationToken ct)
    {
        var dir = Path.Combine(output, "s3");
        var candidatesById = MultiPassProposalCombiner.AssignCandidateIds(s2UnionLocal);
        var candidatesJson = JsonSerializer.Serialize(candidatesById.Select(kv => new { id = kv.Key, i = kv.Value.I, start = kv.Value.Start, end = kv.Value.End, role = kv.Value.Role }),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var occurrencesJson = ExtractOccurrencesArray(packetResult.SerializedJson);
        var requestId = $"{VerifierPrompt.ProtocolVersion}:{DocumentId}:{configurationSignature}";
        var (verifier, telemetry) = await model.CompleteVerifierAsync(
            DocumentId, route, requestId, occurrencesJson, candidatesJson,
            packetResult.SourceTextCharacters, ownedSet.Count, occurrences.Count, ct);

        // Section 7: verifier decisions applied, but every surviving candidate STILL flows through
        // the unchanged binder/hard-validator/projection pipeline in FinalizeStrategyAsync below --
        // a critic KEEP is never itself acceptance.
        var survivors = MultiPassProposalCombiner.ApplyVerifierDecisions(candidatesById, verifier);
        return await FinalizeStrategyAsync("S3", dir, source, policy, occurrences, packetResult, ownedSet, survivors, goldPath, gitSha, configurationSignature,
            providerCalls: 1, telemetry: [telemetry],
            rawInputHashes: new { s2UnionHash = Sha256Text(JsonSerializer.Serialize(s2UnionLocal)), verifierResponseHash = Sha256Text(JsonSerializer.Serialize(verifier)) }, ct, sourceSha256);
    }

    // ================================== Shared finalize/score ===================================
    private static async Task<StrategyMetric> FinalizeStrategyAsync(
        string strategy, string dir, SourceDocument source, DocxPolicyState policy,
        IReadOnlyList<ReasoningSourceOccurrence> occurrences, CeilingPacketResult packetResult, IReadOnlySet<string> ownedSet,
        IReadOnlyList<CeilingHeadingProposal> localProposals, string goldPath, string gitSha, string configurationSignature,
        int providerCalls, IReadOnlyList<RequestPacketTelemetry> telemetry, object rawInputHashes, CancellationToken ct, string sourceSha256,
        int segmentCount = 1, int splitCount = 0, int successfulLeaves = 0, int reusedLeaves = 0)
    {
        var occurrenceById = occurrences.ToDictionary(o => o.SourceId, StringComparer.Ordinal);
        var bound = CeilingProposalBinder.Bind(localProposals, packetResult, ownedSet);
        var globalProposals = bound.Select(b => new ReasoningHeadingProposal
        {
            SourceId = b.SourceId, HeadingSpan = new StructuralSpan(b.Start, b.End),
            Text = occurrenceById[b.SourceId].RawText[b.Start..b.End], SemanticRole = b.Role, Confidence = 1,
        }).ToList();

        var materialized = ReasoningProposalMaterializer.Materialize(source, policy, globalProposals);
        var structureProjection = ReasoningTaskProjection.Project(materialized.Structure);
        var projectionById = structureProjection.ToDictionary(x => x.ProposalId, StringComparer.Ordinal);
        var projection = materialized.Validated.Select(row => projectionById.GetValueOrDefault(row.ElementId) ??
            new ReasoningProjectionDecision(row.ElementId, ReasoningTaskProjection.Excluded, "VALIDATION_REJECTED")).ToArray();
        var finalElements = ReasoningTaskProjection.ProjectContentHeadings(materialized.Structure)
            .OrderBy(x => x.Sources.Single().SourceOrdinal).ThenBy(x => x.Sources.Single().Span.Start).ThenBy(x => x.Id, StringComparer.Ordinal)
            .ToArray();
        var predictionKeys = finalElements.Select(e => Key(e.Sources.Single().SourceId, e.Sources.Single().Span.Start, e.Sources.Single().Span.End)).ToArray();
        var semanticPromptHash = SemanticPromptHash(strategy);
        var packetHash = Sha256Text(packetResult.SerializedJson);

        var predictionPath = Path.Combine(dir, "prediction.v1.json");
        await WriteJsonAsync(predictionPath, new
        {
            documentId = DocumentId, strategy, rawProposalCount = localProposals.Count, boundProposalCount = globalProposals.Count,
            finalHeadingCount = finalElements.Length, proposals = globalProposals, projection, goldReadBeforeFreeze = false,
        }, ct);
        var resultPath = Path.Combine(dir, "result.v1.json");
        await WriteJsonAsync(resultPath, new { documentId = DocumentId, strategy, headings = finalElements, goldReadBeforeFreeze = false }, ct);
        var executionPath = Path.Combine(dir, "execution.v1.json");
        await WriteJsonAsync(executionPath, new
        {
            documentId = DocumentId, strategy, providerCalls,
            segmentCount, splitCount, successfulLeaves, reusedLeaves,
            providerAttempts = telemetry.Count,
            providerSuccessfulResponses = telemetry.Count(t => t.StructuredOutputParsed == true),
            providerTimeouts = telemetry.Count(t => t.TimeoutDetected == true),
            providerStreamStalls = telemetry.Count(t => t.StreamStallDetected == true),
            providerOutputLimits = telemetry.Count(t => string.Equals(t.FinishReason, "length", StringComparison.OrdinalIgnoreCase)),
            providerTransportFailures = telemetry.Count(t => t.FailureClass is not null && t.FailureClass != ReasoningCompletionFailureClass.ProviderOutputLimit && t.TimeoutDetected != true),
            providerCallsCurrentRun = telemetry.Count, providerCallsHistoricalReused = 0,
            reasoningTokensKnown = telemetry.Count(t => t.ReportedReasoningTokens.HasValue),
            outputTokensKnown = telemetry.Count(t => t.ReportedOutputTokens.HasValue),
            inputTokensKnown = telemetry.Count(t => t.ReportedInputTokens.HasValue),
            usageUnknownAttempts = telemetry.Count(t => !t.ReportedInputTokens.HasValue || !t.ReportedOutputTokens.HasValue),
            inputTokens = telemetry.Sum(t => t.ReportedInputTokens ?? 0), outputTokens = telemetry.Sum(t => t.ReportedOutputTokens ?? 0),
            reasoningTokens = telemetry.Sum(t => t.ReportedReasoningTokens ?? 0),
            finishReasons = telemetry.Select(t => t.FinishReason ?? "NOT_EXPOSED").ToArray(),
            reportedModel = Model, latenciesMs = telemetry.Select(t => t.ElapsedMs).ToArray(),
            httpStatus = telemetry.Select(t => (object?)t.HttpStatus ?? "NOT_EXPOSED").ToArray(),
            outputLimitDetected = telemetry.Any(t => string.Equals(t.FinishReason, "length", StringComparison.OrdinalIgnoreCase)),
            responseContentPresent = telemetry.All(t => t.ResponseContentPresent == true),
            structuredOutputParsed = telemetry.All(t => t.StructuredOutputParsed == true),
            timeoutDetected = telemetry.Any(t => t.TimeoutDetected == true),
            streamStallDetected = telemetry.Any(t => t.StreamStallDetected == true),
            providerCallIds = telemetry.Select(t => t.ProviderCallId ?? "NOT_EXPOSED").ToArray(),
            goldReadBeforeFreeze = false,
        }, ct);

        var predictionSha = Sha256(predictionPath);
        var resultSha = Sha256(resultPath);
        var freezePath = Path.Combine(dir, "freeze.v1.json");
        await WriteJsonAsync(freezePath, new
        {
            documentId = DocumentId, strategy, gitSha, sourceSha256, model = Model, reasoningMode = ReasoningMode,
            configurationSignature, semanticPromptHash, packetHash,
            predictionSha256 = predictionSha, resultSha256 = resultSha, providerCalls,
            segmentCount, splitCount, successfulLeaves, reusedLeaves,
            providerAttempts = telemetry.Count,
            providerSuccessfulResponses = telemetry.Count(t => t.StructuredOutputParsed == true),
            providerTimeouts = telemetry.Count(t => t.TimeoutDetected == true),
            providerStreamStalls = telemetry.Count(t => t.StreamStallDetected == true),
            providerOutputLimits = telemetry.Count(t => string.Equals(t.FinishReason, "length", StringComparison.OrdinalIgnoreCase)),
            providerTransportFailures = telemetry.Count(t => t.FailureClass is not null && t.FailureClass != ReasoningCompletionFailureClass.ProviderOutputLimit && t.TimeoutDetected != true),
            providerCallsCurrentRun = telemetry.Count, providerCallsHistoricalReused = 0,
            reasoningTokensKnown = telemetry.Count(t => t.ReportedReasoningTokens.HasValue),
            outputTokensKnown = telemetry.Count(t => t.ReportedOutputTokens.HasValue),
            inputTokensKnown = telemetry.Count(t => t.ReportedInputTokens.HasValue),
            usageUnknownAttempts = telemetry.Count(t => !t.ReportedInputTokens.HasValue || !t.ReportedOutputTokens.HasValue),
            inputTokens = telemetry.Sum(t => t.ReportedInputTokens ?? 0), outputTokens = telemetry.Sum(t => t.ReportedOutputTokens ?? 0),
            reasoningTokens = telemetry.Sum(t => t.ReportedReasoningTokens ?? 0),
            finishReasons = telemetry.Select(t => t.FinishReason ?? "NOT_EXPOSED").ToArray(),
            executionMode = "FULL_CONTEXT_SEMANTIC_ONLY", rawInputHashes, goldReadBeforeFreeze = false, frozenUtc = DateTimeOffset.UtcNow,
        }, ct);

        // ------------------------------- FIREWALL: Gold read below only ------------------------
        // The prediction and freeze are durable before the scorer can open Strict Gold.
        var gold = ReasoningGoldArtifactLoader.LoadOccurrence(goldPath);
        var goldKeys = gold.Where(x => x.HeadingSpan is not null)
            .Select(x => Key(x.SourceId, x.HeadingSpan!.Start, x.HeadingSpan!.End)).ToArray();
        var score = Score(goldKeys, predictionKeys);
        var lossByFn = ClassifyFirstLoss(gold, globalProposals, materialized, finalElements, projection);
        var trueModelOmission = lossByFn.Count(x => x == "MODEL_OMISSION");
        var modelWrongSpan = lossByFn.Count(x => x == "MODEL_WRONG_SPAN");
        var systemLoss = lossByFn.Count(x => x.StartsWith("SYSTEM_", StringComparison.Ordinal));
        var predictedSet = predictionKeys.ToHashSet(StringComparer.Ordinal);
        var goldSet = goldKeys.ToHashSet(StringComparer.Ordinal);
        var falsePositiveKeys = predictedSet.Except(goldSet).ToArray();
        var modelFalsePositive = falsePositiveKeys.Length;
        var falsePositiveDiagnostics = ClassifyFalsePositives(gold, finalElements, goldKeys);

        await WriteJsonAsync(Path.Combine(dir, "score.v1.json"), new
        {
            documentId = DocumentId, strategy, goldCount = goldKeys.Length, tp = score.TP, fp = score.FP, fn = score.FN,
            precision = score.P, recall = score.R, f1 = score.F1, goldReadBeforeFreeze = false,
        }, ct);
        await WriteJsonAsync(Path.Combine(dir, "first-loss.v1.json"), new
        {
            documentId = DocumentId, strategy, trueModelOmission, modelWrongSpan, systemInducedLoss = systemLoss,
            modelFalsePositive, fnClassification = lossByFn, fpClassification = falsePositiveDiagnostics, goldReadBeforeFreeze = false,
        }, ct);

        return new StrategyMetric(strategy, localProposals.Count, globalProposals.Count, finalElements.Length,
            score.TP, score.FP, score.FN, score.P, score.R, score.F1, trueModelOmission, modelWrongSpan, modelFalsePositive,
            systemLoss, providerCalls, telemetry.Sum(t => t.ReportedReasoningTokens ?? 0), telemetry.Sum(t => t.ReportedOutputTokens ?? 0),
            telemetry.Sum(t => t.ElapsedMs), segmentCount, splitCount, successfulLeaves, reusedLeaves);
    }

    private static StrategyMetric ReadS0Metric(string s0ScorePath, int rawProposalCount)
    {
        if (!File.Exists(s0ScorePath))
            return new StrategyMetric("S0", rawProposalCount, rawProposalCount, 0, 0, 0, 71, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        using var doc = JsonDocument.Parse(File.ReadAllText(s0ScorePath));
        var root = doc.RootElement;
        var tp = root.GetProperty("tp").GetInt32();
        var fp = root.GetProperty("fp").GetInt32();
        var fn = root.GetProperty("fn").GetInt32();
        var p = root.GetProperty("precision").GetDouble();
        var r = root.GetProperty("recall").GetDouble();
        var f1 = root.GetProperty("f1").GetDouble();
        // From the frozen zero-F1 forensic audit (offline, diagnostic-only, never re-derived here).
        return new StrategyMetric("S0", rawProposalCount, rawProposalCount, tp + fp, tp, fp, fn, p, r, f1, 69, 2, fp, 0, 0, 16_409, 18_896, 0, 1, 0, 0, 1);
    }

    private static IEnumerable<string> ClassifyFirstLoss(
        IReadOnlyList<ReasoningGoldOccurrence> gold, IReadOnlyList<ReasoningHeadingProposal> proposals,
        (ValidatedStructure Structure, IReadOnlyList<ReasoningValidatedProposal> Validated) materialized,
        IReadOnlyList<ValidatedStructuralElement> finalElements, IReadOnlyList<ReasoningProjectionDecision> projectionById)
    {
        var predicted = finalElements.Select(e => Key(e.Sources.Single().SourceId, e.Sources.Single().Span.Start, e.Sources.Single().Span.End)).ToHashSet(StringComparer.Ordinal);
        var rows = materialized.Validated.ToDictionary(x => x.ElementId, StringComparer.Ordinal);
        foreach (var item in gold.Where(x => x.HeadingSpan is not null))
        {
            var key = Key(item.SourceId, item.HeadingSpan!.Start, item.HeadingSpan!.End);
            if (predicted.Contains(key)) continue;
            var exact = proposals.FirstOrDefault(p => Key(p.SourceId, p.HeadingSpan.Start, p.HeadingSpan.End) == key);
            if (exact is null)
            {
                var near = proposals.Any(p => p.SourceId == item.SourceId &&
                    (p.HeadingSpan.Start == item.HeadingSpan!.Start || (item.ExactText is not null && p.Text.Contains(item.ExactText, StringComparison.Ordinal))));
                yield return near ? "MODEL_WRONG_SPAN" : "MODEL_OMISSION";
                continue;
            }
            var id = ReasoningProposalMaterializer.ElementId(exact);
            if (!rows.TryGetValue(id, out var row) || !row.Accepted) yield return "SYSTEM_VALIDATOR_LOSS";
            else if (projectionById.Any(x => x.ProposalId == id && x.Status == ReasoningTaskProjection.Excluded)) yield return "SYSTEM_PROJECTION_LOSS";
            else yield return "SYSTEM_BINDING_LOSS";
        }
    }

    // ================================== Shared small helpers ===================================
    private static Dictionary<string, int> BuildOccIndexBySourceId(IReadOnlyList<ReasoningSourceOccurrence> occurrences, CeilingPacketResult packetResult)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var binding in packetResult.Bindings)
            result[binding.SourceId] = binding.LocalIndex;
        return result;
    }

    private static IReadOnlyList<CeilingHeadingProposal> ToLocal(
        IReadOnlyList<(string SourceId, int Start, int End, string Role)> globalProposals,
        IReadOnlyDictionary<string, int> occIndexBySourceId, IReadOnlyList<ReasoningSourceOccurrence> occurrences)
    {
        var result = new List<CeilingHeadingProposal>();
        foreach (var p in globalProposals)
            if (occIndexBySourceId.TryGetValue(p.SourceId, out var i))
                result.Add(new CeilingHeadingProposal(i, p.Start, p.End, p.Role));
        return result;
    }

    private static IReadOnlyList<(string SourceId, int Start, int End, string Role)> ReadS0GlobalProposals(string predictionPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(predictionPath));
        var result = new List<(string, int, int, string)>();
        foreach (var p in doc.RootElement.GetProperty("proposals").EnumerateArray())
        {
            var sourceId = p.GetProperty("sourceId").GetString()!;
            var span = p.GetProperty("headingSpan");
            var start = span.GetProperty("start").GetInt32();
            var end = span.GetProperty("end").GetInt32();
            var role = p.GetProperty("semanticRole").GetString()!;
            result.Add((sourceId, start, end, role));
        }
        return result;
    }

    private static string ExtractOccurrencesArray(string packetJson)
    {
        using var doc = JsonDocument.Parse(packetJson);
        return doc.RootElement.GetProperty("occurrences").GetRawText();
    }

    private static string Key(string sourceId, int start, int end) => $"{sourceId}:{start}:{end}";

    private static (int TP, int FP, int FN, double P, double R, double F1) Score(IReadOnlyList<string> gold, IReadOnlyList<string> predicted)
    {
        var g = gold.ToHashSet(StringComparer.Ordinal); var p = predicted.ToHashSet(StringComparer.Ordinal);
        var tp = g.Intersect(p).Count(); var fp = p.Except(g).Count(); var fn = g.Except(p).Count();
        var precision = tp + fp == 0 ? 0d : (double)tp / (tp + fp); var recall = tp + fn == 0 ? 0d : (double)tp / (tp + fn);
        var f1 = precision + recall == 0 ? 0d : 2 * precision * recall / (precision + recall);
        return (tp, fp, fn, precision, recall, f1);
    }

    private static string ConfigurationSignature(OpenRouterModelCapability capability) => Sha256Text(
        $"A99_QWEN9B_MULTIPASS|{Model}|{CeilingSemanticPrompt.ProtocolVersion}|{OmissionReviewPrompt.ProtocolVersion}|{ExhaustiveCoverageSemanticPrompt.ProtocolVersion}|{VerifierPrompt.ProtocolVersion}|temperature=0|reasoning={capability.SelectedReasoningEffort}|context={capability.ContextLength}");

    private static string SemanticPromptHash(string strategy) => strategy switch
    {
        "S1" => Sha256Text(OmissionReviewPrompt.ProtocolVersion + "\n" + OmissionReviewPrompt.System + "\n" + JsonSerializer.Serialize(OmissionReviewPrompt.Schema())),
        "S2" => Sha256Text(ExhaustiveCoverageSemanticPrompt.ProtocolVersion + "\n" + ExhaustiveCoverageSemanticPrompt.System + "\n" + JsonSerializer.Serialize(ExhaustiveCoverageSemanticPrompt.Schema())),
        "S3" => Sha256Text(VerifierPrompt.ProtocolVersion + "\n" + VerifierPrompt.System + "\n" + JsonSerializer.Serialize(VerifierPrompt.Schema())),
        _ => Sha256Text(CeilingSemanticPrompt.ProtocolVersion + "\n" + CeilingSemanticPrompt.System + "\n" + JsonSerializer.Serialize(CeilingSemanticPrompt.Schema())),
    };

    private static string CurrentGitSha(string repoRoot)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "git", Arguments = "rev-parse HEAD", WorkingDirectory = repoRoot,
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("GIT_SHA_UNAVAILABLE");
        var sha = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(sha)) throw new InvalidOperationException("GIT_SHA_UNAVAILABLE");
        return sha;
    }

    private static IReadOnlyList<object> ClassifyFalsePositives(
        IReadOnlyList<ReasoningGoldOccurrence> gold, IReadOnlyList<ValidatedStructuralElement> finalElements,
        IReadOnlyList<string> goldKeys)
    {
        var goldBySource = gold.Where(x => x.HeadingSpan is not null)
            .GroupBy(x => x.SourceId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(x => x.HeadingSpan!).ToArray(), StringComparer.Ordinal);
        var goldSourceIds = goldBySource.Keys.ToHashSet(StringComparer.Ordinal);
        var goldKeySet = goldKeys.ToHashSet(StringComparer.Ordinal);
        var result = new List<object>();
        foreach (var element in finalElements)
        {
            var source = element.Sources.Single();
            var key = Key(source.SourceId, source.Span.Start, source.Span.End);
            if (goldKeySet.Contains(key)) continue;
            var kind = !goldSourceIds.Contains(source.SourceId) ? "WRONG_SOURCE" :
                goldBySource[source.SourceId].Any(span => source.Span.Start >= span.Start && source.Span.End <= span.End) ? "PARTIAL_HEADING_SPAN" :
                goldBySource[source.SourceId].Any(span => source.Span.Start <= span.Start && source.Span.End >= span.End) ? "SUPERSET_HEADING_SPAN" :
                "OTHER";
            result.Add(new { key, classification = kind });
        }
        return result;
    }

    private static void PrintComparison(IReadOnlyList<StrategyMetric> strategies)
    {
        Console.WriteLine("Strategy | TP | FP | FN | P | R | F1 | ModelOmission | SpanError | SystemLoss | Calls | ReasoningTokens");
        foreach (var s in strategies.OrderBy(x => x.Strategy, StringComparer.Ordinal))
            Console.WriteLine(string.Join(" | ", s.Strategy, s.TP, s.FP, s.FN,
                s.Precision.ToString("0.######", CultureInfo.InvariantCulture),
                s.Recall.ToString("0.######", CultureInfo.InvariantCulture),
                s.F1.ToString("0.######", CultureInfo.InvariantCulture), s.TrueModelOmission,
                s.ModelWrongSpan, s.SystemInducedLoss, s.ProviderCalls, s.ReasoningTokens));
        var bestRecall = strategies.OrderByDescending(x => x.Recall).ThenByDescending(x => x.F1).First();
        var bestF1 = strategies.OrderByDescending(x => x.F1).ThenByDescending(x => x.Recall).First();
        Console.WriteLine($"BEST_RECALL_STRATEGY={bestRecall.Strategy}");
        Console.WriteLine($"BEST_F1_STRATEGY={bestF1.Strategy}");
    }

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Sha256Text(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static async Task WriteJsonAsync(string path, object value, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine);
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, FileOptions.SequentialScan);
        await stream.WriteAsync(bytes, ct); await stream.FlushAsync(ct); stream.Flush(true);
    }

    private static async Task<int> WriteBlockedAsync(string output, string reason, CancellationToken ct)
    {
        await WriteJsonAsync(Path.Combine(output, "summary.v1.json"), new
        {
            schemaVersion = "a99-qwen9b-multipass-doc0205-v1", status = "QWEN9B_MULTI_PASS_EXECUTION_BLOCKED",
            reason, documentId = DocumentId, goldReadBeforeFreeze = false, completedUtc = DateTimeOffset.UtcNow,
        }, ct);
        Console.WriteLine("FINAL_CLASSIFICATION=QWEN9B_MULTI_PASS_EXECUTION_BLOCKED");
        return 1;
    }

    private static Task WriteStrategyBlockedAsync(string output, string strategy, ReasoningCompletionException ex,
        OpenRouterCeilingReasoningModel model, CancellationToken ct)
    {
        var passType = strategy switch
        {
            "S1" => "OMISSION_REVIEW",
            "S2" => "COVERAGE_SEMANTIC",
            "S3" => "VERIFIER",
            _ => strategy,
        };
        var strategyTelemetry = model.Telemetry.Where(t => string.Equals(t.PassType, passType, StringComparison.Ordinal)).ToArray();
        return WriteJsonAsync(Path.Combine(output, strategy.ToLowerInvariant(), "execution.v1.json"), new
        {
            documentId = DocumentId, strategy, status = "BLOCKED", providerCalls = strategyTelemetry.Length,
            telemetry = strategyTelemetry, historicalTree = ReadSegmentAudit(output, strategy),
            reason = ex.FailureClass.ToString(), goldReadBeforeFreeze = false,
        }, ct);
    }

    private static object? ReadSegmentAudit(string output, string strategy)
    {
        var treePath = strategy switch
        {
            "S1" => Path.Combine(output, "s1", "segments", "segment-tree-state.v1.json"),
            "S2" => Path.Combine(output, "s2", "extractor-b-segments", "segment-tree-state.v1.json"),
            "S3" => Path.Combine(output, "s3", "verifier-segments", "segment-tree-state.v1.json"),
            _ => "",
        };
        if (!File.Exists(treePath)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(treePath));
        var nodes = doc.RootElement.GetProperty("nodes").Deserialize<SegmentRecoveryTree.SegmentNodeSnapshot[]>(JsonOptions) ?? [];
        return new
        {
            nodeCount = nodes.Length,
            leafCount = nodes.Count(n => n.Status != SegmentRecoveryState.SplitParent),
            attempts = nodes.Sum(n => n.HistoricalAttempts + n.Attempts),
            historicalAttempts = nodes.Sum(n => n.HistoricalAttempts),
            currentAttempts = nodes.Sum(n => n.Attempts),
            statuses = nodes.GroupBy(n => n.Status).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            failureClasses = nodes.Where(n => n.FailureClass is not null).GroupBy(n => n.FailureClass!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
        };
    }

    private static int ReadAccounting(string output, string property)
    {
        var path = Path.Combine(output, "provider-diagnosis.v1.json");
        if (!File.Exists(path)) return 0;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty(property, out var value) && value.TryGetInt32(out var result) ? result : 0;
        }
        catch (JsonException) { return 0; }
    }

    private sealed record InventoryItem(string DocumentId, string SourcePath, string SourceSha256);

    private static InventoryItem[] ReadInventory(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("documents").EnumerateArray()
            .Select(x => new InventoryItem(x.GetProperty("documentId").GetString()!, x.GetProperty("sourcePath").GetString()!, x.GetProperty("sourceSha256").GetString()!))
            .ToArray();
    }

    private sealed record StrategyMetric(
        string Strategy, int RawProposalCount, int BoundProposalCount, int FinalHeadingCount,
        int TP, int FP, int FN, double Precision, double Recall, double F1,
        int TrueModelOmission, int ModelWrongSpan, int ModelFalsePositive, int SystemInducedLoss,
        int ProviderCalls, int ReasoningTokens, int OutputTokens, long WallTimeMs,
        int SegmentCount, int SplitCount, int SuccessfulLeaves, int ReusedLeaves);
}
