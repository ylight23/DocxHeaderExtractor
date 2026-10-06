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

/// <summary>P6T-D is the matched P6T-C arm with only an explicit total abstention role added.</summary>
internal static class P6TDExplicitAbstentionCanary
{
    private const string Root = "artifacts/v5-p6t-total-occurrence-role/p6td-explicit-abstention";
    private const string SnapshotRoot = "eval/a99-closed-loop/pdf-canonical-source-v1";
    private const string Confirm = "yes-i-authorize-p6td-explicit-abstention-two-calls";
    private const string Src089 = "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf";
    private const string Src095 = "todo10_8/heading_corpus_100/07_system_generated/095_RFC9114_HTTP_3.pdf";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private sealed record Item(string Role, string DocumentId, PdfCandidateAuthorityDocumentPlan Plan, PdfTotalRolePreparedPackD Prepared);

    public static async Task<int> RunAsync(string repo, string[] args)
    {
        var directory = Path.Combine(repo, Root.Replace('/', Path.DirectorySeparatorChar));
        var manifestPath = Path.Combine(directory, "execution-manifest.v1.json");
        var resultPath = Path.Combine(directory, "result.v1.json");
        var checkpointPath = Path.Combine(directory, "result.in-progress.v1.json");
        if (File.Exists(resultPath) || File.Exists(checkpointPath)) return Fail("p6td: immutable result/checkpoint exists; stop before network");
        var items = Build(repo);
        var manifest = BuildManifest(repo, items);
        Directory.CreateDirectory(directory);
        if (!File.Exists(manifestPath)) { WriteNew(manifestPath, manifest); Console.WriteLine("P6T-D manifest prepared; ProviderCalls=0, GoldRead=false."); return 0; }
        if (!ManifestParity(manifestPath, manifest)) return Fail("p6td: frozen manifest/body parity failed; no network");
        if (!args.Contains($"--confirm-p6td={Confirm}")) { Console.WriteLine("P6T-D PREPARED_NOT_AUTHORIZED; ProviderCalls=0, GoldRead=false."); return 0; }
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY"))) return Fail("p6td: OPENROUTER_API_KEY is not set");
        var options = RemoteInferenceOptions.FromEnvironment();
        options.Model = "qwen/qwen3.7-flash"; options.OpenRouterProviderRoute = "alibaba"; options.OpenRouterReasoningEffort = "none";
        options.RequireJsonObjectResponse = true; options.TransientRequestRetries = 0; options.ProviderTransportTimeoutSeconds = 300; options.Validate();
        var rows = new List<JsonElement>();
        foreach (var item in items)
        {
            AtomicWrite(checkpointPath, new { state = "IN_FLIGHT", providerCallsCompletedAndPersisted = rows.Count, maximumProviderCalls = 2, retry = 0, repair = false, fallback = false, goldRead = false });
            OpenRouterExecutionObservation? response = null; string? error = null; var watch = Stopwatch.StartNew();
            try { using var client = OpenRouterQualificationTransport.CreateOwned(options); response = await client.ExecuteObservedAsync(item.Prepared.ProviderBody, item.Prepared.SourcePack.MaxCompletionTokens, item.Prepared.Request.SystemPrompt, item.Prepared.Request.UserMessage).ConfigureAwait(false); }
            catch (Exception ex) { error = ex.Message; }
            watch.Stop();
            var analysis = Analyze(item.Prepared, response, error);
            var reasoningTokens = Usage(response?.Usage, "completion_tokens_details", "reasoning_tokens");
            rows.Add(JsonSerializer.SerializeToElement(new
            {
                role = item.Role, documentId = item.DocumentId, packId = item.Prepared.SourcePack.PackId, issuedOccurrences = item.Prepared.Request.Occurrences.Count,
                semanticRequestHash = item.Prepared.Request.UserMessageSha256, systemPromptSha256 = Hash(item.Prepared.Request.SystemPrompt),
                providerRequestHash = item.Prepared.ProviderRequestHash, providerRequestBytes = item.Prepared.ProviderRequestBytes, maxCompletionTokens = item.Prepared.SourcePack.MaxCompletionTokens,
                sourceSha256 = item.Plan.SourceSha256, sourceUniverseSha256 = item.Plan.SourceUniverseSha256,
                reasoningRequested = true, reasoningTokens, reasoningExecutionConfirmed = State(reasoningTokens), promptTokens = Usage(response?.Usage, "prompt_tokens"), completionTokens = Usage(response?.Usage, "completion_tokens"),
                transportAccepted = response is not null, transportError = error, finishReason = response?.FinishReason, retryCount = response?.RetryCount ?? 0, latencyMs = watch.Elapsed.TotalMilliseconds,
                rawSseSha256 = response is null ? null : Hash(response.RawSse), rawResponseSha256 = response is null ? null : Hash(response.Content), rawResponseUtf8Bytes = response is null ? 0 : Encoding.UTF8.GetByteCount(response.Content), rawResponse = response?.Content, analysis,
            }));
            AtomicWrite(checkpointPath, new { state = "READY", providerCallsCompletedAndPersisted = rows.Count, maximumProviderCalls = 2, retry = 0, repair = false, fallback = false, goldRead = false, rows });
            Console.WriteLine($"[{rows.Count}/2] {item.DocumentId}: {Classify(analysis)}, finish={response?.FinishReason ?? "n/a"}, reasoning={State(reasoningTokens)}");
        }
        WriteNew(resultPath, new { schemaVersion = "v5-p6td-explicit-abstention-two-pack-result-v1", providerCalls = 2, maximumAuthorizedProviderCalls = 2, goldRead = false, semanticScore = "NOT_RUN", retry = 0, repair = false, fallback = false, sharedRuntime = "UNCHANGED", rows });
        File.Delete(checkpointPath); return 0;
    }

    private static Item[] Build(string repo)
    {
        var result = new List<Item>();
        foreach (var (role, id, source) in new[] { ("MULTIPART_ANCHOR_EXTENT_HEAVY", "SRC-089", Src089), ("TOC_NEGATIVE_ROLE_HEAVY", "SRC-095", Src095) })
        {
            var hash = CanonicalSemanticSourceHash.Compute(Path.Combine(repo, source.Replace('/', Path.DirectorySeparatorChar)));
            var plan = PdfCandidateAuthorityQualificationAdapter.PrepareFromSnapshot(Path.Combine(repo, SnapshotRoot.Replace('/', Path.DirectorySeparatorChar), hash + ".json"), id);
            var pack = plan.Packs.Single(value => value.PackId.EndsWith("PACK_001", StringComparison.Ordinal));
            var prepared = PdfTotalOccurrenceRoleQualificationAdapter.PrepareWithExplicitAbstentionAndReadOnlyCorrespondences(plan, pack, BuildCorrespondences(pack));
            if (prepared.Request.Occurrences.Count != 96 || prepared.Request.Occurrences.Select(value => value.Id).Distinct(StringComparer.Ordinal).Count() != 96) throw new InvalidOperationException($"p6td-issued-ledger-invalid:{id}");
            result.Add(new Item(role, id, plan, prepared));
        }
        return result.OrderBy(value => value.DocumentId, StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>> BuildCorrespondences(PdfCandidateAuthorityPreparedPack pack)
    {
        var owned = pack.OwnedAliases.ToHashSet(StringComparer.Ordinal); var candidates = pack.Universe.Candidates.ToDictionary(value => value.Id, StringComparer.Ordinal);
        var result = new Dictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>>(StringComparer.Ordinal);
        foreach (var relation in pack.Universe.Relations)
        {
            if (!candidates.TryGetValue(relation.CandidateId, out var candidate) || !owned.Contains(candidate.Endpoint.Parts[0].Alias)) continue;
            var key = candidate.Endpoint.Parts[0].Alias; var list = result.TryGetValue(key, out var existing) ? existing.ToList() : new List<V5ReadOnlyCorrespondenceV1>();
            if (!list.Any(value => value.TargetPage == relation.TargetPage && value.TargetText == relation.TargetText)) list.Add(new(relation.TargetPage, relation.TargetText));
            result[key] = list;
        }
        return result;
    }

    private static object BuildManifest(string repo, IReadOnlyList<Item> items) => new
    {
        schemaVersion = "v5-p6td-explicit-abstention-two-pack-manifest-v1", status = "PREPARED_NOT_AUTHORIZED", preparedAtHead = GitHead(repo), providerCalls = 0, goldRead = false, goldMutation = "NONE", sharedRuntime = "UNCHANGED",
        purpose = "MATCHED_P6TC_PLUS_EXPLICIT_ABSTENTION_ONLY: total anchor-role ledger with UNRESOLVED; no Pass 2 or accuracy claim.",
        treatment = new { gateway = "OpenRouter", model = "qwen/qwen3.7-flash", providerPin = "alibaba", temperature = 0, reasoning = new { enabled = true }, effort = "OMITTED", responseFormat = "json_object" },
        roles = new[] { "HEADING_START", "REPRESENTATION_START", "OTHER", "UNRESOLVED" }, contractGate = new { issued = 96, returned = 96, unknownOccurrenceIds = 0, duplicateOccurrenceIds = 0, missingOccurrenceIds = 0, invalidRoles = 0, extraProperties = 0, parser = "ACCEPTED", unresolvedNotEligibleForPass2 = true },
        execution = new { maximumProviderCalls = 2, exactlyOnePrimaryAttemptPerPack = true, retry = 0, repair = false, fallback = false, goldDuringRun = false, pass2 = "BLOCKED" },
        rows = items.Select(item => new { item.Role, item.DocumentId, item.Prepared.SourcePack.PackId, issuedOccurrences = item.Prepared.Request.Occurrences.Count, semanticRequestHash = item.Prepared.Request.UserMessageSha256, systemPromptSha256 = Hash(item.Prepared.Request.SystemPrompt), providerRequestHash = item.Prepared.ProviderRequestHash, providerRequestBytes = item.Prepared.ProviderRequestBytes, maxCompletionTokens = item.Prepared.SourcePack.MaxCompletionTokens, sourceSha256 = item.Plan.SourceSha256, sourceUniverseSha256 = item.Plan.SourceUniverseSha256 }).ToArray(),
    };

    private static object Analyze(PdfTotalRolePreparedPackD pack, OpenRouterExecutionObservation? response, string? error)
    {
        if (response is null || error is not null) return new { classification = "TRANSPORT_ERROR", parserAccepted = false, expectedDecisions = 96, error };
        if (!string.Equals(response.FinishReason, "stop", StringComparison.OrdinalIgnoreCase)) return new { classification = response.FinishReason == "length" ? "INCOMPLETE_PROVIDER_OUTPUT" : "NONTERMINAL_PROVIDER_OUTPUT", parserAccepted = false, expectedDecisions = 96 };
        try { var parsed = PdfTotalOccurrenceRoleQualificationAdapter.Parse(pack, response.Content); return new { classification = "TOTAL_LEDGER_ACCEPTED", parserAccepted = true, expectedDecisions = 96, returnedDecisions = parsed.Decisions.Count, unresolvedDecisions = parsed.Decisions.Count(value => value.Role == V5OccurrenceRoleD.UNRESOLVED), unknownOccurrenceIds = 0, duplicateOccurrenceIds = 0, missingOccurrenceIds = 0, invalidRoles = 0, extraProperties = 0 }; }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return new { classification = "TOTAL_LEDGER_CONTRACT_FAILURE", parserAccepted = false, expectedDecisions = 96, parserFailure = ex.Message }; }
    }
    private static int? Usage(JsonElement? usage, params string[] path) { if (usage is not { ValueKind: JsonValueKind.Object } current) return null; foreach (var part in path) if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out current)) return null; return current.ValueKind == JsonValueKind.Number && current.TryGetInt32(out var value) ? value : null; }
    private static string State(int? tokens) => tokens switch { > 0 => "TRUE", 0 => "FALSE", _ => "UNKNOWN" };
    private static string Classify(object value) => value.GetType().GetProperty("classification")?.GetValue(value)?.ToString() ?? "UNKNOWN";
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool ManifestParity(string path, object manifest) { try { var frozen = JsonNode.Parse(File.ReadAllText(path))!.AsObject(); var current = JsonSerializer.SerializeToNode(manifest)!.AsObject(); frozen.Remove("preparedAtHead"); current.Remove("preparedAtHead"); return JsonNode.DeepEquals(frozen, current); } catch { return false; } }
    private static void WriteNew(string path, object value) { if (File.Exists(path)) throw new InvalidOperationException("p6td-immutable-artifact-exists"); AtomicWrite(path, value); }
    private static void AtomicWrite(string path, object value) { var tmp = path + ".tmp"; File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json) + Environment.NewLine, new UTF8Encoding(false)); File.Move(tmp, path, true); }
    private static int Fail(string message) { Console.Error.WriteLine(message); return 1; }
    private static string? GitHead(string repo) { try { using var p = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD") { WorkingDirectory = repo, RedirectStandardOutput = true, UseShellExecute = false }); var output = p!.StandardOutput.ReadToEnd().Trim(); p.WaitForExit(); return p.ExitCode == 0 ? output : null; } catch { return null; } }
}
