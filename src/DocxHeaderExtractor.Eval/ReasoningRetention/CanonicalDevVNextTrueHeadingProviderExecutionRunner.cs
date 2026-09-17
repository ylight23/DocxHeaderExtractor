using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>Provider execution for the separately frozen TRUE_HEADING baseline. It consumes only
/// the new materialized requests, performs no retries, persists raw responses before parsing, and
/// freezes the deterministic TRUE_HEADING projection before any Gold access.</summary>
public static class CanonicalDevVNextTrueHeadingProviderExecutionRunner
{
    private const string PreflightRoot = "artifacts/level-accuracy/canonical-vnext-true-heading-doc0116-baseline-v1/preflight-v1";
    private const string ExecutionRoot = "artifacts/level-accuracy/canonical-vnext-true-heading-doc0116-baseline-v1/execution-v1";
    private const string DocumentId = "DOC-0116";
    private const string Model = "qwen/qwen3.7-flash";
    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";
    private const string Route = "ModelCapabilityCeiling";
    private const string SchemaName = "semantic_true_heading_v2";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var preflight = Full(repoRoot, PreflightRoot);
        var execution = Full(repoRoot, ExecutionRoot);
        if (Directory.Exists(execution) && Directory.EnumerateFileSystemEntries(execution).Any())
            return Blocked("EXECUTION_OUTPUT_ALREADY_EXISTS");
        var freezePath = Path.Combine(preflight, "preflight-freeze.v1.json");
        var planPath = Path.Combine(preflight, "request-plan.v1.json");
        var configPath = Path.Combine(preflight, "run-configuration.v1.json");
        if (!File.Exists(freezePath) || !File.Exists(planPath) || !File.Exists(configPath))
            return Blocked("PREFLIGHT_ARTIFACT_MISSING");
        using var freeze = JsonDocument.Parse(await File.ReadAllTextAsync(freezePath, ct));
        using var plan = JsonDocument.Parse(await File.ReadAllTextAsync(planPath, ct));
        var freezeRoot = freeze.RootElement;
        var planRoot = plan.RootElement;
        if (freezeRoot.GetProperty("status").GetString() != "READY_FOR_TRUE_HEADING_PROVIDER_EXECUTION" ||
            freezeRoot.GetProperty("goldReads").GetInt32() != 0 || freezeRoot.GetProperty("providerCalls").GetInt32() != 0 ||
            freezeRoot.GetProperty("predictionFrozen").GetBoolean())
            return Blocked("PREFLIGHT_NOT_AUTHORITY");
        // The preflight freezes runConfigurationHash over the canonical UTF-8 JSON text. The
        // artifact was written by a text API that may emit a BOM, so validate its canonical text
        // bytes rather than treating an encoding marker as a configuration mutation.
        var runConfigurationText = await File.ReadAllTextAsync(configPath, ct);
        if (!string.Equals(Sha256File(planPath), freezeRoot.GetProperty("requestPlanSha256").GetString(), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Sha256(runConfigurationText), freezeRoot.GetProperty("runConfigurationHash").GetString(), StringComparison.OrdinalIgnoreCase))
            return Blocked("FROZEN_PLAN_OR_CONFIG_HASH_MISMATCH");

        var requests = planRoot.GetProperty("requests").EnumerateArray().ToArray();
        var aliases = await LoadAliasesAsync(Path.Combine(preflight, "source-universe.v1.json"), ct);
        if (aliases.Count != 1_921 || !planRoot.GetProperty("everyAliasOwnedExactlyOnce").GetBoolean())
            return Blocked("SOURCE_UNIVERSE_OR_OWNERSHIP_MISMATCH");
        var owned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var request in requests)
        {
            foreach (var alias in request.GetProperty("ownedAliases").EnumerateArray().Select(x => x.GetString()!))
                if (!owned.Add(alias) || !aliases.ContainsKey(alias)) return Blocked("SEGMENT_OWNERSHIP_MISMATCH");
            var ordinal = request.GetProperty("requestOrdinal").GetInt32();
            var materializedPath = Path.Combine(preflight, $"materialized-request-{ordinal:000}.v1.json");
            if (!File.Exists(materializedPath)) return Blocked($"MATERIALIZED_REQUEST_MISSING_{ordinal:000}");
            using var materialized = JsonDocument.Parse(await File.ReadAllTextAsync(materializedPath, ct));
            var root = materialized.RootElement;
            if (!string.Equals(Sha256(root.GetProperty("packetText").GetString()!), request.GetProperty("packetSha256").GetString(), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Sha256(root.GetProperty("userPrompt").GetString()!), request.GetProperty("userPromptSha256").GetString(), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Sha256(root.GetProperty("schemaText").GetString()!), request.GetProperty("schemaSha256").GetString(), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(root.GetProperty("providerRequestBodySha256").GetString(), request.GetProperty("providerRequestBodySha256").GetString(), StringComparison.OrdinalIgnoreCase))
                return Blocked($"MATERIALIZED_REQUEST_HASH_MISMATCH_{ordinal:000}");
        }
        if (owned.Count != aliases.Count) return Blocked("FULL_ALIAS_OWNERSHIP_MISMATCH");

        var remote = RemoteInferenceOptions.FromEnvironment("openrouter");
        remote.Model = freezeRoot.GetProperty("model").GetString() ?? Model;
        remote.Endpoint = new Uri(freezeRoot.GetProperty("endpoint").GetString() ?? Endpoint);
        remote.ContextSize = 1_000_000;
        remote.MaxOutputTokens = 48_000;
        remote.RequestTimeoutSeconds = freezeRoot.GetProperty("requestTimeoutSeconds").GetInt32();
        remote.TransientRequestRetries = 0;
        remote.MissingIdRetries = 0;
        remote.MaxParallelRequests = 1;
        remote.OpenRouterProviderRoute = null;
        remote.OpenRouterAllowNonZdrPublicBenchmark = true;
        if (string.IsNullOrWhiteSpace(remote.ApiKey)) return Blocked("OPENROUTER_API_KEY_MISSING");
        var capability = new OpenRouterModelCapability
        {
            ModelId = remote.Model, ContextLength = 1_000_000, ReasoningSupported = true,
            SelectedReasoningEffort = "enabled", ReasoningEnabled = true, EffortListReported = false,
            StructuredOutputSupported = true, MaxCompletionTokens = remote.MaxOutputTokens,
        };
        Directory.CreateDirectory(execution);
        await WriteJsonAsync(Path.Combine(execution, "execution-start.v1.json"), new
        {
            schemaVersion = "a99-true-heading-execution-v1", status = "PROVIDER_EXECUTION_STARTED",
            preflightFreezeSha256 = Sha256File(freezePath), requestCount = requests.Length,
            goldReads = 0, providerCalls = 0, scoring = false,
        }, ct);

        using var model = new OpenRouterCeilingReasoningModel(remote, capability,
            attemptDeadline: TimeSpan.FromSeconds(freezeRoot.GetProperty("perAttemptHardTimeoutSeconds").GetInt32()));
        var allItems = new List<SemanticTextTrueHeadingItem>();
        var attemptCount = 0;
        try
        {
            foreach (var request in requests.OrderBy(item => item.GetProperty("requestOrdinal").GetInt32()))
            {
                ct.ThrowIfCancellationRequested();
                var ordinal = request.GetProperty("requestOrdinal").GetInt32();
                var ownedAliases = request.GetProperty("ownedAliases").EnumerateArray().Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal);
                var materializedPath = Path.Combine(preflight, $"materialized-request-{ordinal:000}.v1.json");
                using var materialized = JsonDocument.Parse(await File.ReadAllTextAsync(materializedPath, ct));
                var root = materialized.RootElement;
                var packet = root.GetProperty("packetText").GetString()!;
                var system = root.GetProperty("systemPrompt").GetString()!;
                var user = root.GetProperty("userPrompt").GetString()!;
                var schemaText = root.GetProperty("schemaText").GetString()!;
                var schema = JsonDocument.Parse(schemaText).RootElement.Clone();
                var sourceChars = ownedAliases.Sum(alias => aliases[alias].Text.Length);
                var frozenWire = new FrozenWireRequestExpectation(root.GetProperty("providerRequestBodyBytesUtf8").GetInt32(), root.GetProperty("providerRequestBodySha256").GetString()!);
                var response = await model.CompleteRawStructuredSemanticAsync(DocumentId, Route, root.GetProperty("requestId").GetString()!, packet, sourceChars, ownedAliases.Count, root.GetProperty("packet").GetProperty("sourceAliases").GetArrayLength(), system, user, schema, SchemaName, ct, frozenWire);
                attemptCount++;

                // Persist the provider response before parsing or binding.
                await WriteJsonAsync(Path.Combine(execution, $"segment-{ordinal:000}.attempt.v1.json"), new
                {
                    schemaVersion = "a99-true-heading-attempt-v1", requestOrdinal = ordinal,
                    requestId = root.GetProperty("requestId").GetString(), requestSha256 = request.GetProperty("providerRequestBodySha256").GetString(),
                    rawResponse = response.Content, rawResponseSha256 = Sha256(response.Content), telemetry = response.Telemetry,
                }, ct);
                var parsed = SemanticTextTrueHeadingContract.Parse(response.Content);
                var outside = parsed.Headings.Where(item => !ownedAliases.Contains(item.Source)).Select(item => item.Source).Distinct(StringComparer.Ordinal).ToArray();
                if (outside.Length > 0) throw new InvalidDataException($"PROPOSAL_OUTSIDE_SEGMENT:{ordinal}:{string.Join(',', outside)}");
                allItems.AddRange(parsed.Headings);
                await WriteJsonAsync(Path.Combine(execution, $"segment-{ordinal:000}.parsed.v1.json"), new
                {
                    schemaVersion = "a99-true-heading-parsed-v1", requestOrdinal = ordinal, itemCount = parsed.Headings.Count, items = parsed.Headings,
                }, ct);
            }
        }
        catch (Exception ex)
        {
            await WriteJsonAsync(Path.Combine(execution, "execution-failure.v1.json"), new
            {
                schemaVersion = "a99-true-heading-execution-failure-v1", status = "PROVIDER_EXECUTION_FAILED",
                failure = ex.GetType().Name + ":" + ex.Message, completedProviderCalls = model.ProviderCalls, persistedAttempts = attemptCount,
                goldReads = 0, scoring = false, retry = 0,
            }, ct);
            Console.WriteLine("STATUS=PROVIDER_EXECUTION_FAILED");
            Console.WriteLine($"PROVIDER_CALLS={model.ProviderCalls}");
            Console.WriteLine("GOLD_READS=0");
            return 2;
        }

        var binding = BindAll(allItems, aliases);
        await WriteJsonAsync(Path.Combine(execution, "binding-forensic.v1.json"), new
        {
            schemaVersion = "a99-true-heading-binding-forensic-v1", rawProviderItems = allItems.Count,
            boundItems = binding.Bound.Count, observations = binding.Observations,
            failureCounts = binding.Observations.Where(item => item.Status != "BOUND").GroupBy(item => item.Status).ToDictionary(group => group.Key, group => group.Count()),
        }, ct);
        var trueProjection = binding.Bound.Where(item => item.HeadingDecision == "TRUE_HEADING").ToArray();
        var prediction = new
        {
            schemaVersion = "a99-true-heading-prediction-v1", documentId = DocumentId,
            target = "ALL TRUE HEADING OCCURRENCES", protocol = SemanticTextTrueHeadingContract.ProtocolVersion,
            allBoundStructuralItems = binding.Bound, trueHeadingProjection = trueProjection,
            rawProviderItems = allItems.Count, boundItems = binding.Bound.Count,
            trueHeadingItems = trueProjection.Length, notTrueHeadingItems = binding.Bound.Count(item => item.HeadingDecision == "NOT_TRUE_HEADING"),
            providerCalls = model.ProviderCalls, goldReads = 0, scoring = false,
        };
        var predictionPath = Path.Combine(execution, "prediction.v1.json");
        await WriteJsonAsync(predictionPath, prediction, ct);
        await WriteJsonAsync(Path.Combine(execution, "prediction-freeze.v1.json"), new
        {
            schemaVersion = "a99-true-heading-prediction-freeze-v1", status = "PREDICTION_FROZEN_BEFORE_GOLD",
            documentId = DocumentId, predictionSha256 = Sha256File(predictionPath), requestPlanSha256 = Sha256File(planPath),
            segmentCount = requests.Length, providerCalls = model.ProviderCalls, retries = 0,
            rawProviderItems = allItems.Count, boundItems = binding.Bound.Count, trueHeadingItems = trueProjection.Length,
            goldReads = 0, scoring = false, predictionFrozen = true,
        }, ct);
        await WriteJsonAsync(Path.Combine(execution, "execution-complete.v1.json"), new
        {
            schemaVersion = "a99-true-heading-execution-v1", status = "PREDICTION_FROZEN_BEFORE_GOLD",
            providerCalls = model.ProviderCalls, expectedProviderCalls = requests.Length, retries = 0, goldReads = 0, scoring = false,
        }, ct);
        Console.WriteLine("STATUS=PREDICTION_FROZEN_BEFORE_GOLD");
        Console.WriteLine($"PROVIDER_CALLS={model.ProviderCalls}");
        Console.WriteLine($"TRUE_HEADING_ITEMS={trueProjection.Length}");
        Console.WriteLine("GOLD_READS=0");
        return model.ProviderCalls == requests.Length ? 0 : 2;
    }

    private static BindingResult BindAll(IReadOnlyList<SemanticTextTrueHeadingItem> items, IReadOnlyDictionary<string, Alias> aliases)
    {
        var bound = new List<BoundItem>();
        var observations = new List<Observation>();
        var occupied = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            if (!aliases.TryGetValue(item.Source, out var alias)) { observations.Add(new(index, item.Source, "UNKNOWN_ALIAS", null)); continue; }
            var positions = FindExact(alias.Text, item.Text);
            if (positions.Count == 0) { observations.Add(new(index, item.Source, "NON_VERBATIM_TEXT", null)); continue; }
            if (positions.Count > 1)
            {
                var selected = SelectDuplicate(alias.Text, item, positions);
                if (selected is null) { observations.Add(new(index, item.Source, "AMBIGUOUS_BINDING", null)); continue; }
                positions = [selected.Value];
            }
            var start = positions[0];
            var end = start + item.Text.Length;
            var key = $"{alias.SourceId}:{start}:{end}";
            if (!occupied.Add(key)) { observations.Add(new(index, item.Source, "DUPLICATE_PHYSICAL_BINDING", start)); continue; }
            bound.Add(new(alias.AliasName, alias.SourceId, alias.SourceOrdinal, item.Text, item.HeadingDecision, item.Role, start, end));
            observations.Add(new(index, item.Source, "BOUND", start));
        }
        return new(bound, observations);
    }

    private static List<int> FindExact(string source, string text)
    {
        var result = new List<int>();
        for (var offset = 0; offset <= source.Length - text.Length;)
        {
            var index = source.IndexOf(text, offset, StringComparison.Ordinal);
            if (index < 0) break;
            result.Add(index);
            offset = index + Math.Max(1, text.Length);
        }
        return result;
    }
    private static int? SelectDuplicate(string source, SemanticTextTrueHeadingItem item, IReadOnlyList<int> positions)
    {
        if (item.Occurrence is { } occurrence) return occurrence <= positions.Count ? positions[occurrence - 1] : null;
        for (var i = 0; i < positions.Count; i++)
        {
            var start = positions[i]; var end = start + item.Text.Length;
            var left = item.LeftExactContext is null || start >= item.LeftExactContext.Length && source.Substring(start - item.LeftExactContext.Length, item.LeftExactContext.Length) == item.LeftExactContext;
            var right = item.RightExactContext is null || end + item.RightExactContext.Length <= source.Length && source.Substring(end, item.RightExactContext.Length) == item.RightExactContext;
            if (left && right) return start;
        }
        return null;
    }
    private static async Task<Dictionary<string, Alias>> LoadAliasesAsync(string path, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path, ct));
        return doc.RootElement.GetProperty("sourceIdentity").EnumerateArray().ToDictionary(item => item.GetProperty("alias").GetString()!, item => new Alias(item.GetProperty("alias").GetString()!, item.GetProperty("sourceId").GetString()!, item.GetProperty("sourceOrdinal").GetInt32(), item.GetProperty("text").GetString()!), StringComparer.Ordinal);
    }
    private static string Full(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    private static async Task WriteJsonAsync(string path, object value, CancellationToken ct) => await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, ct);
    private static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static int Blocked(string reason) { Console.WriteLine($"STATUS=BLOCKED:{reason}"); Console.WriteLine("PROVIDER_CALLS=0"); Console.WriteLine("GOLD_READS=0"); return 2; }

    private sealed record Alias(string AliasName, string SourceId, int SourceOrdinal, string Text);
    private sealed record BoundItem(string Alias, string SourceId, int SourceOrdinal, string Text, string HeadingDecision, string Role, int Start, int End);
    private sealed record Observation(int RawOrdinal, string Source, string Status, int? Start);
    private sealed record BindingResult(IReadOnlyList<BoundItem> Bound, IReadOnlyList<Observation> Observations);
}
