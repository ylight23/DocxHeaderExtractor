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

/// <summary>Four-call P6I canary. Qualification-only: no Gold, retries, repair, or fallback.</summary>
internal static class P6KCompactSparseCanary
{
    private const string ConfirmSentinel = "yes-i-authorize-p6k-compact-sparse-canary-4-calls";
    private const string Root = "artifacts/v5-p6k-compact-sparse-canary";
    private const string P6LRoot = "artifacts/v5-p6l-canonical-locator-contract";
    private const string P6LConfirmSentinel = "yes-i-authorize-p6l-canonical-locator-canary-4-calls";
    private const string P6IRoot = "artifacts/v5-p6i-compact-locator-directory";
    private static readonly (string Role, string Doc, string Pdf, int Ordinal)[] Roles =
    [
        ("MAX_REQUEST_BODY", "SRC-089", "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf", 4),
        ("L1472_OWNER_OMISSION", "SRC-095", "todo10_8/heading_corpus_100/07_system_generated/095_RFC9114_HTTP_3.pdf", 17),
        ("L1710_RETYPING", "SRC-095", "todo10_8/heading_corpus_100/07_system_generated/095_RFC9114_HTTP_3.pdf", 20),
        ("MULTIPART_RELATION", "SRC-095", "todo10_8/heading_corpus_100/07_system_generated/095_RFC9114_HTTP_3.pdf", 11),
    ];

    private sealed record Item(string Role, string Doc, string Pdf, V5PackedDecisionRequestV3 Pack,
        V5SparseCandidateModelRequestV1 Request, RequestLocalLocatorRegistry Registry, byte[] Body, string Hash);

    public static async Task<int> RunAsync(string repo, string[] args)
        => await RunCoreAsync(repo, args, clarified: false);

    public static async Task<int> RunClarifiedAsync(string repo, string[] args)
        => await RunCoreAsync(repo, args, clarified: true);

    private static async Task<int> RunCoreAsync(string repo, string[] args, bool clarified)
    {
        var authorized = clarified
            ? args.Contains($"--confirm-p6l-canonical-locator-canary={P6LConfirmSentinel}")
            : args.Contains($"--confirm-p6k-compact-sparse-canary={ConfirmSentinel}");
        var selectedRoot = clarified ? P6LRoot : Root;
        var artifact = Path.Combine(repo, selectedRoot.Replace('/', Path.DirectorySeparatorChar));
        var manifestPath = Path.Combine(artifact, "execution-manifest.v1.json");
        var resultPath = Path.Combine(artifact, "result.v1.json");
        var checkpointPath = Path.Combine(artifact, "result.in-progress.v1.json");
        if (File.Exists(resultPath) || File.Exists(checkpointPath)) return Fail("p6k: result/checkpoint exists; stop before network");

        var p6iPath = Path.Combine(repo, P6IRoot.Replace('/', Path.DirectorySeparatorChar), "audit.v1.json");
        var p6i = JsonNode.Parse(File.ReadAllText(p6iPath))?.AsObject() ?? throw new InvalidOperationException("p6k:p6i-audit-invalid");
        if (p6i["providerCalls"]?.GetValue<int>() != 0 || p6i["goldRead"]?.GetValue<bool>() != false || p6i["protocol"]?.GetValue<string>() != V5SparseCandidateRequestComposerV1.CompactDirectoryVersion)
            return Fail("p6k: P6I authority artifact gate invalid");

        var contract = DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
        var envelope = new V5ProviderEnvelope("qwen/qwen3.7-flash", "alibaba", "none", true, "json_object", 300)
        { UsageInclude = true, OpenRouterResponseCacheDisabled = true };
        var items = Resolve(repo, p6i, contract, envelope, clarified);
        var expectedManifest = Manifest(items, envelope, repo);
        if (clarified)
        {
            if (!File.Exists(manifestPath)) return Fail("p6l: prepared manifest missing; run the provider-free P6L contract test first");
            if (!ValidateClarifiedManifest(manifestPath, items)) return Fail("p6l: frozen manifest parity failed; no network call");
        }
        if (!File.Exists(manifestPath))
        {
            if (authorized) return Fail("p6k: manifest was not frozen before authorization; run once without confirmation first");
            Directory.CreateDirectory(artifact);
            Write(manifestPath, expectedManifest);
            Console.WriteLine("p6k: prepared manifest written; providerCalls=0. Review manifest, then explicitly authorize exactly four calls.");
            PrintPlan(items);
            return 0;
        }

        var frozenManifest = JsonNode.Parse(File.ReadAllText(manifestPath))?.AsObject() ?? throw new InvalidOperationException("p6k:manifest-invalid");
        if (!clarified && !JsonNode.DeepEquals(frozenManifest, JsonSerializer.SerializeToNode(expectedManifest))) return Fail("p6k: current requests differ from frozen manifest; no network call");
        Console.WriteLine(clarified
            ? "p6l parity PASS: four clarified bodies reproduce the frozen P6L manifest and differ from P6K only in request contract fields."
            : "p6k parity PASS: four P6I compact bodies match frozen hashes byte-for-byte.");
        PrintPlan(items);
        if (!authorized) { Console.WriteLine($"{(clarified ? "p6l" : "p6k")}: providerCalls=0; explicit authorization required."); return 0; }
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY"))) return Fail("p6k: OPENROUTER_API_KEY is not set");

        var options = RemoteInferenceOptions.FromEnvironment();
        options.Model = envelope.Model;
        options.OpenRouterProviderRoute = envelope.Provider;
        options.OpenRouterReasoningEffort = envelope.Reasoning;
        options.TransientRequestRetries = 0;
        options.Validate();

        // Write before the first request. If execution is interrupted, a subsequent invocation
        // stops rather than risk double-sending a request whose outcome is uncertain.
        var rows = new List<object>();
        Write(checkpointPath, new { schemaVersion = "v5-p6k-canary-checkpoint-v1", providerCallsAlreadySent = 0, maximumCalls = 4, retry = 0, stopOnReentry = true, rows });
        foreach (var item in items)
        {
            OpenRouterExecutionObservation? observed = null;
            string? transportError = null;
            var watch = Stopwatch.StartNew();
            try
            {
                using var client = OpenRouterHeaderExtractor.CreateOwned(options);
                observed = await client.ExecuteObservedAsync(item.Body, item.Pack.MaxCompletionTokens,
                    item.Request.SystemPrompt, item.Request.UserMessage);
            }
            catch (Exception ex) { transportError = ex.Message; }
            watch.Stop();
            var analysis = Analyze(item, observed?.Content, observed?.FinishReason, transportError);
            rows.Add(new
            {
                role = item.Role, documentId = item.Doc, packId = item.Pack.PackId,
                semanticRequestHash = item.Request.UserMessageSha256, registryFingerprint = item.Registry.Fingerprint,
                providerRequestHash = item.Hash, providerRequestBytes = item.Body.Length,
                maxCompletionTokens = item.Pack.MaxCompletionTokens,
                transportError, httpAccepted = transportError is null, finishReason = observed?.FinishReason,
                usage = observed?.Usage, retryCount = observed?.RetryCount ?? 0,
                latencyMs = watch.Elapsed.TotalMilliseconds, sseEventCount = observed?.SseEventCount ?? 0,
                rawSseSha256 = observed is null ? null : Hash(observed.RawSse), rawSse = observed?.RawSse,
                rawResponseSha256 = observed is null ? null : Hash(observed.Content),
                rawResponseBytes = observed is null ? 0 : Encoding.UTF8.GetByteCount(observed.Content),
                rawResponse = observed?.Content, analysis,
            });
            Write(checkpointPath, new { schemaVersion = "v5-p6k-canary-checkpoint-v1", providerCallsAlreadySent = rows.Count, maximumCalls = 4, retry = 0, stopOnReentry = true, rows });
            var summary = JsonSerializer.SerializeToElement(analysis);
            Console.WriteLine($"[{item.Role}] {summary.GetProperty("classification").GetString()} inputTokens={ReadPromptTokens(observed?.Usage)?.ToString() ?? "n/a"} occurrences={summary.GetProperty("bound").GetInt32()} quarantined={summary.GetProperty("quarantined").GetInt32()}");
        }

        Write(resultPath, new
        {
            schemaVersion = clarified ? "v5-p6l-canonical-locator-canary-result-v1" : "v5-p6k-compact-sparse-canary-result-v1", head = GitHead(repo),
            sourceManifest = $"{selectedRoot}/execution-manifest.v1.json", providerCalls = rows.Count, maximumProviderCalls = 4,
            gateway = "OpenRouter", model = envelope.Model, providerPin = envelope.Provider,
            protocol = clarified ? V5SparseCandidateRequestComposerV1.CompactDirectoryCanonicalVersion : V5SparseCandidateRequestComposerV1.CompactDirectoryVersion,
            retryCount = 0, repair = false, fallback = false, goldRead = false, semanticScore = "NOT_RUN",
            sharedRuntime = "UNCHANGED", rows,
        });
        File.Delete(checkpointPath);
        Console.WriteLine($"{(clarified ? "p6l" : "p6k")} gate closed after at most four calls; no Gold or full-31 execution.");
        return 0;
    }

    private static List<Item> Resolve(string repo, JsonObject p6i, DocumentTaskContract contract, V5ProviderEnvelope envelope, bool clarified)
    {
        var cache = new Dictionary<string, (string Pdf, IReadOnlyList<V5PackedDecisionRequestV3> Packs, Dictionary<string, SemanticSourceAtom> Atoms)>(StringComparer.Ordinal);
        var items = new List<Item>();
        foreach (var role in Roles)
        {
            if (!cache.TryGetValue(role.Doc, out var source))
            {
                var pdf = Path.Combine(repo, role.Pdf.Replace('/', Path.DirectorySeparatorChar));
                source = (pdf,
                    V5PdfPreflightBuilder.BuildV3(pdf, role.Doc, contract, V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId, envelope),
                    V5PdfPreflightBuilder.LoadAtoms(pdf).ToDictionary(atom => atom.Alias, StringComparer.Ordinal));
                cache.Add(role.Doc, source);
            }
            var pack = source.Packs.Single(item => item.PackId == $"{V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId}:PACK_{role.Ordinal:000}");
            var ownedAtoms = pack.OwnedAliases.Select(alias => source.Atoms[alias]).ToArray();
            var registry = RequestLocalLocatorRegistry.Create(ownedAtoms);
            var request = clarified
                ? V5SparseCandidateRequestComposerV1.ComposeCompactDirectoryCanonical(contract, pack.Packet, registry)
                : V5SparseCandidateRequestComposerV1.ComposeCompactDirectory(contract, pack.Packet, registry);
            var body = OpenRouterQwen37JsonObjectCarrierV2_1.BuildFromRaw(request.SystemPrompt, request.UserMessage, pack.MaxCompletionTokens, envelope);
            var auditRow = p6i["rows"]?.AsArray().SingleOrDefault(node => node?["DocumentId"]?.GetValue<string>() == role.Doc && node["ParentOrdinal"]?.GetValue<int>() == role.Ordinal)
                ?? throw new InvalidOperationException($"p6k:P6I row missing {role.Doc}/{role.Ordinal}");
            if (!clarified && (auditRow["CompactProviderBodySha256"]?.GetValue<string>() != body.Hash || auditRow["CompactUserMessageSha256"]?.GetValue<string>() != request.UserMessageSha256 ||
                auditRow["CompactProviderBodyBytes"]?.GetValue<int>() != body.Bytes || auditRow["OwnedAtoms"]?.GetValue<int>() != pack.OwnedAliases.Count))
                throw new InvalidOperationException($"p6k:P6I parity failed {role.Role}");
            if (clarified)
            {
                var baselineRequest = V5SparseCandidateRequestComposerV1.ComposeCompactDirectory(contract, pack.Packet, registry);
                var baselineBody = OpenRouterQwen37JsonObjectCarrierV2_1.BuildFromRaw(baselineRequest.SystemPrompt, baselineRequest.UserMessage, pack.MaxCompletionTokens, envelope);
                var p6kPath = Path.Combine(repo, Root.Replace('/', Path.DirectorySeparatorChar), "result.v1.json");
                var p6k = JsonNode.Parse(File.ReadAllText(p6kPath))!.AsObject();
                var p6kRow = p6k["rows"]!.AsArray().Single(node => node!["role"]!.GetValue<string>() == role.Role)!;
                if (p6kRow["providerRequestHash"]?.GetValue<string>() != baselineBody.Hash || p6kRow["semanticRequestHash"]?.GetValue<string>() != baselineRequest.UserMessageSha256 ||
                    p6kRow["providerCalls"] is not null)
                    throw new InvalidOperationException($"p6l:P6K baseline parity failed {role.Role}");
            }
            items.Add(new(role.Role, role.Doc, source.Pdf, pack, request, registry, body.PayloadBytes, body.Hash));
        }
        return items;
    }

    private static object Manifest(IReadOnlyList<Item> items, V5ProviderEnvelope envelope, string repo) => new
    {
        schemaVersion = "v5-p6k-compact-sparse-canary-manifest-v1", status = "PREPARED_NOT_AUTHORIZED",
        preparedAtHead = GitHead(repo), providerCalls = 0, goldRead = false, protocol = V5SparseCandidateRequestComposerV1.CompactDirectoryVersion,
        route = new { gateway = "OpenRouter", model = envelope.Model, providerPin = envelope.Provider, temperature = 0, reasoning = envelope.Reasoning, responseFormat = envelope.ResponseFormat, streaming = true, usageInclude = true },
        executionGate = new { maximumProviderCalls = 4, retry = 0, repair = false, fallback = false, full31 = false, goldRead = false },
        rows = items.Select(item => new { role = item.Role, documentId = item.Doc, packId = item.Pack.PackId,
            ownedAtoms = item.Pack.OwnedAliases.Count, semanticRequestHash = item.Request.UserMessageSha256,
            registryFingerprint = item.Registry.Fingerprint, providerRequestHash = item.Hash, providerRequestBytes = item.Body.Length,
            maxCompletionTokens = item.Pack.MaxCompletionTokens, maxResponseUtf8Bytes = 49152 }).ToArray(),
    };

    private static bool ValidateClarifiedManifest(string path, IReadOnlyList<Item> items)
    {
        try
        {
            var manifest = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            if (manifest["status"]?.GetValue<string>() != "PREPARED_NOT_AUTHORIZED" ||
                manifest["providerCalls"]?.GetValue<int>() != 0 || manifest["goldRead"]?.GetValue<bool>() != false ||
                manifest["protocol"]?.GetValue<string>() != V5SparseCandidateRequestComposerV1.CompactDirectoryCanonicalVersion ||
                manifest["executionGate"]?["maximumProviderCalls"]?.GetValue<int>() != 4 || manifest["executionGate"]?["retry"]?.GetValue<int>() != 0)
                return false;
            var rows = manifest["rows"]!.AsArray();
            if (rows.Count != 4) return false;
            foreach (var item in items)
            {
                var row = rows.Single(node => node!["role"]!.GetValue<string>() == item.Role)!;
                if (row["providerRequestHash"]?.GetValue<string>() != item.Hash ||
                    row["semanticRequestHash"]?.GetValue<string>() != item.Request.UserMessageSha256 ||
                    row["providerRequestBytes"]?.GetValue<int>() != item.Body.Length ||
                    row["registryFingerprint"]?.GetValue<string>() != item.Registry.Fingerprint)
                    return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException) { return false; }
    }

    private static object Analyze(Item item, string? raw, string? finish, string? transportError)
    {
        if (transportError is not null || raw is null) return new { classification = "TRANSPORT_ERROR", parserAccepted = false, binderExecuted = false, bound = 0, quarantined = 0, emptyFunctions = 0, rawClaims = 0, error = transportError };
        var rawBytes = Encoding.UTF8.GetByteCount(raw);
        if (string.Equals(finish, "length", StringComparison.OrdinalIgnoreCase) || rawBytes > 49152)
            return new { classification = "RESPONSE_OVERFLOW", parserAccepted = false, binderExecuted = false, bound = 0, quarantined = 0, emptyFunctions = 0, rawClaims = 0, error = rawBytes > 49152 ? "response-byte-cap-exceeded" : "finish-reason-length" };
        try
        {
            using var document = JsonDocument.Parse(raw);
            var owned = (IReadOnlySet<int>)Enumerable.Range(0, item.Registry.AtomCount).ToHashSet();
            var parsed = item.Registry.Parse(document.RootElement, rawBytes, 49152, owned);
            var bound = 0;
            foreach (var occurrence in parsed.Response.Occurrences) { _ = item.Registry.Decode(occurrence); bound++; }
            var emptyFunctions = parsed.Response.Occurrences.Count(occurrence => occurrence.Functions.Count == 0);
            var classification = parsed.Quarantined.Count > 0 ? "PARSER_ACCEPTED_WITH_QUARANTINE" : bound > 0 ? "PARSER_AND_BINDER_VALID" : "VALID_EMPTY_PROPOSAL";
            return new { classification, parserAccepted = true, binderExecuted = true, bound, quarantined = parsed.Quarantined.Count, emptyFunctions, rawClaims = document.RootElement.TryGetProperty("occurrences", out var occurrences) && occurrences.ValueKind == JsonValueKind.Array ? occurrences.GetArrayLength() : 0, canonicalResponseBytes = parsed.CanonicalUtf8Bytes, quarantines = parsed.Quarantined };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        { return new { classification = "PARSER_REJECTED", parserAccepted = false, binderExecuted = false, bound = 0, quarantined = 0, emptyFunctions = 0, rawClaims = 0, error = ex.Message }; }
    }

    private static int? ReadPromptTokens(JsonElement? usage) => usage is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty("prompt_tokens", out var tokens) && tokens.TryGetInt32(out var count) ? count : null;
    private static void PrintPlan(IReadOnlyList<Item> items) { foreach (var item in items) Console.WriteLine($"  {item.Role}: {item.Doc} PACK_{item.Pack.PackId.Split("PACK_").Last()} owned={item.Pack.OwnedAliases.Count} body={item.Body.Length}B max_tokens={item.Pack.MaxCompletionTokens} sha256={item.Hash}"); }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string Hash(string text) => Hash(Encoding.UTF8.GetBytes(text));
    private static void Write(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine, new UTF8Encoding(false));
    private static int Fail(string message) { Console.Error.WriteLine(message); return 1; }
    private static string? GitHead(string repo) { try { using var process = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD") { WorkingDirectory = repo, RedirectStandardOutput = true, UseShellExecute = false }); var output = process!.StandardOutput.ReadToEnd().Trim(); process.WaitForExit(); return process.ExitCode == 0 ? output : null; } catch { return null; } }
}
