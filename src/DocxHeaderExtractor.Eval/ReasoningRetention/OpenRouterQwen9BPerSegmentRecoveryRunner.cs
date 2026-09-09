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
/// Successor to <see cref="OpenRouterQwen9BExecutableCeilingRunner"/>: replaces its rung-atomic
/// ladder (discard every sibling in a rung when any one sibling fails) with the segment-atomic
/// <see cref="SegmentRecoveryTree"/>. Each source-occurrence range gets its own independent
/// lifecycle: a leaf that reaches SUCCESS is frozen to disk immediately and never re-run; a leaf
/// that fails workload-shape (timeout/output-limit) has only itself split, recursively, down to a
/// capability-aware character floor. Model, reasoning mode (Ceiling, never disabled), and the
/// semantic/hierarchy prompts are unchanged from the frozen ceiling route -- this file changes
/// only execution shape and persistence. DOC-0258's frozen FULL_CONTEXT result is never rerun
/// here; this runner is scoped to DOC-0205 and DOC-0264 only.
/// </summary>
public static class OpenRouterQwen9BPerSegmentRecoveryRunner
{
    private const string OutputRoot = "eval/a99-closed-loop/openrouter-qwen35-9b-per-segment-recovery";
    private const string InventoryPath = "eval/a99-dataset/document-inventory.v1.json";
    private const string Model = "qwen/qwen3.5-9b";
    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";
    private const string ReasoningMode = "CEILING_NEVER_DISABLED";
    private static readonly string[] SelectedIds = ["DOC-0205", "DOC-0264"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private const int MaxTransientAttemptsPerLeaf = 2;
    private const int MinimumSegmentCharacters = 2_000;
    private const int HaloOccurrences = 1;

    public static Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        // Section 8/operational note: bounded wall clock so a single invocation never runs
        // unbounded against the provider. A long document is expected to need multiple bounded
        // invocations to reach full leaf coverage -- persisted leaves (section 7) make each
        // invocation resume exactly where the previous one left off, never re-running a SUCCESS.
        var minutes = double.TryParse(Environment.GetEnvironmentVariable("A99_PER_SEGMENT_RECOVERY_WALLCLOCK_MINUTES"), out var m) ? m : 25;
        return RunAsync(repoRoot, TimeSpan.FromMinutes(minutes), ct);
    }

    public static async Task<int> RunAsync(string repoRoot, TimeSpan wallClockBudget, CancellationToken ct = default)
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
            ContextSize = 262_144, MaxOutputTokens = 48_000, RequestTimeoutSeconds = 300,
            TransientRequestRetries = 2, MaxParallelRequests = 1, SendChatTemplateKwargs = false,
        };

        if (string.IsNullOrWhiteSpace(key))
            return await WriteBlockedAsync(output, DocumentCompletionState.ProviderUnavailable, "OPENROUTER_API_KEY_MISSING", ct);

        using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = Timeout.InfiniteTimeSpan };
        var preflight = await OpenRouterModelCapabilityResolver.ResolveAsync(options, http, ct);
        Console.WriteLine($"MODEL={Model}");
        if (preflight.Capability is { } capLog)
            Console.WriteLine($"CONTEXT_LENGTH={capLog.ContextLength} REASONING_SUPPORTED={capLog.ReasoningSupported}");

        await WriteJsonAsync(Path.Combine(output, "config.v1.json"), new
        {
            schemaVersion = "a99-openrouter-qwen35-9b-per-segment-recovery-v1",
            requestedModel = Model, endpoint = Endpoint, provider = "OpenRouter",
            selectedDocuments = SelectedIds, apiKeyPresent = true, modelFallback = "NONE",
            reasoningMode = ReasoningMode, goldReadBeforeFreeze = false,
            minimumSegmentCharacters = MinimumSegmentCharacters,
            capability = preflight.Capability, preflightAvailable = preflight.Available,
            preflightClassification = preflight.Classification, preflightReason = preflight.Reason,
            note = "segment-atomic recovery: a successful leaf is frozen and reused, only the failing leaf is split -- DOC-0258 is frozen and not rerun here",
            startedUtc = DateTimeOffset.UtcNow,
        }, ct);

        if (!preflight.Available || preflight.Capability is null)
            return await WriteBlockedAsync(output, DocumentCompletionState.ProviderUnavailable, preflight.Reason, ct);
        if (!preflight.Capability.ReasoningSupported)
            return await WriteBlockedAsync(output, DocumentCompletionState.ProviderUnavailable, "REASONING_NOT_REPORTED_SUPPORTED", ct);
        if (!string.Equals(preflight.Capability.ModelId, Model, StringComparison.Ordinal))
            return await WriteBlockedAsync(output, DocumentCompletionState.ProviderUnavailable, "MODEL_IDENTITY_MISMATCH", ct);

        using var model = new OpenRouterCeilingReasoningModel(options, preflight.Capability, http);
        var configurationSignature = ConfigurationSignature(model.Capability);

        using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadlineCts.CancelAfter(wallClockBudget);

        var documentStates = new List<DocumentRecoveryState>();
        foreach (var item in selected)
        {
            var state = await PrepareDocumentAsync(repoRoot, output, item, model, configurationSignature, ct);
            ReconcileSuccessfulLeavesFromDisk(output, state, configurationSignature);
            documentStates.Add(state);
        }

        var deadlineHit = false;
        try
        {
            await ExecuteFairRoundRobinAsync(model, output, documentStates, configurationSignature, deadlineCts.Token);
        }
        catch (OperationCanceledException) when (deadlineCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            deadlineHit = true;
            Console.Error.WriteLine("PER_SEGMENT_RECOVERY_WALL_CLOCK_BUDGET_EXCEEDED");
        }

        foreach (var state in documentStates)
            SaveTreeState(output, state, configurationSignature);

        var documents = new List<DocumentMetric>();
        foreach (var state in documentStates)
            documents.Add(await FinalizeDocumentAsync(repoRoot, output, state, model, configurationSignature, ct));

        var exact = documents.Where(x => x.exactStatus == "EVALUABLE").ToArray();
        var micro = Score(exact.SelectMany(x => x.GoldKeys).ToArray(), exact.SelectMany(x => x.PredictionKeys).ToArray());
        var systemLossTotal = documents.Sum(x => x.systemLossCount);
        var anyBlocked = documents.Any(x => x.finalExecutionMode is DocumentCompletionState.SegmentedRecoveryPartialBlocked or DocumentCompletionState.SemanticCompleteHierarchyBlocked);
        var classification = deadlineHit || anyBlocked
            ? (documents.Any(x => x.reusedLeafCount + x.successfulLeafCount > 0) ? "QWEN9B_PER_SEGMENT_RECOVERY_PARTIAL_BLOCKED" : "BLOCKED_PROVIDER_UNAVAILABLE")
            : systemLossTotal > 0
                ? "QWEN9B_PER_SEGMENT_RECOVERY_SYSTEM_LOSS_REMAINS"
                : "QWEN9B_PER_SEGMENT_RECOVERY_COMPLETE";

        var previous = new { doc0205Calls = 6, doc0205ReasoningTokens = 16_409, doc0205OutputTokens = 18_896, doc0264Calls = 16, doc0264ReasoningTokens = 55_687, doc0264OutputTokens = 54_195 };
        var totalProviderCalls = model.ProviderCalls;
        var totalReasoningTokens = model.Telemetry.Sum(x => x.ReportedReasoningTokens ?? 0);
        var totalOutputTokens = model.Telemetry.Sum(x => x.ReportedOutputTokens ?? 0);
        var previousTotalCalls = previous.doc0205Calls + previous.doc0264Calls;
        var previousReasoningTokens = previous.doc0205ReasoningTokens + previous.doc0264ReasoningTokens;

        await WriteJsonAsync(Path.Combine(output, "summary.v1.json"), new
        {
            schemaVersion = "a99-openrouter-qwen35-9b-per-segment-recovery-v1",
            status = classification, requestedModel = Model, provider = "OpenRouter", selectedDocuments = SelectedIds,
            documents, microExact = micro, systemInducedLossTotal = systemLossTotal, wallClockBudgetHit = deadlineHit,
            openRouterCalls = totalProviderCalls,
            inputTokens = model.Telemetry.Sum(x => x.ReportedInputTokens ?? 0),
            outputTokens = totalOutputTokens,
            reasoningTokens = totalReasoningTokens,
            compareWithPreviousRungAtomicCampaign = new
            {
                previousProviderCalls = previousTotalCalls, previousReasoningTokens,
                avoidedProviderCalls = previousTotalCalls - totalProviderCalls,
                avoidedReasoningTokens = previousReasoningTokens - totalReasoningTokens,
            },
            capability = preflight.Capability, requestTelemetry = model.Telemetry, goldReadBeforeFreeze = false,
            completedUtc = DateTimeOffset.UtcNow,
        }, ct);
        Console.WriteLine($"FINAL_CLASSIFICATION={classification}");
        return classification is "QWEN9B_PER_SEGMENT_RECOVERY_COMPLETE" or "QWEN9B_PER_SEGMENT_RECOVERY_SYSTEM_LOSS_REMAINS" ? 0 : 1;
    }

    private sealed class DocumentRecoveryState
    {
        public required InventoryItem Item { get; init; }
        public required SourceDocument Source { get; init; }
        public required DocxPolicyState Policy { get; init; }
        public required IReadOnlyList<ReasoningSourceOccurrence> Occurrences { get; init; }
        public required Dictionary<string, ReasoningSourceOccurrence> OccurrenceById { get; init; }
        public required SegmentRecoveryTree Tree { get; init; }
        public required string DocDir { get; init; }
        public int SuccessfulLeafCount;
        public int FailedLeafCount;
        public int SplitCount;
        public int ReusedLeafCount;
        public int ProviderCalls;
        public readonly List<(string SegmentId, string SourceId, int Start, int End, string Role)> RawProposals = [];
        public readonly Dictionary<string, string> ResponseHashBySegment = new(StringComparer.Ordinal);
        public readonly Stopwatch Stopwatch = Stopwatch.StartNew();
    }

    private static Task<DocumentRecoveryState> PrepareDocumentAsync(
        string repoRoot, string output, InventoryItem item, OpenRouterCeilingReasoningModel model, string configurationSignature, CancellationToken ct)
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
        var pack = ReasoningContextBuilder.Build(source, policy, int.MaxValue / 2, int.MaxValue / 2, expandOwnedPerOccurrence: false);
        var occurrenceById = pack.Occurrences.ToDictionary(o => o.SourceOccurrenceId, StringComparer.Ordinal);
        var lengths = pack.Occurrences.Select(o => o.RawText.Length).ToArray();
        var tree = LoadOrCreateTree(output, item, lengths, configurationSignature);

        return Task.FromResult(new DocumentRecoveryState
        {
            Item = item, Source = source, Policy = policy, Occurrences = pack.Occurrences,
            OccurrenceById = occurrenceById, Tree = tree, DocDir = docDir,
        });
    }

    /// <summary>Section 7/14 resume: a prior invocation's tree (splits performed, leaves already
    /// SUCCESS/FAILED_TERMINAL) is restored verbatim when the source and configuration signature
    /// still match, so a bounded re-invocation continues exactly where the last one stopped
    /// instead of re-attempting the whole-document root from scratch every time.</summary>
    private static string TreeStatePath(string output, string documentId) =>
        Path.Combine(output, "documents", documentId, "segment-tree-state.v1.json");

    private static SegmentRecoveryTree LoadOrCreateTree(string output, InventoryItem item, int[] lengths, string configurationSignature)
    {
        var path = TreeStatePath(output, item.DocumentId);
        if (File.Exists(path))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                if (root.GetProperty("sourceSha256").GetString() == item.SourceSha256 &&
                    root.GetProperty("configurationSignature").GetString() == configurationSignature)
                {
                    var snapshot = root.GetProperty("nodes").Deserialize<SegmentRecoveryTree.SegmentNodeSnapshot[]>(JsonOptions) ?? [];
                    // A node persisted mid-flight as RUNNING represents an attempt interrupted by
                    // the previous invocation's wall-clock deadline -- nothing is genuinely still
                    // running across a process restart, so normalize it back to PENDING or it
                    // would never be picked up again (RUNNING is excluded from PendingLeaves).
                    var normalized = snapshot.Select(n => n.Status == SegmentRecoveryState.Running ? n with { Status = SegmentRecoveryState.Pending } : n).ToArray();
                    return SegmentRecoveryTree.RestoreFromSnapshot(item.DocumentId, lengths, MinimumSegmentCharacters, normalized);
                }
            }
            catch (JsonException)
            {
                // Corrupt/partial state file from an interrupted write -- fall back to a fresh
                // tree rather than propagating a resume failure.
            }
        }
        return new SegmentRecoveryTree(item.DocumentId, lengths, MinimumSegmentCharacters);
    }

    private static void SaveTreeState(string output, DocumentRecoveryState state, string configurationSignature)
    {
        var path = TreeStatePath(output, state.Item.DocumentId);
        var json = JsonSerializer.Serialize(new
        {
            documentId = state.Item.DocumentId, sourceSha256 = state.Item.SourceSha256, configurationSignature,
            nodes = state.Tree.ExportSnapshot(), savedUtc = DateTimeOffset.UtcNow,
        }, JsonOptions);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Section 8: fair scheduling. A single bounded loop round-robins across both
    /// documents' pending leaves so one pathological branch never monopolizes the run. Each pass
    /// attempts (or splits, or reuses) exactly one leaf per document before moving to the next.</summary>
    private static async Task ExecuteFairRoundRobinAsync(
        OpenRouterCeilingReasoningModel model, string output, List<DocumentRecoveryState> documents, string configurationSignature, CancellationToken ct)
    {
        while (documents.Any(d => d.Tree.PendingLeaves.Count > 0))
        {
            ct.ThrowIfCancellationRequested();
            var progressed = false;
            foreach (var state in documents)
            {
                ct.ThrowIfCancellationRequested();
                var pending = state.Tree.PendingLeaves;
                if (pending.Count == 0) continue;
                progressed = true;
                var leaf = pending[0];
                await ProcessLeafAsync(model, output, state, leaf, configurationSignature, ct);
                SaveTreeState(output, state, configurationSignature);
            }
            if (!progressed) break;
        }
    }

    private static async Task ProcessLeafAsync(
        OpenRouterCeilingReasoningModel model, string output, DocumentRecoveryState state, SegmentNode leaf, string configurationSignature, CancellationToken ct)
    {
        // Section 3: a leaf sitting in WORKLOAD_SPLIT_REQUIRED must never be re-attempted with the
        // same shape -- split (or terminate at the floor), never call the provider again for it.
        if (leaf.Status == SegmentRecoveryState.WorkloadSplitRequired)
        {
            if (state.Tree.CanSplit(leaf))
            {
                state.Tree.Split(leaf.SegmentId, HaloOccurrences);
                state.SplitCount++;
            }
            else
            {
                state.Tree.MarkFailedTerminal(leaf.SegmentId, leaf.FailureClass ?? "UNKNOWN");
                state.FailedLeafCount++;
            }
            return;
        }

        // A transient failure gets a bounded number of identical-shape attempts before it is
        // treated as a workload-shape problem instead (never retried forever).
        if (leaf.Status == SegmentRecoveryState.TransientRetry && leaf.Attempts >= MaxTransientAttemptsPerLeaf)
        {
            if (state.Tree.CanSplit(leaf)) { state.Tree.Split(leaf.SegmentId, HaloOccurrences); state.SplitCount++; }
            else { state.Tree.MarkFailedTerminal(leaf.SegmentId, leaf.FailureClass ?? "UNKNOWN"); state.FailedLeafCount++; }
            return;
        }

        var planHash = SegmentLeafPersistence.PlanHash(leaf.Owned);
        var key = new SegmentLeafArtifactKey(state.Item.DocumentId, leaf.SegmentId, state.Item.SourceSha256, configurationSignature, planHash, Model, ReasoningMode);

        // Section 7/18(C,D): reuse a frozen leaf whose key matches exactly. Any mismatch --
        // including a source/config/plan change -- forces a fresh call for this leaf only.
        if (SegmentLeafPersistence.TryLoad(output, key, out var artifact) && artifact is not null)
        {
            state.Tree.MarkSuccess(leaf.SegmentId, "REUSED", artifact.ResponseHash);
            state.ReusedLeafCount++;
            LoadRawProposalsFromArtifact(state, leaf, artifact);
            return;
        }

        state.Tree.MarkRunning(leaf.SegmentId);
        var (ownedSet, visibleWindow, ownedWindow, visibleOccurrences) = BuildWindows(state, leaf);
        var packetResult = CeilingPacketBuilder.Build(visibleOccurrences, ownedSet, visibleWindow, ownedWindow);
        var requestId = $"{CeilingSemanticPrompt.ProtocolVersion}:{state.Item.DocumentId}:{leaf.SegmentId}:{configurationSignature}:attempt-{leaf.Attempts + 1}";

        // A per-leaf attempt bound, distinct from (and always <=) the shared infrastructure's
        // request-timeout floor: it exists purely to keep the recovery tree progressing within a
        // single bounded process invocation. A cancellation caused by THIS bound is a genuine
        // PROVIDER_TOTAL_TIMEOUT for that leaf's shape (workload-shape -> triggers a split, never
        // an identical retry); a cancellation caused by the caller's own token propagates as-is so
        // the outer run can still classify it as a wall-clock-budget stop rather than a leaf failure.
        // Ceiling-mode reasoning genuinely spends a long time thinking even over a small prompt --
        // the shared infrastructure's own floor for this same route is 600s (see
        // OpenRouterCeilingReasoningModel). A short per-leaf bound would misclassify slow-but-
        // otherwise-fine reasoning as a workload-shape timeout and split segments that would have
        // succeeded given more time, so the floor here stays generous; only genuinely oversized
        // owned ranges get a materially longer allowance.
        var leafTimeoutSeconds = Math.Clamp(leaf.OwnedCharacters / 50, 180, 400);
        using var leafCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        leafCts.CancelAfter(TimeSpan.FromSeconds(leafTimeoutSeconds));

        try
        {
            var (response, telemetry) = await model.CompleteSemanticAsync(
                state.Item.DocumentId, ReasoningRoute.ModelCapabilityCeiling.ToString(), requestId, packetResult.SerializedJson,
                packetResult.SourceTextCharacters, ownedSet.Count, visibleOccurrences.Length, leafCts.Token).ConfigureAwait(false);
            state.ProviderCalls++;

            var rawResponseHash = Sha256Text(JsonSerializer.Serialize(response));
            var proposals = new List<(string, string, int, int, string)>();
            foreach (var heading in response.Headings)
            {
                if (heading.I < 0 || heading.I >= packetResult.Bindings.Count) continue;
                var binding = packetResult.Bindings[heading.I];
                if (!ownedSet.Contains(binding.SourceOccurrenceId)) continue;
                if (!binding.TryBind(heading.Start, heading.End, out var globalStart, out var globalEnd, out var owned) || !owned) continue;
                var occurrence = state.OccurrenceById[binding.SourceOccurrenceId];
                proposals.Add((leaf.SegmentId, occurrence.SourceId, globalStart, globalEnd, heading.Role));
            }

            var requestHash = Sha256Text(requestId);
            var predictionJson = JsonSerializer.Serialize(new
            {
                segmentId = leaf.SegmentId, headings = response.Headings,
                boundProposals = proposals.Select(p => new { sourceId = p.Item2, start = p.Item3, end = p.Item4, role = p.Item5 }).ToArray(),
            }, JsonOptions);
            var executionJson = JsonSerializer.Serialize(new
            {
                segmentId = leaf.SegmentId, ownedCharacters = leaf.OwnedCharacters, attempts = leaf.Attempts + 1,
                reasoningTokens = telemetry.ReportedReasoningTokens, outputTokens = telemetry.ReportedOutputTokens, inputTokens = telemetry.ReportedInputTokens,
            }, JsonOptions);
            var requestManifestJson = JsonSerializer.Serialize(new
            {
                segmentId = leaf.SegmentId, documentId = state.Item.DocumentId, ownedAtoms = leaf.Owned, visibleAtoms = leaf.Visible, requestId,
            }, JsonOptions);
            SegmentLeafPersistence.Save(output, key, requestManifestJson, predictionJson, executionJson, requestHash, rawResponseHash);

            state.Tree.MarkSuccess(leaf.SegmentId, requestHash, rawResponseHash);
            state.SuccessfulLeafCount++;
            state.RawProposals.AddRange(proposals);
            state.ResponseHashBySegment[leaf.SegmentId] = rawResponseHash;
        }
        catch (ReasoningCompletionException ex)
        {
            state.Tree.RecordFailure(leaf.SegmentId, ex.FailureClass);
        }
        catch (Exception ex) when (ex is HttpRequestException or FormatException or JsonException)
        {
            state.Tree.RecordFailure(leaf.SegmentId, ex is JsonException ? ReasoningCompletionFailureClass.CompleteResponseSchemaInvalid : ReasoningCompletionFailureClass.OtherProviderFailure);
        }
        catch (OperationCanceledException) when (leafCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // The per-leaf bound fired, not the caller's own deadline/cancellation -- classify
            // explicitly as a workload-shape timeout for this leaf's shape rather than letting a
            // bare OperationCanceledException look like a vacuous success or abort the whole run.
            state.Tree.RecordFailure(leaf.SegmentId, ReasoningCompletionFailureClass.ProviderTotalTimeout);
        }
    }

    private static void LoadRawProposalsFromArtifact(DocumentRecoveryState state, SegmentNode leaf, SegmentLeafArtifact artifact)
    {
        // Section 7/14: a reused (or resumed) leaf's bound proposals are re-parsed verbatim from
        // its persisted prediction.json -- never re-derived by re-calling the provider, and never
        // fabricated when the file is unexpectedly absent/corrupt (the union simply reflects
        // whatever this leaf genuinely produced last time it ran).
        try
        {
            using var doc = JsonDocument.Parse(artifact.PredictionJson);
            if (!doc.RootElement.TryGetProperty("boundProposals", out var arr) || arr.ValueKind != JsonValueKind.Array) return;
            foreach (var row in arr.EnumerateArray())
            {
                var sourceId = row.GetProperty("sourceId").GetString();
                var start = row.GetProperty("start").GetInt32();
                var end = row.GetProperty("end").GetInt32();
                var role = row.GetProperty("role").GetString();
                if (sourceId is null || role is null) continue;
                state.RawProposals.Add((leaf.SegmentId, sourceId, start, end, role));
            }
        }
        catch (JsonException)
        {
            // Corrupt cached artifact -- leave proposals empty for this leaf; the mismatch will
            // surface as a coverage/union gap rather than fabricated headings.
        }
    }

    /// <summary>After restoring a tree from a prior invocation's snapshot, every leaf already at
    /// SUCCESS needs its bound proposals re-loaded from disk (they only exist in-memory for the
    /// process that first produced them) so the eventual union sees every successful leaf's
    /// contribution, not just the ones this process happens to (re-)visit.</summary>
    private static void ReconcileSuccessfulLeavesFromDisk(string output, DocumentRecoveryState state, string configurationSignature)
    {
        foreach (var leaf in state.Tree.Leaves.Where(n => n.Status == SegmentRecoveryState.Success))
        {
            var planHash = SegmentLeafPersistence.PlanHash(leaf.Owned);
            var key = new SegmentLeafArtifactKey(state.Item.DocumentId, leaf.SegmentId, state.Item.SourceSha256, configurationSignature, planHash, Model, ReasoningMode);
            if (SegmentLeafPersistence.TryLoad(output, key, out var artifact) && artifact is not null)
                LoadRawProposalsFromArtifact(state, leaf, artifact);
        }
    }

    private static (HashSet<string> OwnedSet, Dictionary<string, (int, int)> VisibleWindow, Dictionary<string, (int, int)> OwnedWindow, ReasoningSourceOccurrence[] VisibleOccurrences)
        BuildWindows(DocumentRecoveryState state, SegmentNode leaf)
    {
        var ownedByOccurrence = leaf.Owned.GroupBy(a => a.OccurrenceIndex).ToDictionary(g => g.Key, g => (g.Min(a => a.CharStart), g.Max(a => a.CharEnd)));
        var visibleByOccurrence = leaf.Visible.GroupBy(a => a.OccurrenceIndex).ToDictionary(g => g.Key, g => (g.Min(a => a.CharStart), g.Max(a => a.CharEnd)));

        var ownedSet = new HashSet<string>(StringComparer.Ordinal);
        var visibleWindow = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        var ownedWindow = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        var visibleOccurrences = new List<ReasoningSourceOccurrence>();

        foreach (var (occIndex, (vStart, vEnd)) in visibleByOccurrence)
        {
            var occurrence = state.Occurrences[occIndex];
            visibleOccurrences.Add(occurrence);
            visibleWindow[occurrence.SourceOccurrenceId] = (vStart, vEnd);
            if (ownedByOccurrence.TryGetValue(occIndex, out var owned))
            {
                ownedSet.Add(occurrence.SourceOccurrenceId);
                ownedWindow[occurrence.SourceOccurrenceId] = owned;
            }
            else
            {
                ownedWindow[occurrence.SourceOccurrenceId] = (vStart, vStart);
            }
        }
        return (ownedSet, visibleWindow, ownedWindow, visibleOccurrences.ToArray());
    }

    private static async Task<DocumentMetric> FinalizeDocumentAsync(
        string repoRoot, string output, DocumentRecoveryState state, OpenRouterCeilingReasoningModel model, string configurationSignature, CancellationToken ct)
    {
        var item = state.Item;
        var source = state.Source;
        var policy = state.Policy;
        var docDir = state.DocDir;
        var tree = state.Tree;

        var fullCoverage = tree.VerifyFullCoverage();
        var isForcedFullContext = tree.Leaves.Count == 1;
        string finalExecutionMode;
        if (fullCoverage && isForcedFullContext) finalExecutionMode = DocumentCompletionState.FullContextSuccess;
        else if (fullCoverage) finalExecutionMode = DocumentCompletionState.SegmentedRecoverySuccess;
        else finalExecutionMode = DocumentCompletionState.SegmentedRecoveryPartialBlocked;

        var union = SegmentProposalUnion.Union(state.RawProposals);
        var proposals = union.Select(p => new ReasoningHeadingProposal
        {
            SourceId = p.SourceId,
            HeadingSpan = new StructuralSpan(p.Start, p.End),
            Text = state.OccurrenceById.Values.First(o => o.SourceId == p.SourceId).RawText[p.Start..p.End],
            SemanticRole = p.Role,
            Confidence = 1,
        }).ToList();

        var hierarchyCalls = 0;
        var hierarchyValidation = new ReasoningHierarchyValidation([], [], []);
        var hierarchyDegraded = false;
        var proposalSetHash = Sha256Text(string.Join('|', proposals.Select(Key).OrderBy(x => x, StringComparer.Ordinal)));
        var hierarchyCachePath = Path.Combine(docDir, "hierarchy-cache.v1.json");
        var cachedDegraded = TryLoadCachedHierarchyDegraded(hierarchyCachePath, proposalSetHash, configurationSignature);
        if (cachedDegraded)
        {
            // Section 11: a hierarchy pass that already failed for this exact proposal set and
            // configuration is a stable, reproducible workload-shape result -- re-attempting it on
            // every bounded invocation would burn the shared infrastructure's ~600s request floor
            // every single time without making progress. Reuse the classification instead.
            hierarchyDegraded = true;
            finalExecutionMode = DocumentCompletionState.SemanticCompleteHierarchyBlocked;
            proposals = proposals.Select(p => p with { ProposedParent = null, ProposedLevel = 1 }).ToList();
        }
        else if (fullCoverage && proposals.Count > 0)
        {
            var globalInventory = ReasoningGlobalHierarchyPass.BuildInventory(source.DocumentId, source, proposals);
            var (packetJson, bindings) = CeilingHierarchyPacketBuilder.Build(globalInventory);
            var requestId = $"{CeilingHierarchyPrompt.ProtocolVersion}:{source.DocumentId}:global-hierarchy:{configurationSignature}";
            using var hierarchyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            hierarchyCts.CancelAfter(TimeSpan.FromSeconds(400));
            try
            {
                var (hResponse, _) = await model.CompleteHierarchyAsync(source.DocumentId, ReasoningRoute.ModelCapabilityCeiling.ToString(), requestId, packetJson, globalInventory.Count, hierarchyCts.Token);
                hierarchyCalls++;
                var distinctChildren = hResponse.Parents.Select(edge => edge.Child).ToHashSet();
                if (hResponse.Parents.Count != globalInventory.Count || distinctChildren.Count != globalInventory.Count)
                    throw new FormatException("ceiling-hierarchy-response-incomplete");
                var edges = hResponse.Parents.Select(edge => new ReasoningHierarchyEdge(bindings[edge.Child].ProposalId, edge.Parent is { } p ? bindings[p].ProposalId : null)).ToArray();
                hierarchyValidation = ReasoningGlobalHierarchyPass.Validate(globalInventory, edges);
                var levels = ReasoningGlobalHierarchyPass.DeriveLevels(globalInventory, hierarchyValidation.AcceptedEdges);
                var byGlobalId = proposals.ToDictionary(p => ReasoningGlobalHierarchyPass.ProposalId(source.DocumentId, p.SourceId, p.HeadingSpan), StringComparer.Ordinal);
                var parents = hierarchyValidation.AcceptedEdges.ToDictionary(
                    edge => edge.ChildProposalId,
                    edge => edge.ParentProposalId is not null && byGlobalId.TryGetValue(edge.ParentProposalId, out var parent) ? ReasoningProposalMaterializer.ElementId(parent) : null,
                    StringComparer.Ordinal);
                proposals = proposals.Select(proposal =>
                {
                    var id = ReasoningGlobalHierarchyPass.ProposalId(source.DocumentId, proposal.SourceId, proposal.HeadingSpan);
                    return proposal with { ProposedParent = parents.GetValueOrDefault(id), ProposedLevel = levels.GetValueOrDefault(id, 1) };
                }).ToList();
            }
            catch (Exception ex) when (ex is ReasoningCompletionException or FormatException or JsonException)
            {
                hierarchyDegraded = true;
                finalExecutionMode = DocumentCompletionState.SemanticCompleteHierarchyBlocked;
                proposals = proposals.Select(p => p with { ProposedParent = null, ProposedLevel = 1 }).ToList();
                SaveCachedHierarchyDegraded(hierarchyCachePath, proposalSetHash, configurationSignature);
            }
            catch (OperationCanceledException) when (hierarchyCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                hierarchyDegraded = true;
                finalExecutionMode = DocumentCompletionState.SemanticCompleteHierarchyBlocked;
                proposals = proposals.Select(p => p with { ProposedParent = null, ProposedLevel = 1 }).ToList();
                SaveCachedHierarchyDegraded(hierarchyCachePath, proposalSetHash, configurationSignature);
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

        var gate = new DocumentFreezeGate();
        var leafSnapshot = tree.AllNodes.Select(n => new
        {
            n.SegmentId, n.ParentSegmentId, n.Depth, n.Status, n.Attempts, n.FailureClass, n.ResponseHash,
            ownedCharacters = n.OwnedCharacters, childSegmentIds = n.ChildSegmentIds,
        }).ToArray();

        var predictionPath = Path.Combine(docDir, "prediction.v1.json");
        await WriteJsonAsync(predictionPath, new
        {
            documentId = item.DocumentId, sourceSha256 = item.SourceSha256, requestedModel = Model,
            finalExecutionMode, hierarchyDegraded, semanticProtocolVersion = CeilingSemanticPrompt.ProtocolVersion,
            hierarchyProtocolVersion = CeilingHierarchyPrompt.ProtocolVersion,
            leafSegments = tree.Leaves.Count, successfulLeafCount = state.SuccessfulLeafCount, failedLeafCount = state.FailedLeafCount,
            splitCount = state.SplitCount, reusedLeafCount = state.ReusedLeafCount, providerCalls = state.ProviderCalls,
            rawProposalCount = state.RawProposals.Count, boundProposalCount = proposals.Count,
            validatedSemanticCount = materialized.Validated.Count(x => x.Accepted), proposals, projection,
            hierarchyValidation, segmentTree = leafSnapshot, goldReadBeforeFreeze = false,
        }, ct);
        var resultPath = Path.Combine(docDir, "result.v1.json");
        await WriteJsonAsync(resultPath, new
        {
            documentId = item.DocumentId, status = finalExecutionMode, headings = finalElements, goldReadBeforeFreeze = false,
        }, ct);
        var runtimeTracePath = Path.Combine(docDir, "runtime-trace.v1.json");
        await WriteJsonAsync(runtimeTracePath, new
        {
            documentId = item.DocumentId, finalExecutionMode, segmentTree = leafSnapshot,
            wallTimeMs = state.Stopwatch.ElapsedMilliseconds, goldReadBeforeFreeze = false,
        }, ct);

        var predictionHash = Sha256(predictionPath);
        var resultHash = Sha256(resultPath);
        var runtimeTraceHash = Sha256(runtimeTracePath);
        var freezePath = Path.Combine(docDir, "freeze.v1.json");
        await WriteJsonAsync(freezePath, new
        {
            documentId = item.DocumentId, sourceSha256 = item.SourceSha256,
            predictionSha256 = predictionHash, resultSha256 = resultHash, runtimeTraceSha256 = runtimeTraceHash,
            model = Model, reasoningMode = ReasoningMode, modelCapabilityDigest = CapabilityDigest(model.Capability),
            executionMode = finalExecutionMode, leafSegments = tree.Leaves.Count,
            configurationSignature, goldReadBeforeFreeze = false, frozenUtc = DateTimeOffset.UtcNow,
        }, ct);
        gate.MarkFrozen();

        var eligibility = ReasoningGoldEligibilityEvaluator.Evaluate(repoRoot, item.DocumentId);
        gate.GuardGoldRead(); // section 13 firewall enforced in code, not just by call order
        var goldKeys = Array.Empty<string>();
        var tp = 0; var fp = 0; var fn = 0; var exactStatus = "NOT_EVALUABLE"; var systemLoss = 0;
        var lossCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var canScore = eligibility.Eligible && finalExecutionMode is DocumentCompletionState.FullContextSuccess or DocumentCompletionState.SegmentedRecoverySuccess;
        // Hierarchy completion can flip between invocations (the hierarchy pass has its own
        // independent timeout/retry surface, non-deterministic in latency); never leave a stale
        // score.v1.json or semantic-evaluation.v1.json from a previous invocation's different
        // outcome sitting next to the current one.
        File.Delete(Path.Combine(docDir, "score.v1.json"));
        File.Delete(Path.Combine(docDir, "semantic-evaluation.v1.json"));
        if (canScore)
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
                finalExecutionMode, goldReadBeforeFreeze = false,
            }, ct);
        }
        else
        {
            var occurrencePath = Path.Combine(repoRoot, "eval", "a99-closed-loop", "strict-gold-occurrence-v1", $"{item.DocumentId}.occurrence-gold-v1.json");
            var semanticTotal = File.Exists(occurrencePath) ? JsonDocument.Parse(File.ReadAllText(occurrencePath)).RootElement.GetProperty("semanticHeadingTotal").GetInt32() : 0;
            await WriteJsonAsync(Path.Combine(docDir, "semantic-evaluation.v1.json"), new
            {
                documentId = item.DocumentId, exactStatus, eligibilityReason = eligibility.Eligible ? "EXECUTION_INCOMPLETE" : eligibility.Reason,
                semanticHeadingTotal = semanticTotal, rawProposalCount = state.RawProposals.Count, boundProposalCount = proposals.Count,
                finalHeadingCount = finalElements.Length, systemLossCount = 0, finalExecutionMode, goldReadBeforeFreeze = false,
            }, ct);
        }

        var docTelemetry = model.Telemetry.Where(x => string.Equals(x.DocumentId, source.DocumentId, StringComparison.Ordinal)).ToArray();
        var scoreFinal = Score(goldKeys, predictionKeys);
        var metric = new DocumentMetric(item.DocumentId, source.Paragraphs.Sum(p => p.Text.Length), tree.Leaves.Count,
            state.SuccessfulLeafCount, state.FailedLeafCount, state.SplitCount, state.ReusedLeafCount, state.ProviderCalls, hierarchyCalls,
            docTelemetry.Length, docTelemetry.Sum(x => x.ReportedInputTokens ?? 0), docTelemetry.Sum(x => x.ReportedOutputTokens ?? 0),
            docTelemetry.Sum(x => x.ReportedReasoningTokens ?? 0), state.Stopwatch.ElapsedMilliseconds, state.RawProposals.Count, proposals.Count,
            materialized.Validated.Count(x => x.Accepted), finalElements.Length, systemLoss, exactStatus, tp, fp, fn,
            scoreFinal.P, scoreFinal.R, scoreFinal.F1, goldKeys, predictionKeys, finalExecutionMode);
        await WriteJsonAsync(Path.Combine(docDir, "execution.v1.json"), metric with { sourceSha256 = item.SourceSha256, freezePath = freezePath, goldReadBeforeFreeze = false }, ct);
        return metric;
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
            yield return raw.Any(p => Key(p) == key) ? "MODEL_FALSE_POSITIVE" : "SYSTEM_BINDING_LOSS";
        }
    }

    private static bool TryLoadCachedHierarchyDegraded(string path, string proposalSetHash, string configurationSignature)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            return root.GetProperty("degraded").GetBoolean() &&
                   root.GetProperty("proposalSetHash").GetString() == proposalSetHash &&
                   root.GetProperty("configurationSignature").GetString() == configurationSignature;
        }
        catch (JsonException) { return false; }
    }

    private static void SaveCachedHierarchyDegraded(string path, string proposalSetHash, string configurationSignature)
    {
        var json = JsonSerializer.Serialize(new { degraded = true, proposalSetHash, configurationSignature, savedUtc = DateTimeOffset.UtcNow }, JsonOptions);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

    private static string Key(ReasoningHeadingProposal p) => Key(p.SourceId, p.HeadingSpan);
    private static string Key(ValidatedStructuralElement e) => Key(e.Sources.Single().SourceId, e.Sources.Single().Span);
    private static string Key(string sourceId, StructuralSpan span) => $"{sourceId}:{span.Start}:{span.End}";
    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Sha256Text(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string ConfigurationSignature(OpenRouterModelCapability capability) => Sha256Text(
        $"A99_OPENROUTER_QWEN35_9B_PER_SEGMENT_RECOVERY|{Model}|{CeilingSemanticPrompt.ProtocolVersion}|{CeilingHierarchyPrompt.ProtocolVersion}|temperature=0|reasoning={capability.SelectedReasoningEffort}|exclude=true|fallbacks=false|context={capability.ContextLength}|minSegmentChars={MinimumSegmentCharacters}");

    private static string CapabilityDigest(OpenRouterModelCapability capability) => Sha256Text(
        $"{capability.ModelId}|{capability.ContextLength}|{capability.ReasoningSupported}|{string.Join(',', capability.SupportedReasoningEfforts)}|{capability.SelectedReasoningEffort}|{capability.StructuredOutputSupported}|{capability.MaxCompletionTokens}");

    private static async Task WriteJsonAsync(string path, object value, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine);
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, FileOptions.SequentialScan);
        await stream.WriteAsync(bytes, ct); await stream.FlushAsync(ct); stream.Flush(true);
    }

    private static async Task<int> WriteBlockedAsync(string output, string classification, string? reason, CancellationToken ct)
    {
        await WriteJsonAsync(Path.Combine(output, "summary.v1.json"), new
        {
            schemaVersion = "a99-openrouter-qwen35-9b-per-segment-recovery-v1", status = classification, reason,
            requestedModel = Model, provider = "OpenRouter", selectedDocuments = SelectedIds,
            openRouterCalls = 0, inputTokens = 0, outputTokens = 0, goldReadBeforeFreeze = false,
        }, ct);
        Console.WriteLine($"FINAL_CLASSIFICATION=BLOCKED_PROVIDER_UNAVAILABLE");
        return 1;
    }

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
    private sealed record DocumentMetric(string documentId, int sourceCharacters, int leafSegments,
        int successfulLeafCount, int failedLeafCount, int splitCount, int reusedLeafCount, int providerCalls, int hierarchyCalls,
        int totalModelCalls, int inputTokens, int outputTokens, int reasoningTokens, long wallTime, int rawProposalCount,
        int boundProposalCount, int validatedSemanticCount, int finalHeadingCount, int systemLossCount, string exactStatus,
        int TP, int FP, int FN, double P, double R, double F1, IReadOnlyList<string> GoldKeys, IReadOnlyList<string> PredictionKeys,
        string finalExecutionMode)
    {
        public string? sourceSha256 { get; init; }
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
