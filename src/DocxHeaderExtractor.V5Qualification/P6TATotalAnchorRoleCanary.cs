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

/// <summary>
/// P6T-A is deliberately a two-call contract-viability canary.  It does not read Gold or score
/// semantic quality: its sole question is whether the provider can complete the total 96/96 anchor-role ledger.
/// </summary>
internal static class P6TATotalAnchorRoleCanary
{
    private const string Root = "artifacts/v5-p6t-total-occurrence-role/p6ta-two-pack-canary";
    private const string SnapshotRoot = "eval/a99-closed-loop/pdf-canonical-source-v1";
    private const string Confirm = "yes-i-authorize-p6ta-total-anchor-role-two-calls";
    private const string Src089 = "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf";
    private const string Src095 = "todo10_8/heading_corpus_100/07_system_generated/095_RFC9114_HTTP_3.pdf";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private sealed record Item(string Role, string DocumentId, PdfCandidateAuthorityDocumentPlan Plan, PdfTotalRolePreparedPack Prepared);

    public static async Task<int> RunAsync(string repo, string[] args)
    {
        var directory = Path.Combine(repo, Root.Replace('/', Path.DirectorySeparatorChar));
        var manifestPath = Path.Combine(directory, "execution-manifest.v1.json");
        var resultPath = Path.Combine(directory, "result.v1.json");
        var checkpointPath = Path.Combine(directory, "result.in-progress.v1.json");
        if (File.Exists(resultPath)) return Fail("p6ta: immutable result already exists; stop before network");
        if (File.Exists(checkpointPath)) return Fail("p6ta: ambiguous in-progress checkpoint exists; stop before network");

        var items = Build(repo);
        var manifest = BuildManifest(repo, items);
        Directory.CreateDirectory(directory);
        if (!File.Exists(manifestPath))
        {
            WriteNew(manifestPath, manifest);
            Console.WriteLine("P6T-A two-pack manifest prepared; ProviderCalls=0, GoldRead=false. Separate authorization required.");
            return 0;
        }
        if (!ManifestParity(manifestPath, manifest)) return Fail("p6ta: frozen manifest/body parity failed; no network call");
        if (!args.Contains($"--confirm-p6ta={Confirm}"))
        {
            Console.WriteLine("P6T-A PREPARED_NOT_AUTHORIZED; ProviderCalls=0, GoldRead=false.");
            return 0;
        }
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")))
            return Fail("p6ta: OPENROUTER_API_KEY is not set; ProviderCalls=0");

        var options = RemoteInferenceOptions.FromEnvironment();
        options.Model = "qwen/qwen3.7-flash";
        options.OpenRouterProviderRoute = "alibaba";
        // The frozen exact body carries reasoning.enabled=true and deliberately omits effort.
        options.OpenRouterReasoningEffort = "none";
        options.RequireJsonObjectResponse = true;
        options.TransientRequestRetries = 0;
        options.MaxParallelRequests = 1;
        options.ProviderTransportTimeoutSeconds = 300;
        options.Validate();

        var rows = new List<JsonElement>();
        foreach (var item in items)
        {
            AtomicWrite(checkpointPath, Checkpoint("IN_FLIGHT", rows, item));
            OpenRouterExecutionObservation? response = null;
            string? transportError = null;
            var watch = Stopwatch.StartNew();
            try
            {
                using var client = OpenRouterHeaderExtractor.CreateOwned(options);
                response = await client.ExecuteObservedAsync(item.Prepared.ProviderBody, item.Prepared.SourcePack.MaxCompletionTokens,
                    item.Prepared.Request.SystemPrompt, item.Prepared.Request.UserMessage).ConfigureAwait(false);
            }
            catch (Exception exception) { transportError = exception.Message; }
            watch.Stop();

            var analysis = Analyze(item, response, transportError);
            var reasoningTokens = ReadUsage(response?.Usage, "completion_tokens_details", "reasoning_tokens");
            rows.Add(JsonSerializer.SerializeToElement(new
            {
                role = item.Role, documentId = item.DocumentId, item.Prepared.SourcePack.PackId,
                issuedOccurrences = item.Prepared.Request.Occurrences.Count,
                semanticRequestHash = item.Prepared.Request.UserMessageSha256,
                systemPromptSha256 = Hash(item.Prepared.Request.SystemPrompt),
                providerRequestHash = item.Prepared.ProviderRequestHash, providerRequestBytes = item.Prepared.ProviderRequestBytes,
                maxCompletionTokens = item.Prepared.SourcePack.MaxCompletionTokens,
                sourceSha256 = item.Plan.SourceSha256, sourceUniverseSha256 = item.Plan.SourceUniverseSha256,
                reasoningRequested = true, reasoningTokens,
                reasoningExecutionConfirmed = ReasoningExecutionState(reasoningTokens),
                promptTokens = ReadUsage(response?.Usage, "prompt_tokens"), completionTokens = ReadUsage(response?.Usage, "completion_tokens"),
                transportAccepted = response is not null, transportError, finishReason = response?.FinishReason,
                retryCount = response?.RetryCount ?? 0, latencyMs = watch.Elapsed.TotalMilliseconds, sseEventCount = response?.SseEventCount ?? 0,
                rawSseSha256 = response is null ? null : Hash(response.RawSse), rawSse = response?.RawSse,
                rawResponseSha256 = response is null ? null : Hash(response.Content), rawResponseUtf8Bytes = response is null ? 0 : Encoding.UTF8.GetByteCount(response.Content),
                rawResponse = response?.Content, analysis,
            }));
            AtomicWrite(checkpointPath, Checkpoint("READY", rows, null));
            Console.WriteLine($"[{rows.Count}/2] {item.DocumentId}/{item.Prepared.SourcePack.PackId}: {Classification(analysis)}, finish={response?.FinishReason ?? "n/a"}, reasoning={reasoningTokens?.ToString() ?? "unknown"}, retry={response?.RetryCount ?? 0}");
        }

        if (rows.Count != 2) return Fail("p6ta: call accounting did not reach exactly two primary attempts");
        WriteNew(resultPath, new
        {
            schemaVersion = "v5-p6ta-total-anchor-role-two-pack-result-v1", sourceManifest = $"{Root}/execution-manifest.v1.json", startedFromHead = GitHead(repo),
            providerCalls = 2, maximumAuthorizedProviderCalls = 2,
            treatment = new { gateway = "OpenRouter", model = "qwen/qwen3.7-flash", providerPin = "alibaba", temperature = 0, reasoning = new { enabled = true }, effort = "OMITTED", responseFormat = "json_object" },
            retry = 0, repair = false, fallback = false, goldRead = false, goldMutation = "NONE", semanticScore = "NOT_RUN", sharedRuntime = "UNCHANGED",
            stopPolicy = "CLOSED_AFTER_2_PRIMARY_CALLS_NO_RETRY_REPAIR_FALLBACK_OR_PASS2", rows,
        });
        File.Delete(checkpointPath);
        Console.WriteLine("P6T-A raw two-call contract gate closed. Gold/source review, accuracy claims, and Pass 2 remain blocked.");
        return 0;
    }

    private static Item[] Build(string repo)
    {
        var items = new List<Item>();
        foreach (var (role, id, pdf) in new[]
                 {
                     ("MULTIPART_ANCHOR_EXTENT_HEAVY", "SRC-089", Src089),
                     ("TOC_NEGATIVE_ROLE_HEAVY", "SRC-095", Src095),
                 })
        {
            var sourceHash = CanonicalSemanticSourceHash.Compute(Path.Combine(repo, pdf.Replace('/', Path.DirectorySeparatorChar)));
            var plan = PdfCandidateAuthorityQualificationAdapter.PrepareFromSnapshot(Path.Combine(repo, SnapshotRoot.Replace('/', Path.DirectorySeparatorChar), sourceHash + ".json"), id);
            var sourcePack = plan.Packs.Single(value => value.PackId.EndsWith("PACK_001", StringComparison.Ordinal));
            var prepared = PdfTotalOccurrenceRoleQualificationAdapter.Prepare(plan, sourcePack);
            if (prepared.Request.Occurrences.Count != 96 || prepared.Request.Occurrences.Select(value => value.Id).Distinct(StringComparer.Ordinal).Count() != 96)
                throw new InvalidOperationException($"p6ta-issued-ledger-invalid:{id}");
            items.Add(new Item(role, id, plan, prepared));
        }
        return items.OrderBy(value => value.DocumentId, StringComparer.Ordinal).ToArray();
    }

    private static object BuildManifest(string repo, IReadOnlyList<Item> items) => new
    {
        schemaVersion = "v5-p6ta-total-anchor-role-two-pack-manifest-v1", status = "PREPARED_NOT_AUTHORIZED", preparedAtHead = GitHead(repo),
        providerCalls = 0, goldRead = false, goldMutation = "NONE", sharedRuntime = "UNCHANGED",
        purpose = "CONTRACT_VIABILITY_ONLY: can qwen/qwen3.7-flash with enabled reasoning return the exact total 96/96 anchor-role ledger? This manifest makes no accuracy or ontology claim.",
        treatment = new { gateway = "OpenRouter", model = "qwen/qwen3.7-flash", providerPin = "alibaba", temperature = 0, reasoning = new { enabled = true }, effort = "OMITTED", responseFormat = "json_object" },
        contractGate = new { issued = 96, returned = 96, finishReason = "stop", unknownOccurrenceIds = 0, duplicateOccurrenceIds = 0, missingOccurrenceIds = 0, invalidRoles = 0, extraProperties = 0, parser = "ACCEPTED" },
        execution = new { maximumProviderCalls = 2, exactlyOnePrimaryAttemptPerPack = true, retry = 0, repair = false, fallback = false, goldDuringRun = false, pass2 = "BLOCKED" },
        failureClassification = new { stopButInvalidLedger = "TOTAL_LEDGER_CONTRACT_FAILURE", finishLength = "INCOMPLETE_PROVIDER_OUTPUT", otherNonStop = "NONTERMINAL_PROVIDER_OUTPUT", transport = "TRANSPORT_ERROR" },
        rows = items.Select(item => new
        {
            item.Role, item.DocumentId, item.Prepared.SourcePack.PackId, issuedOccurrences = item.Prepared.Request.Occurrences.Count,
            semanticRequestHash = item.Prepared.Request.UserMessageSha256, systemPromptSha256 = Hash(item.Prepared.Request.SystemPrompt),
            providerRequestHash = item.Prepared.ProviderRequestHash, providerRequestBytes = item.Prepared.ProviderRequestBytes, maxCompletionTokens = item.Prepared.SourcePack.MaxCompletionTokens,
            sourceSha256 = item.Plan.SourceSha256, sourceUniverseSha256 = item.Plan.SourceUniverseSha256,
        }).ToArray(),
    };

    private static object Analyze(Item item, OpenRouterExecutionObservation? response, string? transportError)
    {
        if (response is null || transportError is not null)
            return new { classification = "TRANSPORT_ERROR", parserAccepted = false, expectedDecisions = 96, returnedDecisions = 0, error = transportError };
        if (!string.Equals(response.FinishReason, "stop", StringComparison.OrdinalIgnoreCase))
            return new { classification = string.Equals(response.FinishReason, "length", StringComparison.OrdinalIgnoreCase) ? "INCOMPLETE_PROVIDER_OUTPUT" : "NONTERMINAL_PROVIDER_OUTPUT", parserAccepted = false, expectedDecisions = 96, returnedDecisions = 0, responseBytes = Encoding.UTF8.GetByteCount(response.Content) };
        try
        {
            var parsed = PdfTotalOccurrenceRoleQualificationAdapter.Parse(item.Prepared, response.Content);
            return new
            {
                classification = "TOTAL_LEDGER_ACCEPTED", parserAccepted = true, expectedDecisions = item.Prepared.Request.Occurrences.Count,
                returnedDecisions = parsed.Decisions.Count, unknownOccurrenceIds = 0, duplicateOccurrenceIds = 0, missingOccurrenceIds = 0, invalidRoles = 0, extraProperties = 0,
            };
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return new { classification = "TOTAL_LEDGER_CONTRACT_FAILURE", parserAccepted = false, expectedDecisions = item.Prepared.Request.Occurrences.Count,
                returnedDecisions = (int?)null, parserFailure = exception.Message };
        }
    }

    private static bool ManifestParity(string path, object manifest)
    {
        try { return JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllText(path)), JsonSerializer.SerializeToNode(manifest)); }
        catch (Exception exception) when (exception is IOException or JsonException) { return false; }
    }

    private static object Checkpoint(string state, IReadOnlyList<JsonElement> rows, Item? next) => new
    {
        schemaVersion = "v5-p6ta-total-anchor-role-checkpoint-v1", state, providerCallsCompletedAndPersisted = rows.Count, maximumProviderCalls = 2,
        retry = 0, repair = false, fallback = false, goldRead = false,
        next = next is null ? null : new { next.Role, next.DocumentId, next.Prepared.SourcePack.PackId, providerRequestHash = next.Prepared.ProviderRequestHash }, rows,
    };

    private static int? ReadUsage(JsonElement? usage, params string[] path)
    {
        if (usage is not { ValueKind: JsonValueKind.Object } current) return null;
        foreach (var segment in path) if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current)) return null;
        return current.ValueKind == JsonValueKind.Number && current.TryGetInt32(out var value) ? value : null;
    }

    private static string ReasoningExecutionState(int? tokens) => tokens switch { > 0 => "TRUE", 0 => "FALSE", _ => "UNKNOWN" };
    private static string Classification(object analysis) => analysis.GetType().GetProperty("classification")?.GetValue(analysis)?.ToString() ?? "UNKNOWN";
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static void WriteNew(string path, object value)
    {
        if (File.Exists(path)) throw new InvalidOperationException($"p6ta-immutable-artifact-exists:{path}");
        AtomicWrite(path, value);
    }
    private static void AtomicWrite(string path, object value)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json) + Environment.NewLine, new UTF8Encoding(false));
        File.Move(temporary, path, true);
    }
    private static int Fail(string message) { Console.Error.WriteLine(message); return 1; }
    private static string? GitHead(string repo)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD") { WorkingDirectory = repo, RedirectStandardOutput = true, UseShellExecute = false });
            var output = process!.StandardOutput.ReadToEnd().Trim(); process.WaitForExit();
            return process.ExitCode == 0 ? output : null;
        }
        catch { return null; }
    }
}
