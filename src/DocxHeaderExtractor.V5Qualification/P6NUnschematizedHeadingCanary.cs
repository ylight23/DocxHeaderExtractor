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

/// <summary>Provider-free prepare plus separately-authorized four-call P6N unconstrained-output arm.</summary>
internal static class P6NUnschematizedHeadingCanary
{
    private const string ConfirmSentinel = "yes-i-authorize-p6n-unconstrained-output-canary-4-calls";
    private const string Root = "artifacts/v5-p6n-unconstrained-output-ceiling";
    private const string P6LRoot = "artifacts/v5-p6l-canonical-locator-contract";
    private const string P6IRoot = "artifacts/v5-p6i-compact-locator-directory";
    private const string Src089 = "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf";
    private const string Src095 = "todo10_8/heading_corpus_100/07_system_generated/095_RFC9114_HTTP_3.pdf";
    private const int ResponseCap = 49_152;
    private static readonly DocumentTaskContract Contract = DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
    private static readonly V5ProviderEnvelope Envelope = new("qwen/qwen3.7-flash", "alibaba", "none", true, "json_object", 300)
    { UsageInclude = true, OpenRouterResponseCacheDisabled = true };
    private static readonly (string Role, string DocumentId, string Pdf, int ParentOrdinal)[] Roles =
    [
        ("MAX_REQUEST_BODY", "SRC-089", Src089, 4),
        ("L1472_OWNER_OMISSION", "SRC-095", Src095, 17),
        ("L1710_RETYPING", "SRC-095", Src095, 20),
        ("MULTIPART_RELATION", "SRC-095", Src095, 11),
    ];

    private sealed record Item(string Role, string DocumentId, int ParentOrdinal, V5PackedDecisionRequestV3 Pack,
        V5FreeHeadingRequestV1 Request, RequestLocalLocatorRegistry Registry, byte[] Body, string BodyHash, string SourceEvidenceHash);

    public static async Task<int> RunAsync(string repo, string[] args)
    {
        var authorized = args.Contains($"--confirm-p6n-unconstrained-output={ConfirmSentinel}");
        var directory = Path.Combine(repo, Root.Replace('/', Path.DirectorySeparatorChar));
        var manifestPath = Path.Combine(directory, "execution-manifest.v1.json");
        var resultPath = Path.Combine(directory, "result.v1.json");
        var checkpointPath = Path.Combine(directory, "result.in-progress.v1.json");
        if (File.Exists(resultPath) || File.Exists(checkpointPath)) return Fail("p6n-free-schema: result/checkpoint already exists; stop before network");
        var items = BuildItems(repo);
        if (!File.Exists(manifestPath))
        {
            Directory.CreateDirectory(directory);
            Write(manifestPath, BuildManifest(repo, items));
            Console.WriteLine("P6N unconstrained-output manifest prepared; ProviderCalls=0, GoldRead=false. A NEW explicit authorization is required.");
            Print(items);
            return 0;
        }
        if (!ValidateManifest(manifestPath, items)) return Fail("p6n-free-schema: frozen raw-body parity failed; no network call");
        Print(items);
        Console.WriteLine("P6N unconstrained-output parity PASS. ProviderCalls=0 unless newly authorized.");
        if (!authorized) return 0;
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY"))) return Fail("p6n-free-schema: OPENROUTER_API_KEY is not set");

        var options = RemoteInferenceOptions.FromEnvironment();
        options.Model = Envelope.Model;
        options.OpenRouterProviderRoute = Envelope.Provider;
        options.OpenRouterReasoningEffort = "none"; // Frozen wire body uses reasoning.enabled=true, no effort.
        options.TransientRequestRetries = 0;
        options.Validate();
        var rows = new List<object>();
        Write(checkpointPath, Checkpoint(rows));
        foreach (var item in items)
        {
            OpenRouterExecutionObservation? observation = null;
            string? transportError = null;
            var watch = Stopwatch.StartNew();
            try
            {
                using var client = OpenRouterHeaderExtractor.CreateOwned(options);
                observation = await client.ExecuteObservedUnconstrainedAsync(item.Body, item.Pack.MaxCompletionTokens,
                    item.Request.SystemPrompt, item.Request.UserMessage);
            }
            catch (Exception exception) { transportError = exception.Message; }
            watch.Stop();
            var bytes = observation is null ? 0 : Encoding.UTF8.GetByteCount(observation.Content);
            rows.Add(new
            {
                role = item.Role, documentId = item.DocumentId, parentOrdinal = item.ParentOrdinal, packId = item.Pack.PackId,
                semanticRequestHash = item.Request.UserMessageSha256, sourceEvidenceHash = item.SourceEvidenceHash,
                registryFingerprint = item.Registry.Fingerprint, providerRequestHash = item.BodyHash, providerRequestBytes = item.Body.Length,
                promptTokens = ReadUsage(observation?.Usage, "prompt_tokens"), completionTokens = ReadUsage(observation?.Usage, "completion_tokens"),
                reasoningTokens = ReadUsage(observation?.Usage, "completion_tokens_details", "reasoning_tokens"),
                finishReason = observation?.FinishReason, latencyMs = watch.Elapsed.TotalMilliseconds, responseBytes = bytes,
                retryCount = observation?.RetryCount ?? 0, transportError, rawSseEventCount = observation?.SseEventCount ?? 0,
                rawSseSha256 = observation is null ? null : Hash(observation.RawSse), rawSse = observation?.RawSse,
                rawResponseSha256 = observation is null ? null : Hash(observation.Content), rawResponse = observation?.Content,
                outputHandling = new { parser = "NOT_RUN_BY_DESIGN", binder = "NOT_RUN_BY_DESIGN", schemaValidity = "NOT_APPLICABLE_NO_SCHEMA_DECLARED" },
                responseCapStatus = bytes == 0 ? "NO_CONTENT" : bytes <= ResponseCap ? "WITHIN_CAP" : "OVER_CAP_OBSERVED",
            });
            Write(checkpointPath, Checkpoint(rows));
            Console.WriteLine($"[{item.Role}] calls={rows.Count}/4 finish={observation?.FinishReason ?? "error"} bytes={bytes} prompt={ReadUsage(observation?.Usage, "prompt_tokens")?.ToString() ?? "n/a"} reasoning={ReadUsage(observation?.Usage, "completion_tokens_details", "reasoning_tokens")?.ToString() ?? "n/a"}");
        }

        Write(resultPath, new
        {
            schemaVersion = "v5-p6n-unconstrained-output-ceiling-result-v1", head = GitHead(repo),
            sourceManifest = $"{Root}/execution-manifest.v1.json", providerCalls = rows.Count, maximumAuthorizedProviderCalls = 4,
            route = new { gateway = "OpenRouter", model = Envelope.Model, providerPin = Envelope.Provider, temperature = 0, reasoning = new { enabled = true } },
            responseFormat = "OMITTED", outputSchema = "UNCONSTRAINED_BY_DESIGN", maxResponseUtf8Bytes = ResponseCap,
            retries = 0, repair = false, fallback = false, postFilter = false, parser = "NOT_RUN", binder = "NOT_RUN",
            goldRead = false, semanticScore = "NOT_RUN", sharedRuntime = "UNCHANGED", stopGate = "CLOSED_AFTER_4_CALLS", rows,
        });
        File.Delete(checkpointPath);
        Console.WriteLine("P6N unconstrained-output gate closed after four one-attempt calls; raw responses only; no parsing, binding, Gold, or scoring.");
        return 0;
    }

    private static List<Item> BuildItems(string repo)
    {
        var p6l = JsonNode.Parse(File.ReadAllText(Path.Combine(repo, P6LRoot, "execution-manifest.v1.json")))!.AsObject();
        var p6i = JsonNode.Parse(File.ReadAllText(Path.Combine(repo, P6IRoot, "audit.v1.json")))!.AsObject();
        if (p6l["providerCalls"]?.GetValue<int>() != 0 || p6l["goldRead"]?.GetValue<bool>() != false ||
            p6l["executionGate"]?["maximumProviderCalls"]?.GetValue<int>() != 4 ||
            p6i["providerCalls"]?.GetValue<int>() != 0 || p6i["goldRead"]?.GetValue<bool>() != false)
            throw new InvalidOperationException("p6n-free-schema-source-authority-gate-invalid");
        var cache = new Dictionary<string, (IReadOnlyList<V5PackedDecisionRequestV3> Packs, Dictionary<string, SemanticSourceAtom> Atoms)>();
        var result = new List<Item>();
        foreach (var role in Roles)
        {
            if (!cache.TryGetValue(role.DocumentId, out var source))
            {
                var pdf = Path.Combine(repo, role.Pdf.Replace('/', Path.DirectorySeparatorChar));
                source = (V5PdfPreflightBuilder.BuildV3(pdf, role.DocumentId, Contract,
                        V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId, Envelope),
                    V5PdfPreflightBuilder.LoadAtoms(pdf).ToDictionary(atom => atom.Alias, StringComparer.Ordinal));
                cache.Add(role.DocumentId, source);
            }
            var pack = source.Packs.Single(candidate => candidate.PackId == $"{V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId}:PACK_{role.ParentOrdinal:000}");
            var registry = RequestLocalLocatorRegistry.Create(pack.OwnedAliases.Select(alias => source.Atoms[alias]).ToArray());
            var canonical = V5SparseCandidateRequestComposerV1.ComposeCompactDirectoryCanonical(Contract, pack.Packet, registry);
            var request = V5FreeHeadingCandidateProtocolV1.ComposeUnschematized(canonical);
            var sourceEvidenceHash = Hash(SourceEvidenceProjection(request.UserMessage));
            var body = V5FreeHeadingCandidateProtocolV1.BuildUnconstrainedProviderBody(request, pack.MaxCompletionTokens);
            var sourceRow = p6l["rows"]!.AsArray().Single(row => row!["role"]!.GetValue<string>() == role.Role)!;
            if (sourceRow["registryFingerprint"]?.GetValue<string>() != registry.Fingerprint ||
                sourceRow["documentId"]?.GetValue<string>() != role.DocumentId ||
                sourceRow["packId"]?.GetValue<string>() != pack.PackId ||
                sourceRow["ownedAtoms"]?.GetValue<int>() != pack.OwnedAliases.Count)
                throw new InvalidOperationException($"p6n-free-schema-source-lineage-mismatch:{role.Role}");
            result.Add(new(role.Role, role.DocumentId, role.ParentOrdinal, pack, request, registry, body.PayloadBytes, body.Hash, sourceEvidenceHash));
        }
        if (result.Count != 4 || result.Select(item => item.Role).Distinct(StringComparer.Ordinal).Count() != 4)
            throw new InvalidOperationException("p6n-free-schema-exact-four-pack-invariant-failed");
        return result;
    }

    private static object BuildManifest(string repo, IReadOnlyList<Item> items) => new
    {
        schemaVersion = "v5-p6n-unconstrained-output-ceiling-manifest-v1", status = "PREPARED_NOT_AUTHORIZED",
        preparedAtHead = GitHead(repo), providerCalls = 0, goldRead = false, goldMutation = "NONE", sharedRuntime = "UNCHANGED",
        supersedesAuthorization = "NONE; earlier structured P6N authorization does not apply to these changed bodies",
        hypothesis = "With the same P6M source evidence and packing, does Qwen's free semantic judgement outperform the nine-function ontology when both reasoning and response format are unconstrained?",
        route = new { gateway = "OpenRouter", model = "qwen/qwen3.7-flash", providerPin = "alibaba", temperature = 0, reasoning = new { enabled = true }, responseFormat = "OMITTED", streaming = true, usageInclude = true },
        protocol = V5FreeHeadingCandidateProtocolV1.UnschematizedVersion, outputSchema = "NONE; model may choose any response format/schema",
        parser = "NOT_RUN_BY_DESIGN", binder = "NOT_RUN_BY_DESIGN", semanticScore = "NOT_RUN", maxResponseUtf8Bytes = ResponseCap,
        executionGate = new { maximumProviderCalls = 4, exactlyOneAttemptPerPack = true, retry = 0, repair = false, fallback = false, postFilter = false, full31 = false, goldRead = false, parsing = false, binding = false },
        sourceAuthority = new { manifest = $"{P6LRoot}/execution-manifest.v1.json", sourceCohort = $"{P6IRoot}/audit.v1.json", sourcePackCount = 4, packing = "same frozen P05 parent packs", locatorDirectoryVisible = true, locatorOutputRequired = false, sourceEvidenceUnchanged = true },
        intentionalDeltas = new[] { "semantic prompt", "reasoning envelope: enabled=true, effort omitted", "response_format omitted; model-chosen output schema" },
        rows = items.Select(item => new
        {
            role = item.Role, documentId = item.DocumentId, parentOrdinal = item.ParentOrdinal, packId = item.Pack.PackId,
            ownedAtoms = item.Pack.OwnedAliases.Count, semanticRequestHash = item.Request.UserMessageSha256,
            sourceEvidenceHash = item.SourceEvidenceHash, registryFingerprint = item.Registry.Fingerprint,
            providerRequestHash = item.BodyHash, providerRequestBytes = item.Body.Length, maxCompletionTokens = item.Pack.MaxCompletionTokens,
        }).ToArray(),
    };

    private static bool ValidateManifest(string path, IReadOnlyList<Item> items)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.GetProperty("status").GetString() != "PREPARED_NOT_AUTHORIZED" || root.GetProperty("providerCalls").GetInt32() != 0 ||
                root.GetProperty("goldRead").GetBoolean() || root.GetProperty("protocol").GetString() != V5FreeHeadingCandidateProtocolV1.UnschematizedVersion ||
                root.GetProperty("outputSchema").GetString()?.StartsWith("NONE", StringComparison.Ordinal) != true ||
                root.GetProperty("route").GetProperty("responseFormat").GetString() != "OMITTED" ||
                root.GetProperty("route").GetProperty("reasoning").GetProperty("enabled").GetBoolean() != true ||
                root.GetProperty("route").GetProperty("reasoning").TryGetProperty("effort", out _) ||
                root.GetProperty("rows").GetArrayLength() != 4 || root.GetProperty("executionGate").GetProperty("maximumProviderCalls").GetInt32() != 4 ||
                root.GetProperty("executionGate").GetProperty("retry").GetInt32() != 0 || root.GetProperty("executionGate").GetProperty("parsing").GetBoolean() ||
                root.GetProperty("executionGate").GetProperty("binding").GetBoolean()) return false;
            foreach (var item in items)
            {
                var row = root.GetProperty("rows").EnumerateArray().Single(node => node.GetProperty("role").GetString() == item.Role);
                if (row.GetProperty("providerRequestHash").GetString() != item.BodyHash || row.GetProperty("semanticRequestHash").GetString() != item.Request.UserMessageSha256 ||
                    row.GetProperty("sourceEvidenceHash").GetString() != item.SourceEvidenceHash || row.GetProperty("registryFingerprint").GetString() != item.Registry.Fingerprint ||
                    row.GetProperty("providerRequestBytes").GetInt32() != item.Body.Length || row.GetProperty("maxCompletionTokens").GetInt32() != item.Pack.MaxCompletionTokens) return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException or KeyNotFoundException) { return false; }
    }

    private static object Checkpoint(IReadOnlyList<object> rows) => new
    {
        schemaVersion = "v5-p6n-unconstrained-output-checkpoint-v1", providerCalls = rows.Count, maximumProviderCalls = 4,
        retryCount = 0, repair = false, fallback = false, postFilter = false, goldRead = false,
        parser = "NOT_RUN", binder = "NOT_RUN", semanticScore = "NOT_RUN", rows,
    };

    private static string SourceEvidenceProjection(string message)
    {
        using var document = JsonDocument.Parse(message);
        var root = document.RootElement;
        return JsonSerializer.Serialize(new { ownedSubjects = root.GetProperty("ownedSubjects"), contextOnlyEvidence = root.GetProperty("contextOnlyEvidence") },
            new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }
    private static int? ReadUsage(JsonElement? usage, params string[] path)
    {
        if (usage is not { ValueKind: JsonValueKind.Object } current) return null;
        foreach (var segment in path) if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current)) return null;
        return current.ValueKind == JsonValueKind.Number && current.TryGetInt32(out var value) ? value : null;
    }
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static void Print(IEnumerable<Item> items) { foreach (var item in items) Console.WriteLine($"{item.Role} {item.DocumentId}/PACK_{item.ParentOrdinal:000} body={item.Body.Length} B hash={item.BodyHash}"); }
    private static void Write(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine, new UTF8Encoding(false));
    private static int Fail(string message) { Console.Error.WriteLine(message); return 1; }
    private static string? GitHead(string repo) { try { using var process = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD") { WorkingDirectory = repo, RedirectStandardOutput = true, UseShellExecute = false }); var output = process!.StandardOutput.ReadToEnd().Trim(); process.WaitForExit(); return process.ExitCode == 0 ? output : null; } catch { return null; } }
}
