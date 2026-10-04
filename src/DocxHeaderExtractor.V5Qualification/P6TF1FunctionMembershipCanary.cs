using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.V5Qualification;

/// <summary>
/// Two-call P6T-F1 function-membership canary. It consumes the provider-free frozen preflight only,
/// never P6T-E1 output, and performs no retry, repair, fallback, grouping, extent, or Gold work.
/// </summary>
internal static class P6TF1FunctionMembershipCanary
{
    private const string Root = "artifacts/v5-p6t-function-membership/p6tf1-preflight";
    private const string SnapshotRoot = "eval/a99-closed-loop/pdf-canonical-source-v1";
    private const string ManifestName = "two-pack-function-membership-manifest.v1.json";
    private const string Confirm = "yes-i-authorize-p6tf1-function-membership-two-calls";
    private const string Src089 = "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf";
    private const string Src095 = "todo10_8/heading_corpus_100/07_system_generated/095_RFC9114_HTTP_3.pdf";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private sealed record Item(string DocumentId, PdfFunctionMembershipPreparedPackF1 Prepared);

    public static async Task<int> RunAsync(string repo, string[] args)
    {
        var directory = Path.Combine(repo, Root.Replace('/', Path.DirectorySeparatorChar));
        var manifestPath = Path.Combine(directory, ManifestName);
        var resultPath = Path.Combine(directory, "result.v1.json");
        var checkpointPath = Path.Combine(directory, "result.in-progress.v1.json");
        if (File.Exists(resultPath) || File.Exists(checkpointPath))
            return Fail("p6tf1: immutable result/checkpoint exists; stop before network");
        if (!File.Exists(manifestPath))
            return Fail("p6tf1: frozen preflight manifest is missing; stop before network");

        var items = Build(repo);
        if (!MatchesFrozenPreflight(manifestPath, items))
            return Fail("p6tf1: frozen preflight/body parity failed; stop before network");
        if (!args.Contains($"--confirm-p6tf1={Confirm}"))
        {
            Console.WriteLine("P6T-F1 PREPARED_NOT_AUTHORIZED; ProviderCalls=0, GoldRead=false.");
            return 0;
        }
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")))
            return Fail("p6tf1: OPENROUTER_API_KEY is not set");

        var options = RemoteInferenceOptions.FromEnvironment();
        options.Model = "qwen/qwen3.7-flash";
        options.OpenRouterProviderRoute = "alibaba";
        options.OpenRouterReasoningEffort = "none";
        options.RequireJsonObjectResponse = true;
        options.TransientRequestRetries = 0;
        options.MaxParallelRequests = 1;
        options.ProviderTransportTimeoutSeconds = 300;
        options.Validate();

        var rows = new List<JsonElement>();
        foreach (var item in items)
        {
            AtomicWrite(checkpointPath, new { state = "IN_FLIGHT", providerCallsCompletedAndPersisted = rows.Count, maximumProviderCalls = 2, retry = 0, repair = false, fallback = false, goldRead = false });
            OpenRouterExecutionObservation? response = null;
            string? error = null;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                using var client = OpenRouterHeaderExtractor.CreateOwned(options);
                response = await client.ExecuteObservedAsync(item.Prepared.ProviderBody, item.Prepared.SourcePack.MaxCompletionTokens, item.Prepared.Request.SystemPrompt, item.Prepared.Request.UserMessage).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            stopwatch.Stop();
            var analysis = Analyze(item.Prepared, response, error);
            var reasoningTokens = Usage(response?.Usage, "completion_tokens_details", "reasoning_tokens");
            rows.Add(JsonSerializer.SerializeToElement(new
            {
                documentId = item.DocumentId,
                packId = item.Prepared.SourcePack.PackId,
                issuedOccurrences = item.Prepared.Request.Occurrences.Count,
                semanticRequestHash = item.Prepared.Request.UserMessageSha256,
                systemPromptSha256 = Hash(item.Prepared.Request.SystemPrompt),
                providerRequestHash = item.Prepared.ProviderRequestHash,
                providerRequestBytes = item.Prepared.ProviderRequestBytes,
                reasoningRequested = true,
                reasoningTokens,
                reasoningExecutionConfirmed = ReasoningState(reasoningTokens),
                promptTokens = Usage(response?.Usage, "prompt_tokens"),
                completionTokens = Usage(response?.Usage, "completion_tokens"),
                transportAccepted = response is not null,
                transportError = error,
                finishReason = response?.FinishReason,
                retryCount = response?.RetryCount ?? 0,
                latencyMs = stopwatch.Elapsed.TotalMilliseconds,
                rawSseSha256 = response is null ? null : Hash(response.RawSse),
                rawResponseSha256 = response is null ? null : Hash(response.Content),
                rawResponseUtf8Bytes = response is null ? 0 : Encoding.UTF8.GetByteCount(response.Content),
                rawResponse = response?.Content,
                analysis,
            }));
            AtomicWrite(checkpointPath, new { state = "READY", providerCallsCompletedAndPersisted = rows.Count, maximumProviderCalls = 2, retry = 0, repair = false, fallback = false, goldRead = false, rows });
            Console.WriteLine($"[{rows.Count}/2] {item.DocumentId}: {Classification(analysis)}, finish={response?.FinishReason ?? "n/a"}, reasoning={ReasoningState(reasoningTokens)}");
        }

        WriteNew(resultPath, new
        {
            schemaVersion = "v5-p6tf1-function-membership-two-pack-result-v1",
            providerCalls = 2,
            maximumAuthorizedProviderCalls = 2,
            goldRead = false,
            semanticScore = "NOT_RUN",
            retry = 0,
            repair = false,
            fallback = false,
            segmentationDependency = "NONE",
            groupingPass = "BLOCKED",
            exactExtentPass = "BLOCKED",
            sharedRuntime = "UNCHANGED",
            rows,
        });
        File.Delete(checkpointPath);
        return 0;
    }

    private static Item[] Build(string repo)
    {
        var list = new List<Item>();
        foreach (var (documentId, source) in new[] { ("SRC-089", Src089), ("SRC-095", Src095) })
        {
            var sourceHash = CanonicalSemanticSourceHash.Compute(Path.Combine(repo, source.Replace('/', Path.DirectorySeparatorChar)));
            var plan = PdfCandidateAuthorityQualificationAdapter.PrepareFromSnapshot(Path.Combine(repo, SnapshotRoot.Replace('/', Path.DirectorySeparatorChar), sourceHash + ".json"), documentId);
            var pack = plan.Packs.Single(value => value.PackId.EndsWith("PACK_001", StringComparison.Ordinal));
            var prepared = PdfTotalOccurrenceRoleQualificationAdapter.PrepareFunctionMembershipF1(plan, pack, Correspondences(pack));
            if (prepared.Request.Occurrences.Count != 96)
                throw new InvalidOperationException($"p6tf1-issued-ledger-invalid:{documentId}");
            list.Add(new Item(documentId, prepared));
        }
        return list.OrderBy(value => value.DocumentId, StringComparer.Ordinal).ToArray();
    }

    private static bool MatchesFrozenPreflight(string manifestPath, IReadOnlyList<Item> items)
    {
        try
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = manifest.RootElement;
            if (root.GetProperty("status").GetString() != "PREPARED_NOT_AUTHORIZED" || root.GetProperty("providerCalls").GetInt32() != 0 || root.GetProperty("goldRead").GetBoolean() || root.GetProperty("consumesP6TE1Output").GetBoolean() || root.GetProperty("segmentationDependency").GetString() != "NONE")
                return false;
            var rows = root.GetProperty("rows").EnumerateArray().OrderBy(value => value.GetProperty("documentId").GetString(), StringComparer.Ordinal).ToArray();
            if (rows.Length != items.Count) return false;
            for (var index = 0; index < items.Count; index++)
            {
                var frozen = rows[index];
                var current = items[index];
                if (frozen.GetProperty("documentId").GetString() != current.DocumentId ||
                    frozen.GetProperty("packId").GetString() != current.Prepared.SourcePack.PackId ||
                    frozen.GetProperty("issuedOccurrences").GetInt32() != current.Prepared.Request.Occurrences.Count ||
                    frozen.GetProperty("requestHash").GetString() != current.Prepared.Request.UserMessageSha256 ||
                    frozen.GetProperty("systemPromptSha256").GetString() != Hash(current.Prepared.Request.SystemPrompt) ||
                    frozen.GetProperty("providerRequestHash").GetString() != current.Prepared.ProviderRequestHash ||
                    frozen.GetProperty("providerRequestBytes").GetInt32() != current.Prepared.ProviderRequestBytes ||
                    frozen.GetProperty("maxCompletionTokens").GetInt32() != current.Prepared.SourcePack.MaxCompletionTokens)
                    return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return false;
        }
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>> Correspondences(PdfCandidateAuthorityPreparedPack pack)
    {
        var owned = pack.OwnedAliases.ToHashSet(StringComparer.Ordinal);
        var candidates = pack.Universe.Candidates.ToDictionary(value => value.Id, StringComparer.Ordinal);
        var result = new Dictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>>(StringComparer.Ordinal);
        foreach (var relation in pack.Universe.Relations)
        {
            if (!candidates.TryGetValue(relation.CandidateId, out var candidate) || !owned.Contains(candidate.Endpoint.Parts[0].Alias)) continue;
            var key = candidate.Endpoint.Parts[0].Alias;
            var values = result.TryGetValue(key, out var old) ? old.ToList() : new List<V5ReadOnlyCorrespondenceV1>();
            if (!values.Any(value => value.TargetPage == relation.TargetPage && value.TargetText == relation.TargetText))
                values.Add(new V5ReadOnlyCorrespondenceV1(relation.TargetPage, relation.TargetText));
            result[key] = values;
        }
        return result;
    }

    private static object Analyze(PdfFunctionMembershipPreparedPackF1 pack, OpenRouterExecutionObservation? response, string? error)
    {
        if (response is null || error is not null)
            return new { classification = "TRANSPORT_ERROR", parserAccepted = false, error };
        if (!string.Equals(response.FinishReason, "stop", StringComparison.OrdinalIgnoreCase))
            return new { classification = response.FinishReason == "length" ? "INCOMPLETE_PROVIDER_OUTPUT" : "NONTERMINAL_PROVIDER_OUTPUT", parserAccepted = false };
        try
        {
            var parsed = PdfTotalOccurrenceRoleQualificationAdapter.ParseFunctionMembershipF1(pack, response.Content);
            return new
            {
                classification = "TOTAL_FUNCTION_MEMBERSHIP_LEDGER_ACCEPTED",
                parserAccepted = true,
                expectedDecisions = 96,
                returnedDecisions = parsed.Decisions.Count,
                establishesStructure = parsed.Decisions.Count(value => value.Function == V5OccurrenceFunctionF1.ESTABLISHES_STRUCTURE),
                representsStructure = parsed.Decisions.Count(value => value.Function == V5OccurrenceFunctionF1.REPRESENTS_STRUCTURE),
                other = parsed.Decisions.Count(value => value.Function == V5OccurrenceFunctionF1.OTHER),
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return new { classification = "TOTAL_FUNCTION_MEMBERSHIP_CONTRACT_FAILURE", parserAccepted = false, parserFailure = ex.Message };
        }
    }

    private static int? Usage(JsonElement? usage, params string[] path)
    {
        if (usage is not { ValueKind: JsonValueKind.Object } current) return null;
        foreach (var part in path)
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out current)) return null;
        return current.ValueKind == JsonValueKind.Number && current.TryGetInt32(out var value) ? value : null;
    }

    private static string ReasoningState(int? tokens) => tokens switch { > 0 => "TRUE", 0 => "FALSE", _ => "UNKNOWN" };
    private static string Classification(object value) => value.GetType().GetProperty("classification")?.GetValue(value)?.ToString() ?? "UNKNOWN";
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static void WriteNew(string path, object value) { if (File.Exists(path)) throw new InvalidOperationException("p6tf1-immutable-artifact-exists"); AtomicWrite(path, value); }
    private static void AtomicWrite(string path, object value) { var temporary = path + ".tmp"; File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json) + Environment.NewLine, new UTF8Encoding(false)); File.Move(temporary, path, true); }
    private static int Fail(string message) { Console.Error.WriteLine(message); return 1; }
}
