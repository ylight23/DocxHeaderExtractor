using DocxHeaderExtractor.DocumentProcessing.Provenance;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.V5Qualification;

internal static class P6TCCorrespondenceEvidenceCanary
{
    private const string Root = "artifacts/v5-p6t-total-occurrence-role/p6tc-correspondence-evidence";
    private const string SnapshotRoot = "eval/a99-closed-loop/pdf-canonical-source-v1";
    private const string Confirm = "yes-i-authorize-p6tc-correspondence-evidence-two-calls";
    private const string Src089 = "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf";
    private const string Src095 = "todo10_8/heading_corpus_100/07_system_generated/095_RFC9114_HTTP_3.pdf";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private sealed record Item(string DocumentId, PdfCandidateAuthorityDocumentPlan Plan, PdfTotalRolePreparedPack Prepared);

    public static async Task<int> RunAsync(string repo, string[] args)
    {
        var dir = Path.Combine(repo, Root.Replace('/', Path.DirectorySeparatorChar));
        var manifestPath = Path.Combine(dir, "execution-manifest.v1.json");
        var resultPath = Path.Combine(dir, "result.v1.json");
        var checkpointPath = Path.Combine(dir, "result.in-progress.v1.json");
        if (File.Exists(resultPath) || File.Exists(checkpointPath)) return Fail("p6tc: result/checkpoint exists; stop before network");
        var items = Build(repo);
        Directory.CreateDirectory(dir);
        if (!File.Exists(manifestPath)) { WriteNew(manifestPath, Manifest(repo, items)); Console.WriteLine("P6T-C manifest prepared; ProviderCalls=0, GoldRead=false."); return 0; }
        if (!Parity(manifestPath, Manifest(repo, items))) return Fail("p6tc: frozen manifest/body parity failed; no network");
        if (!args.Contains($"--confirm-p6tc={Confirm}")) { Console.WriteLine("P6T-C PREPARED_NOT_AUTHORIZED; ProviderCalls=0, GoldRead=false."); return 0; }
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY"))) return Fail("p6tc: OPENROUTER_API_KEY is not set");
        var options = RemoteInferenceOptions.FromEnvironment(); options.Model = "qwen/qwen3.7-flash"; options.OpenRouterProviderRoute = "alibaba"; options.OpenRouterReasoningEffort = "none"; options.RequireJsonObjectResponse = true; options.TransientRequestRetries = 0; options.ProviderTransportTimeoutSeconds = 300; options.Validate();
        var rows = new List<JsonElement>();
        foreach (var item in items)
        {
            AtomicWrite(checkpointPath, new { state = "IN_FLIGHT", providerCallsCompletedAndPersisted = rows.Count, maximumProviderCalls = 2, retry = 0, repair = false, fallback = false, goldRead = false });
            OpenRouterExecutionObservation? observation = null; string? error = null; var watch = Stopwatch.StartNew();
            try { using var client = OpenRouterQualificationTransport.CreateOwned(options); observation = await client.ExecuteObservedAsync(item.Prepared.ProviderBody, item.Prepared.SourcePack.MaxCompletionTokens, item.Prepared.Request.SystemPrompt, item.Prepared.Request.UserMessage).ConfigureAwait(false); }
            catch (Exception ex) { error = ex.Message; }
            watch.Stop();
            var analysis = Analyze(item.Prepared, observation, error);
            rows.Add(JsonSerializer.SerializeToElement(new
            {
                documentId = item.DocumentId, packId = item.Prepared.SourcePack.PackId, issuedOccurrences = 96,
                providerRequestHash = item.Prepared.ProviderRequestHash, providerRequestBytes = item.Prepared.ProviderRequestBytes,
                reasoningRequested = true, reasoningTokens = Usage(observation?.Usage, "completion_tokens_details", "reasoning_tokens"), reasoningExecutionConfirmed = State(Usage(observation?.Usage, "completion_tokens_details", "reasoning_tokens")),
                promptTokens = Usage(observation?.Usage, "prompt_tokens"), completionTokens = Usage(observation?.Usage, "completion_tokens"), finishReason = observation?.FinishReason,
                retryCount = observation?.RetryCount ?? 0, latencyMs = watch.Elapsed.TotalMilliseconds, transportError = error,
                rawSseSha256 = observation is null ? null : Hash(observation.RawSse), rawResponseSha256 = observation is null ? null : Hash(observation.Content), rawResponse = observation?.Content, analysis,
            }));
            AtomicWrite(checkpointPath, new { state = "READY", providerCallsCompletedAndPersisted = rows.Count, maximumProviderCalls = 2, retry = 0, repair = false, fallback = false, goldRead = false, rows });
            Console.WriteLine($"[{rows.Count}/2] {item.DocumentId}: {Classify(analysis)}, finish={observation?.FinishReason ?? "n/a"}, reasoning={State(Usage(observation?.Usage, "completion_tokens_details", "reasoning_tokens"))}");
        }
        WriteNew(resultPath, new { schemaVersion = "v5-p6tc-correspondence-evidence-result-v1", providerCalls = 2, maximumAuthorizedProviderCalls = 2, goldRead = false, semanticScore = "NOT_RUN", retry = 0, repair = false, fallback = false, sharedRuntime = "UNCHANGED", rows });
        File.Delete(checkpointPath); return 0;
    }

    private static Item[] Build(string repo)
    {
        var result = new List<Item>();
        foreach (var (id, source) in new[] { ("SRC-089", Src089), ("SRC-095", Src095) })
        {
            var hash = CanonicalSemanticSourceHash.Compute(Path.Combine(repo, source.Replace('/', Path.DirectorySeparatorChar)));
            var plan = PdfCandidateAuthorityQualificationAdapter.PrepareFromSnapshot(Path.Combine(repo, SnapshotRoot.Replace('/', Path.DirectorySeparatorChar), hash + ".json"), id);
            var pack = plan.Packs.Single(value => value.PackId.EndsWith("PACK_001", StringComparison.Ordinal));
            var atoms = plan.SourceAtoms.ToDictionary(value => value.Alias, StringComparer.Ordinal);
            var owned = pack.OwnedAliases.ToHashSet(StringComparer.Ordinal);
            var candidates = pack.Universe.Candidates.ToDictionary(value => value.Id, StringComparer.Ordinal);
            var edges = new Dictionary<string, List<V5ReadOnlyCorrespondenceV1>>(StringComparer.Ordinal);
            foreach (var relation in pack.Universe.Relations)
            {
                if (!candidates.TryGetValue(relation.CandidateId, out var candidate) || !owned.Contains(candidate.Endpoint.Parts[0].Alias)) continue;
                var key = candidate.Endpoint.Parts[0].Alias; if (!edges.TryGetValue(key, out var list)) edges[key] = list = new();
                if (!list.Any(value => value.TargetPage == relation.TargetPage && value.TargetText == relation.TargetText)) list.Add(new(relation.TargetPage, relation.TargetText));
            }
            result.Add(new(id, plan, PdfTotalOccurrenceRoleQualificationAdapter.PrepareWithReadOnlyCorrespondences(plan, pack, edges.ToDictionary(value => value.Key, value => (IReadOnlyList<V5ReadOnlyCorrespondenceV1>)value.Value, StringComparer.Ordinal))));
        }
        return result.OrderBy(value => value.DocumentId, StringComparer.Ordinal).ToArray();
    }

    private static object Manifest(string repo, IReadOnlyList<Item> items) => new { schemaVersion = "v5-p6tc-correspondence-evidence-execution-manifest-v1", status = "PREPARED_NOT_AUTHORIZED", preparedAtHead = GitHead(repo), providerCalls = 0, goldRead = false, goldMutation = "NONE", sharedRuntime = "UNCHANGED", treatment = new { model = "qwen/qwen3.7-flash", provider = "alibaba", temperature = 0, reasoning = new { enabled = true }, effort = "OMITTED", responseFormat = "json_object" }, matchedVariable = "ONLY_READ_ONLY_CORRESPONDENCES", execution = new { maximumProviderCalls = 2, retry = 0, repair = false, fallback = false, pass2 = "BLOCKED" }, rows = items.Select(item => new { item.DocumentId, item.Prepared.SourcePack.PackId, issuedOccurrences = 96, requestHash = item.Prepared.Request.UserMessageSha256, providerRequestHash = item.Prepared.ProviderRequestHash, providerRequestBytes = item.Prepared.ProviderRequestBytes }).ToArray() };
    private static object Analyze(PdfTotalRolePreparedPack pack, OpenRouterExecutionObservation? observation, string? error)
    {
        if (observation is null) return new { classification = "TRANSPORT_ERROR", parserAccepted = false, error };
        if (!string.Equals(observation.FinishReason, "stop", StringComparison.OrdinalIgnoreCase)) return new { classification = observation.FinishReason == "length" ? "INCOMPLETE_PROVIDER_OUTPUT" : "NONTERMINAL_PROVIDER_OUTPUT", parserAccepted = false };
        try { using var json = JsonDocument.Parse(observation.Content); var parsed = PdfTotalOccurrenceRoleQualificationAdapter.Parse(pack, observation.Content); return new { classification = "TOTAL_LEDGER_ACCEPTED", parserAccepted = true, expectedDecisions = 96, returnedDecisions = parsed.Decisions.Count, unknownOccurrenceIds = 0, duplicateOccurrenceIds = 0, missingOccurrenceIds = 0, invalidRoles = 0, extraProperties = 0 }; }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return new { classification = "TOTAL_LEDGER_CONTRACT_FAILURE", parserAccepted = false, error = ex.Message }; }
    }
    private static int? Usage(JsonElement? usage, params string[] path) { if (usage is not { ValueKind: JsonValueKind.Object } current) return null; foreach (var part in path) if (!current.TryGetProperty(part, out current)) return null; return current.ValueKind == JsonValueKind.Number && current.TryGetInt32(out var value) ? value : null; }
    private static string State(int? tokens) => tokens switch { > 0 => "TRUE", 0 => "FALSE", _ => "UNKNOWN" };
    private static string Classify(object value) => value.GetType().GetProperty("classification")?.GetValue(value)?.ToString() ?? "UNKNOWN";
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool Parity(string path, object value) { try { var a = JsonNode.Parse(File.ReadAllText(path))!.AsObject(); var b = JsonSerializer.SerializeToNode(value)!.AsObject(); a.Remove("preparedAtHead"); b.Remove("preparedAtHead"); return JsonNode.DeepEquals(a, b); } catch { return false; } }
    private static void WriteNew(string path, object value) { if (File.Exists(path)) throw new InvalidOperationException("p6tc-immutable-artifact-exists"); AtomicWrite(path, value); }
    private static void AtomicWrite(string path, object value) { var tmp = path + ".tmp"; File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json) + Environment.NewLine, new UTF8Encoding(false)); File.Move(tmp, path, true); }
    private static int Fail(string message) { Console.Error.WriteLine(message); return 1; }
    private static string? GitHead(string repo) { try { using var p = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD") { WorkingDirectory = repo, RedirectStandardOutput = true, UseShellExecute = false }); var output = p!.StandardOutput.ReadToEnd().Trim(); p.WaitForExit(); return output; } catch { return null; } }
}
