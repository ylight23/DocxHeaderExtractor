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

/// <summary>Gold-blind provider characterization for the persisted DOC-0205 S1 tree. It probes
/// exactly three leaves against a small metadata-selected route set, then returns an execution
/// profile for the multipass runner. No semantic prompt, source, model, Gold, or tree result is
/// changed by this class.</summary>
public static class OpenRouterQwen9BProviderDiagnosticRunner
{
    private const string OutputRoot = "eval/a99-closed-loop/qwen9b-multipass-doc0205";
    private const string InventoryPath = "eval/a99-dataset/document-inventory.v1.json";
    private const string S0Root = "eval/a99-closed-loop/openrouter-qwen35-9b-per-segment-recovery";
    private const string DocumentId = "DOC-0205";
    private const string Model = "qwen/qwen3.5-9b";
    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";
    private const string SemanticRoute = "ModelCapabilityCeiling";
    private const int MaxProbeRoutes = 3;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly JsonSerializerOptions EventJsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<(int ExitCode, Qwen9BExecutionProfile? Profile)> RunAsync(
        string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(output);
        var eventPath = Path.Combine(output, "provider-probe-events.v1.jsonl");
        await File.WriteAllTextAsync(eventPath, string.Empty, Encoding.UTF8, ct);
        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            await WriteJsonAsync(Path.Combine(output, "provider-diagnosis.v1.json"), new { status = "BLOCKED", reason = "OPENROUTER_API_KEY_MISSING", goldReadBeforeFreeze = false }, ct);
            return (1, null);
        }

        var inventory = ReadInventory(Path.Combine(repoRoot, InventoryPath));
        var item = inventory.Single(x => x.DocumentId == DocumentId);
        var sourcePath = Path.Combine(repoRoot, item.SourcePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(sourcePath) || !string.Equals(Sha256File(sourcePath), item.SourceSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SOURCE_HASH_MISMATCH");

        var source = new OpenXmlDocumentSource().Read(sourcePath) with { DocumentId = item.DocumentId };
        Console.WriteLine("PROVIDER_DIAGNOSIS_SOURCE_READY");
        var features = NumberingStyleFeatures.FromSourceDocument(source);
        var derived = new DocumentFeatureDeriver().Derive(source);
        var policy = DocxPolicyStateBuilder.Build(source, features, derived, new PipelineOptions { DisableLlm = false }.Extraction);
        var occurrences = ReasoningContextBuilder.Build(source, policy, int.MaxValue / 2, int.MaxValue / 2, expandOwnedPerOccurrence: false).Occurrences;

        var s0Prediction = Path.Combine(repoRoot, S0Root.Replace('/', Path.DirectorySeparatorChar), "documents", DocumentId, "prediction.v1.json");
        var s1TreePath = Path.Combine(output, "s1", "segments", "segment-tree-state.v1.json");
        var selected = SelectLeaves(s1TreePath);
        var fullInventory = ReadS0Inventory(s0Prediction);
        var probes = selected.Select((leaf, index) => BuildProbe(leaf, index, occurrences, fullInventory)).ToArray();
        var baseOptions = new RemoteInferenceOptions
        {
            Endpoint = new Uri(Endpoint), ApiKey = key, Model = Model,
            ContextSize = 262_144, MaxOutputTokens = 48_000, RequestTimeoutSeconds = 1_500,
            TransientRequestRetries = 0, MaxParallelRequests = 1, SendChatTemplateKwargs = false,
        };
        using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = Timeout.InfiniteTimeSpan };
        var preflight = await OpenRouterModelCapabilityResolver.ResolveAsync(baseOptions, http, ct);
        Console.WriteLine($"PROVIDER_DIAGNOSIS_MODEL_PREFLIGHT={preflight.Reason}");
        if (!preflight.Available || preflight.Capability is null || !preflight.Capability.ReasoningSupported ||
            !string.Equals(preflight.Capability.ModelId, Model, StringComparison.Ordinal))
        {
            await WriteJsonAsync(Path.Combine(output, "provider-diagnosis.v1.json"), new { status = "BLOCKED", reason = preflight.Reason, preflight, goldReadBeforeFreeze = false }, ct);
            return (1, null);
        }

        var routesResult = await OpenRouterProviderRouteResolver.ResolveAsync(baseOptions, http, ct);
        Console.WriteLine($"PROVIDER_DIAGNOSIS_ROUTES={routesResult.Routes.Count}");
        if (!routesResult.Available)
        {
            await WriteJsonAsync(Path.Combine(output, "provider-diagnosis.v1.json"), new { status = "BLOCKED", reason = routesResult.Reason, preflight, goldReadBeforeFreeze = false }, ct);
            return (1, null);
        }
        var routes = routesResult.Routes.Where(x => x.SupportsCeilingRequest)
            .OrderByDescending(x => x.UptimeLast30Minutes ?? double.MinValue)
            .ThenBy(x => x.LatencyP50Ms ?? double.MaxValue)
            .Take(MaxProbeRoutes).ToArray();
        if (routes.Length == 0)
        {
            await WriteJsonAsync(Path.Combine(output, "provider-diagnosis.v1.json"), new { status = "BLOCKED", reason = "NO_ROUTE_SUPPORTS_CEILING_REQUEST", preflight, routes = routesResult.Routes, goldReadBeforeFreeze = false }, ct);
            return (1, null);
        }

        using var liveLease = await A99OpenRouterLiveProviderLease.AcquireAsync(repoRoot, "provider-diagnosis", DocumentId, ct);
        Console.WriteLine($"LIVE_PROVIDER_LOCK=acquired concurrentCampaignsDetected={liveLease.ConcurrentCampaignsDetected} providerConcurrency={liveLease.ProviderConcurrency}");
        var probeResults = new List<ProbeResult>();
        var probeDeadlineMinutes = ReadBoundedMinutes("A99_QWEN_CAPABILITY_PROBE_DEADLINE_MINUTES", 8, 2, 10);
        foreach (var route in routes)
        {
            var options = Clone(baseOptions, route.Route);
            using var model = new OpenRouterCeilingReasoningModel(options, preflight.Capability, http,
                attemptDeadline: TimeSpan.FromMinutes(probeDeadlineMinutes), streamStallDeadline: TimeSpan.FromMinutes(Math.Min(5, probeDeadlineMinutes)));
            foreach (var probe in probes)
            {
                ct.ThrowIfCancellationRequested();
                Console.WriteLine($"PROVIDER_PROBE_START provider={route.ProviderName} route={route.Route} request={probe.Label} packetChars={probe.PacketCharacters}");
                var requestId = $"provider-diagnostic:{probe.Label}:{OmissionReviewPrompt.ProtocolVersion}";
                try
                {
                    var (_, telemetry) = await model.CompleteOmissionReviewAsync(DocumentId, SemanticRoute, requestId,
                        probe.PacketJson, probe.InventoryJson, probe.SourceCharacters, probe.OwnedOccurrences, probe.VisibleOccurrences, ct);
                    var result = MakeSuccess(route, probe, telemetry);
                    probeResults.Add(result);
                    await AppendEventAsync(eventPath, result, ct);
                    Console.WriteLine($"PROVIDER_PROBE_RESULT provider={route.ProviderName} request={probe.Label} result=SUCCESS elapsedMs={telemetry.ElapsedMs}");
                }
                catch (ReasoningCompletionException ex)
                {
                    var telemetry = model.Telemetry.LastOrDefault(t => t.RequestIdHash == Sha256Text(requestId)) ??
                        new RequestPacketTelemetry
                        {
                            RequestIdHash = Sha256Text(requestId), DocumentId = DocumentId, PassType = "OMISSION_REVIEW",
                            Model = Model, ReasoningEffort = preflight.Capability.SelectedReasoningEffort,
                            ReasoningExcludedFromResponse = true, ContextLength = preflight.Capability.ContextLength,
                            MaxPromptTokens = 0, MaxCompletionTokens = 0,
                        };
                    var result = MakeFailure(route, probe, telemetry, ex.FailureClass);
                    probeResults.Add(result);
                    await AppendEventAsync(eventPath, result, ct);
                    Console.WriteLine($"PROVIDER_PROBE_RESULT provider={route.ProviderName} request={probe.Label} result={result.ResultClass} elapsedMs={telemetry.ElapsedMs}");
                }
            }
        }

        var canonicalHashes = probeResults.GroupBy(x => x.Label, StringComparer.Ordinal)
            .Select(g => g.Select(x => x.Telemetry.CanonicalRequestHash).Where(x => x is not null).Distinct(StringComparer.Ordinal).Count()).ToArray();
        if (canonicalHashes.Any(x => x > 1)) throw new InvalidDataException("PROBE_REQUEST_HASH_MISMATCH");

        var successes = probeResults.Where(x => x.Success && x.Telemetry.ElapsedMs > 0).ToArray();
        var derivedDeadlineSeconds = DeriveDeadlineSeconds(successes.Select(x => x.Telemetry).ToArray());
        var bottleneck = Diagnose(probeResults);
        var pinned = SelectPinnedRoute(probeResults, routes);
        var profile = new Qwen9BExecutionProfile(
            pinned?.Route,
            TimeSpan.FromSeconds(derivedDeadlineSeconds),
            TimeSpan.FromSeconds(Math.Clamp(derivedDeadlineSeconds / 2, 120, 300)),
            bottleneck,
            pinned is null ? "NO_ROUTE_CLEARLY_SUPERIOR" : "SUCCESS_RATE_THEN_END_TO_END_LATENCY");

        var historicalAttempts = ReadHistoricalAttempts(output);
        var artifact = new
        {
            schemaVersion = "a99-qwen9b-provider-diagnosis-v1",
            status = "COMPLETE",
            documentId = DocumentId,
            model = Model,
            providerMetadata = routesResult.Routes,
            selectedLeaves = probes.Select(x => new { x.Label, x.SegmentId, x.OwnedCharacters, x.PacketCharacters, x.EstimatedInputTokens, x.PreviousStatus, x.PreviousFailureClass }),
            probeDeadlineMinutes,
            requests = probeResults.Select(x => new
            {
                route = x.Route.Route, provider = x.Route.ProviderName, x.Label, x.SegmentId, success = x.Success,
                result = x.ResultClass, canonicalRequestHash = x.Telemetry.CanonicalRequestHash,
                requestBodyHash = x.Telemetry.RequestBodyHash, telemetry = x.Telemetry,
            }),
            routeComparison = BuildRouteComparison(probeResults),
            cacheComparison = probeResults.Select(x => new { provider = x.Route.ProviderName, route = x.Route.Route, request = x.Label, cacheMode = x.Telemetry.CacheMode, cachedInputTokens = x.Telemetry.CachedInputTokens }),
            primaryBottleneck = bottleneck,
            pinnedProvider = pinned?.Route,
            pinReason = profile.PinReason,
            derivedDeadlineSeconds,
            historicalAttempts,
            probeAttempts = probeResults.Count,
            resumeAttempts = 0,
            successfulResponses = probeResults.Count(x => x.Success),
            timeouts = probeResults.Count(x => IsTimeout(x.ResultClass)),
            outputLimits = probeResults.Count(x => x.ResultClass == "OUTPUT_LIMIT"),
            streamStalls = probeResults.Count(x => x.ResultClass == "STREAM_STALL"),
            providerErrors = probeResults.Count(x => x.ResultClass == "PROVIDER_ERROR"),
            reusedSuccessLeaves = 1,
            goldReadBeforeFreeze = false,
        };
        await WriteJsonAsync(Path.Combine(output, "provider-diagnosis.v1.json"), artifact, ct);
        await WriteJsonAsync(Path.Combine(output, "provider-execution-profile.v1.json"), profile, ct);
        Print(probeResults, bottleneck, pinned, derivedDeadlineSeconds);
        return (0, profile);
    }

    private static OpenRouterProviderRouteDescriptor? SelectPinnedRoute(IReadOnlyList<ProbeResult> results, IReadOnlyList<OpenRouterProviderRouteDescriptor> routes)
    {
        var grouped = results.GroupBy(x => x.Route.Route, StringComparer.Ordinal)
            .Select(g => new { Route = routes.Single(r => r.Route == g.Key), Successes = g.Count(x => x.Success), Total = g.Count(), Mean = g.Where(x => x.Success).Select(x => (double)x.Telemetry.ElapsedMs).DefaultIfEmpty(double.MaxValue).Average() })
            .OrderByDescending(x => x.Successes / (double)x.Total).ThenBy(x => x.Mean).ToArray();
        if (grouped.Length == 0) return null;
        var best = grouped[0];
        var second = grouped.Skip(1).Select(x => x.Successes / (double)x.Total).DefaultIfEmpty(0).Max();
        return best.Successes > 0 && (best.Successes == results.Count(x => x.Route.Route == best.Route.Route) || best.Successes / (double)best.Total >= second + 0.5)
            ? best.Route : null;
    }

    private static string Diagnose(IReadOnlyList<ProbeResult> results)
    {
        var byLabel = results.GroupBy(x => x.Label, StringComparer.Ordinal).ToArray();
        var successRates = results.GroupBy(x => x.Route.Route, StringComparer.Ordinal).Select(g => g.Count(x => x.Success) / (double)g.Count()).ToArray();
        var routeVariance = successRates.Length > 1 && successRates.Max() - successRates.Min() >= 0.34;
        var routeLatencyVariance = byLabel.Any(g => g.Where(x => x.Success).Select(x => (double)x.Telemetry.ElapsedMs).DefaultIfEmpty(0).Max() >
            2 * Math.Max(1, g.Where(x => x.Success).Select(x => (double)x.Telemetry.ElapsedMs).DefaultIfEmpty(0).Min()));
        var shape = results.Where(x => x.Label == "C").All(x => x.Success) && results.Where(x => x.Label != "C").Any(x => !x.Success);
        if (routeVariance || routeLatencyVariance) return "PROVIDER_QUEUE_OR_ROUTE_VARIANCE";
        if (shape) return "REQUEST_SHAPE_DOMINANT";
        var longSuccessful = results.Where(x => x.Success && x.Telemetry.ReportedReasoningTokens is not null).Select(x => x.Telemetry.ElapsedMs).DefaultIfEmpty(0).Average() > 180_000;
        return longSuccessful ? "REASONING_DECODE_DOMINANT" : "NOT_DETERMINED";
    }

    private static object[] BuildRouteComparison(IReadOnlyList<ProbeResult> results) => results
        .GroupBy(x => new { x.Route.ProviderName, x.Route.Route, x.Label })
        .Select(g =>
        {
            var x = g.Single();
            var t = x.Telemetry;
            var returned = (t.ReportedOutputTokens ?? 0);
            var rate = x.Success && t.ElapsedMs > 0 ? returned / (t.ElapsedMs / 1000d) : (double?)null;
            return new { provider = x.Route.ProviderName, route = x.Route.Route, request = x.Label, success = x.Success, result = x.ResultClass, ttft = "NOT_MEASURED", ttfb = t.TtfbMs, totalSec = t.ElapsedMs / 1000d, inputTok = t.ReportedInputTokens, reasoningTok = t.ReportedReasoningTokens, outputTok = t.ReportedOutputTokens, endToEndTokenRate = rate, cacheMode = t.CacheMode, cachedInputTokens = t.CachedInputTokens };
        }).Cast<object>().ToArray();

    private static int DeriveDeadlineSeconds(IReadOnlyList<RequestPacketTelemetry> successful)
    {
        if (successful.Count == 0) return 300;
        var values = successful.Select(x => x.ElapsedMs).OrderBy(x => x).ToArray();
        var p95 = values[Math.Min(values.Length - 1, (int)Math.Ceiling(values.Length * .95) - 1)];
        return Math.Clamp((int)Math.Ceiling(p95 / 1000d * 3), 300, 900);
    }

    private sealed record Probe(string Label, string SegmentId, string PacketJson, string InventoryJson, int SourceCharacters, int OwnedOccurrences, int VisibleOccurrences, int OwnedCharacters, int PacketCharacters, int EstimatedInputTokens, string PreviousStatus, string? PreviousFailureClass);
    private sealed record ProbeResult(OpenRouterProviderRouteDescriptor Route, Probe Probe, bool Success, string ResultClass, RequestPacketTelemetry Telemetry)
    {
        public string Label => Probe.Label;
        public string SegmentId => Probe.SegmentId;
    }

    private static ProbeResult MakeSuccess(OpenRouterProviderRouteDescriptor route, Probe probe, RequestPacketTelemetry telemetry) => new(route, probe, true, "SUCCESS", telemetry);
    private static ProbeResult MakeFailure(OpenRouterProviderRouteDescriptor route, Probe probe, RequestPacketTelemetry telemetry, string failure) => new(route, probe, false, failure switch
    {
        ReasoningCompletionFailureClass.ProviderOutputLimit => "OUTPUT_LIMIT",
        ReasoningCompletionFailureClass.ProviderStreamInactivityTimeout => "STREAM_STALL",
        ReasoningCompletionFailureClass.ProviderTotalTimeout or ReasoningCompletionFailureClass.ProviderFirstByteTimeout => "TIMEOUT",
        _ => "PROVIDER_ERROR",
    }, telemetry);
    private static bool IsTimeout(string result) => result is "TIMEOUT" or "STREAM_STALL";
    private static bool IsHistoricalTimeout(string failure) => failure is ReasoningCompletionFailureClass.ProviderTotalTimeout or
        ReasoningCompletionFailureClass.ProviderFirstByteTimeout or ReasoningCompletionFailureClass.ProviderStreamInactivityTimeout;

    private sealed record SelectedLeaf(string Label, SegmentRecoveryTree.SegmentNodeSnapshot Node);

    private static SelectedLeaf[] SelectLeaves(string treePath)
    {
        if (!File.Exists(treePath)) throw new InvalidDataException("S1_TREE_MISSING");
        using var doc = JsonDocument.Parse(File.ReadAllText(treePath));
        var nodes = doc.RootElement.GetProperty("nodes").Deserialize<SegmentRecoveryTree.SegmentNodeSnapshot[]>(JsonOptions) ?? [];
        var leaves = nodes.Where(x => x.Status != SegmentRecoveryState.SplitParent).ToArray();
        var success = leaves.Where(x => x.Status == SegmentRecoveryState.Success).OrderByDescending(x => x.Owned.Sum(a => a.Length)).FirstOrDefault();
        if (success is null) throw new InvalidDataException("SUCCESS_PROBE_LEAF_MISSING");
        var timeout = leaves.Where(x => x.Status == SegmentRecoveryState.FailedTerminal && IsHistoricalTimeout(x.FailureClass ?? ""))
            .OrderBy(x => Math.Abs(x.Owned.Sum(a => a.Length) - success.Owned.Sum(a => a.Length))).FirstOrDefault();
        var smaller = leaves.Where(x => x.Status == SegmentRecoveryState.FailedTerminal && IsHistoricalTimeout(x.FailureClass ?? "") && x.Owned.Sum(a => a.Length) < (timeout?.Owned.Sum(a => a.Length) ?? int.MaxValue))
            .OrderByDescending(x => x.Owned.Sum(a => a.Length)).FirstOrDefault();
        if (timeout is null || smaller is null) throw new InvalidDataException("REPRESENTATIVE_PROBE_LEAVES_MISSING");
        return new[] { success, timeout, smaller }.Select((x, i) => new SelectedLeaf(((char)('A' + i)).ToString(), x)).ToArray();
    }

    private static Probe BuildProbe(SelectedLeaf selected, int _, IReadOnlyList<ReasoningSourceOccurrence> occurrences, IReadOnlyList<InventoryItem> inventory)
    {
        var node = selected.Node;
        var ownedByOccurrence = node.Owned.GroupBy(a => a.OccurrenceIndex).ToDictionary(g => g.Key, g => (g.Min(a => a.CharStart), g.Max(a => a.CharEnd)));
        var visibleByOccurrence = node.Visible.GroupBy(a => a.OccurrenceIndex).ToDictionary(g => g.Key, g => (g.Min(a => a.CharStart), g.Max(a => a.CharEnd)));
        var visible = new List<ReasoningSourceOccurrence>();
        var owned = new HashSet<string>(StringComparer.Ordinal);
        var visibleWindows = new Dictionary<string, (int Start, int End)>(StringComparer.Ordinal);
        var ownedWindows = new Dictionary<string, (int Start, int End)>(StringComparer.Ordinal);
        foreach (var item in visibleByOccurrence.OrderBy(x => x.Key))
        {
            var occurrence = occurrences[item.Key]; visible.Add(occurrence);
            visibleWindows[occurrence.SourceOccurrenceId] = item.Value;
            if (ownedByOccurrence.TryGetValue(item.Key, out var range)) { owned.Add(occurrence.SourceOccurrenceId); ownedWindows[occurrence.SourceOccurrenceId] = range; }
            else ownedWindows[occurrence.SourceOccurrenceId] = (item.Value.Item1, item.Value.Item1);
        }
        var packet = CeilingPacketBuilder.Build(visible, owned, visibleWindows, ownedWindows);
        var localInventory = inventory.Where(x => packet.Bindings.FirstOrDefault(b => b.SourceId == x.SourceId) is { } b && x.Start < b.VisibleEnd && x.End > b.VisibleStart)
            .Select(x => new CeilingHeadingProposal(packet.Bindings.First(b => b.SourceId == x.SourceId).LocalIndex, x.Start - packet.Bindings.First(b => b.SourceId == x.SourceId).VisibleStart, x.End - packet.Bindings.First(b => b.SourceId == x.SourceId).VisibleStart, x.Role)).ToArray();
        var inventoryJson = OmissionReviewPrompt.BuildInventoryJson(localInventory);
        return new Probe(selected.Label, selected.Node.SegmentId, packet.SerializedJson, inventoryJson, packet.SourceTextCharacters, owned.Count, packet.Bindings.Count, selected.Node.Owned.Sum(a => a.Length), packet.SerializedJson.Length, ReasoningTokenBudget.EstimateTokens(packet.SerializedJson.Length), selected.Node.Status, selected.Node.FailureClass);
    }

    private sealed record InventoryItem(string SourceId, int Start, int End, string Role);
    private sealed record DocumentInventoryItem(string DocumentId, string SourcePath, string SourceSha256);

    private static IReadOnlyList<InventoryItem> ReadS0Inventory(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("proposals").EnumerateArray().Select(p => new InventoryItem(p.GetProperty("sourceId").GetString()!, p.GetProperty("headingSpan").GetProperty("start").GetInt32(), p.GetProperty("headingSpan").GetProperty("end").GetInt32(), p.GetProperty("semanticRole").GetString()!)).ToArray();
    }
    private static DocumentInventoryItem[] ReadInventory(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("documents").EnumerateArray().Select(x => new DocumentInventoryItem(x.GetProperty("documentId").GetString()!, x.GetProperty("sourcePath").GetString()!, x.GetProperty("sourceSha256").GetString()!)).ToArray();
    }
    private static RemoteInferenceOptions Clone(RemoteInferenceOptions source, string route) => new()
    {
        Endpoint = source.Endpoint, ApiKey = source.ApiKey, Model = source.Model, ContextSize = source.ContextSize,
        MaxOutputTokens = source.MaxOutputTokens, RequestTimeoutSeconds = source.RequestTimeoutSeconds,
        TransientRequestRetries = 0, MaxParallelRequests = 1, SendChatTemplateKwargs = false, OpenRouterProviderRoute = route,
    };
    private static int ReadBoundedMinutes(string name, int fallback, int min, int max) => int.TryParse(Environment.GetEnvironmentVariable(name), out var v) ? Math.Clamp(v, min, max) : fallback;
    private static int ReadHistoricalAttempts(string output)
    {
        var paths = new[] { Path.Combine(output, "s1", "segments", "segment-tree-state.v1.json"), Path.Combine(output, "s2", "extractor-b-segments", "segment-tree-state.v1.json") };
        return paths.Where(File.Exists).Sum(path => JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("nodes").EnumerateArray().Sum(x => x.GetProperty("attempts").GetInt32()));
    }
    private static void Print(IReadOnlyList<ProbeResult> results, string bottleneck, OpenRouterProviderRouteDescriptor? pinned, int deadline)
    {
        Console.WriteLine("PROVIDER_DIAGNOSIS");
        Console.WriteLine("Provider | Request | Success | TTFT | TotalSec | InputTok | ReasoningTok | OutputTok | TokPerSec");
        foreach (var x in results) Console.WriteLine(string.Join(" | ", x.Route.ProviderName, x.Label, x.Success, "NOT_MEASURED", (x.Telemetry.ElapsedMs / 1000d).ToString("0.###", CultureInfo.InvariantCulture), x.Telemetry.ReportedInputTokens?.ToString() ?? "NOT_MEASURED", x.Telemetry.ReportedReasoningTokens?.ToString() ?? "NOT_MEASURED", x.Telemetry.ReportedOutputTokens?.ToString() ?? "NOT_MEASURED", x.Telemetry.ReportedOutputTokens is { } o && x.Telemetry.ElapsedMs > 0 ? (o / (x.Telemetry.ElapsedMs / 1000d)).ToString("0.###", CultureInfo.InvariantCulture) : "NOT_MEASURED"));
        Console.WriteLine($"PRIMARY_BOTTLENECK={bottleneck}");
        Console.WriteLine($"PINNED_PROVIDER={pinned?.Route ?? "NONE"}");
        Console.WriteLine($"DERIVED_DEADLINE={deadline}s");
    }
    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Sha256Text(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static async Task WriteJsonAsync(string path, object value, CancellationToken ct) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, Encoding.UTF8, ct); }
    private static async Task AppendEventAsync(string path, ProbeResult result, CancellationToken ct) =>
        await File.AppendAllTextAsync(path, JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, provider = result.Route.ProviderName, route = result.Route.Route, request = result.Label, result = result.ResultClass, telemetry = result.Telemetry }, EventJsonOptions) + Environment.NewLine, Encoding.UTF8, ct);
}

public sealed record Qwen9BExecutionProfile(string? PinnedProvider, TimeSpan AttemptDeadline, TimeSpan StreamStallDeadline, string PrimaryBottleneck, string PinReason);
