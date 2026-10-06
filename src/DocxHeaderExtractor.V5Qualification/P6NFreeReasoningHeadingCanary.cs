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

/// <summary>Provider-free prepare plus separately-authorized four-call P6N ceiling canary.</summary>
internal static class P6NFreeReasoningHeadingCanary
{
    private const string ConfirmSentinel = "yes-i-authorize-p6n-free-reasoning-canary-4-calls";
    private const string Root = "artifacts/v5-p6n-free-reasoning-heading-ceiling";
    private const string P6LRoot = "artifacts/v5-p6l-canonical-locator-contract";
    private const string P6IRoot = "artifacts/v5-p6i-compact-locator-directory";
    private const string Src089 = "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf";
    private const string Src095 = "todo10_8/heading_corpus_100/07_system_generated/095_RFC9114_HTTP_3.pdf";
    private const int ResponseCap = 49_152;

    private static readonly DocumentTaskContract TaskContract = DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
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
        var authorized = args.Contains($"--confirm-p6n-free-reasoning-canary={ConfirmSentinel}");
        var directory = Path.Combine(repo, Root.Replace('/', Path.DirectorySeparatorChar));
        var manifestPath = Path.Combine(directory, "execution-manifest.v1.json");
        var resultPath = Path.Combine(directory, "result.v1.json");
        var checkpointPath = Path.Combine(directory, "result.in-progress.v1.json");
        if (File.Exists(resultPath) || File.Exists(checkpointPath)) return Fail("p6n: result/checkpoint already exists; stop before network");

        var items = BuildItems(repo);
        var manifest = BuildManifest(repo, items);
        if (!File.Exists(manifestPath))
        {
            Directory.CreateDirectory(directory);
            Write(manifestPath, manifest);
            Console.WriteLine("P6N four-pack manifest prepared; ProviderCalls=0, GoldRead=false. A separate explicit authorization is required to execute.");
            Print(items);
            return 0;
        }
        if (!ValidateManifest(manifestPath, items)) return Fail("p6n: frozen request/provider body parity failed; no network call");
        Print(items);
        Console.WriteLine("P6N exact body parity PASS. ProviderCalls=0 unless separately authorized.");
        if (!authorized) return 0;
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY"))) return Fail("p6n: OPENROUTER_API_KEY is not set");

        var options = RemoteInferenceOptions.FromEnvironment();
        options.Model = Envelope.Model;
        options.OpenRouterProviderRoute = Envelope.Provider;
        options.OpenRouterReasoningEffort = "none"; // The frozen raw body carries reasoning.enabled=true instead.
        options.TransientRequestRetries = 0;
        options.Validate();

        var rows = new List<object>();
        Write(checkpointPath, new { schemaVersion = "v5-p6n-checkpoint-v1", providerCalls = 0, maximumProviderCalls = 4,
            retryCount = 0, repair = false, fallback = false, goldRead = false, semanticScore = "NOT_RUN", rows });
        foreach (var item in items)
        {
            OpenRouterExecutionObservation? observation = null;
            string? transportError = null;
            var watch = Stopwatch.StartNew();
            try
            {
                using var client = OpenRouterQualificationTransport.CreateOwned(options);
                observation = await client.ExecuteObservedAsync(item.Body, item.Pack.MaxCompletionTokens,
                    item.Request.SystemPrompt, item.Request.UserMessage);
            }
            catch (Exception exception) { transportError = exception.Message; }
            watch.Stop();
            var analysis = Analyze(item, observation, transportError);
            rows.Add(new
            {
                role = item.Role, documentId = item.DocumentId, parentOrdinal = item.ParentOrdinal, packId = item.Pack.PackId,
                semanticRequestHash = item.Request.UserMessageSha256, sourceEvidenceHash = item.SourceEvidenceHash,
                registryFingerprint = item.Registry.Fingerprint, providerRequestHash = item.BodyHash, providerRequestBytes = item.Body.Length,
                promptTokens = ReadUsage(observation?.Usage, "prompt_tokens"), completionTokens = ReadUsage(observation?.Usage, "completion_tokens"),
                reasoningTokens = ReadUsage(observation?.Usage, "completion_tokens_details", "reasoning_tokens"),
                finishReason = observation?.FinishReason, latencyMs = watch.Elapsed.TotalMilliseconds, responseBytes = observation is null ? 0 : Encoding.UTF8.GetByteCount(observation.Content),
                retryCount = observation?.RetryCount ?? 0, transportError, rawSseEventCount = observation?.SseEventCount ?? 0,
                rawSseSha256 = observation is null ? null : Hash(observation.RawSse), rawSse = observation?.RawSse,
                rawResponseSha256 = observation is null ? null : Hash(observation.Content), rawResponse = observation?.Content,
                analysis,
            });
            Write(checkpointPath, new { schemaVersion = "v5-p6n-checkpoint-v1", providerCalls = rows.Count, maximumProviderCalls = 4,
                retryCount = 0, repair = false, fallback = false, goldRead = false, semanticScore = "NOT_RUN", rows });
            Console.WriteLine($"[{item.Role}] calls={rows.Count}/4 finish={observation?.FinishReason ?? "error"} prompt={ReadUsage(observation?.Usage, "prompt_tokens")?.ToString() ?? "n/a"} reasoning={ReadUsage(observation?.Usage, "completion_tokens_details", "reasoning_tokens")?.ToString() ?? "n/a"} emitted={ReadCount(analysis, "rawHeadings")} bound={ReadCount(analysis, "boundHeadings")} quarantine={ReadCount(analysis, "quarantinedHeadings")}");
        }

        Write(resultPath, new
        {
            schemaVersion = "v5-p6n-free-reasoning-heading-ceiling-result-v1", head = GitHead(repo),
            sourceManifest = $"{Root}/execution-manifest.v1.json", providerCalls = rows.Count, maximumAuthorizedProviderCalls = 4,
            route = new { gateway = "OpenRouter", model = Envelope.Model, providerPin = Envelope.Provider, temperature = 0, reasoning = new { enabled = true } },
            protocol = V5FreeHeadingCandidateProtocolV1.Version, responseFormat = "json_object", maxResponseUtf8Bytes = ResponseCap,
            retries = 0, repair = false, fallback = false, postFilter = false, goldRead = false, semanticScore = "NOT_RUN",
            sharedRuntime = "UNCHANGED", stopGate = "CLOSED_AFTER_4_CALLS", rows,
        });
        File.Delete(checkpointPath);
        Console.WriteLine("P6N gate closed after at most four one-attempt calls; no Gold, scoring, repair, fallback, or post-filter.");
        return 0;
    }

    private static List<Item> BuildItems(string repo)
    {
        var p6l = JsonNode.Parse(File.ReadAllText(Path.Combine(repo, P6LRoot, "execution-manifest.v1.json")))!.AsObject();
        var p6i = JsonNode.Parse(File.ReadAllText(Path.Combine(repo, P6IRoot, "audit.v1.json")))!.AsObject();
        if (p6l["providerCalls"]?.GetValue<int>() != 0 || p6l["goldRead"]?.GetValue<bool>() != false ||
            p6l["executionGate"]?["maximumProviderCalls"]?.GetValue<int>() != 4 ||
            p6i["providerCalls"]?.GetValue<int>() != 0 || p6i["goldRead"]?.GetValue<bool>() != false)
            throw new InvalidOperationException("p6n-source-authority-gate-invalid");

        var result = new List<Item>();
        var packCache = new Dictionary<string, (IReadOnlyList<V5PackedDecisionRequestV3> Packs, Dictionary<string, SemanticSourceAtom> Atoms)>();
        foreach (var role in Roles)
        {
            if (!packCache.TryGetValue(role.DocumentId, out var source))
            {
                var pdf = Path.Combine(repo, role.Pdf.Replace('/', Path.DirectorySeparatorChar));
                var packs = V5PdfPreflightBuilder.BuildV3(pdf, role.DocumentId, TaskContract,
                    V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId, Envelope);
                source = (packs, V5PdfPreflightBuilder.LoadAtoms(pdf).ToDictionary(atom => atom.Alias, StringComparer.Ordinal));
                packCache.Add(role.DocumentId, source);
            }

            var pack = source.Packs.Single(candidate => candidate.PackId == $"{V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId}:PACK_{role.ParentOrdinal:000}");
            var registry = RequestLocalLocatorRegistry.Create(pack.OwnedAliases.Select(alias => source.Atoms[alias]).ToArray());
            var p6mBase = V5SparseCandidateRequestComposerV1.ComposeCompactDirectoryCanonical(TaskContract, pack.Packet, registry);
            var request = V5FreeHeadingCandidateProtocolV1.Compose(p6mBase);
            var sourceEvidenceHash = Hash(SourceEvidenceProjection(p6mBase.UserMessage));
            var body = V5FreeHeadingCandidateProtocolV1.BuildProviderBody(request, pack.MaxCompletionTokens);
            var p6lRow = p6l["rows"]!.AsArray().Single(row => row!["role"]!.GetValue<string>() == role.Role)!;
            if (p6lRow["registryFingerprint"]?.GetValue<string>() != registry.Fingerprint ||
                p6lRow["documentId"]?.GetValue<string>() != role.DocumentId ||
                p6lRow["packId"]?.GetValue<string>() != pack.PackId ||
                p6lRow["ownedAtoms"]?.GetValue<int>() != pack.OwnedAliases.Count)
                throw new InvalidOperationException($"p6n-p6l-source-lineage-mismatch:{role.Role}");
            result.Add(new(role.Role, role.DocumentId, role.ParentOrdinal, pack, request, registry, body.PayloadBytes, body.Hash, sourceEvidenceHash));
        }
        if (result.Count != 4 || result.Select(item => item.Role).Distinct(StringComparer.Ordinal).Count() != 4)
            throw new InvalidOperationException("p6n-four-pack-manifest-invariant-failed");
        return result;
    }

    private static object BuildManifest(string repo, IReadOnlyList<Item> items) => new
    {
        schemaVersion = "v5-p6n-free-reasoning-heading-ceiling-manifest-v1", status = "PREPARED_NOT_AUTHORIZED",
        preparedAtHead = GitHead(repo), providerCalls = 0, goldRead = false, goldMutation = "NONE", sharedRuntime = "UNCHANGED",
        hypothesis = "With the same source evidence, packing and canonical locator contract, can Qwen identify heading occurrences more accurately when semantic membership is left to its own reasoning rather than constrained by the current nine-function ontology?",
        route = new { gateway = "OpenRouter", model = Envelope.Model, providerPin = Envelope.Provider, temperature = 0, reasoning = new { enabled = true }, responseFormat = "json_object", streaming = true, usageInclude = true },
        protocol = V5FreeHeadingCandidateProtocolV1.Version, maxResponseUtf8Bytes = ResponseCap,
        protocolNotes = "P6L canonical locator contract; response parser/binder reuse through qualification-only adapter",
        executionGate = new { maximumProviderCalls = 4, exactlyOneAttemptPerPack = true, retry = 0, repair = false, fallback = false, postFilter = false, full31 = false, goldRead = false, semanticScore = "NOT_RUN" },
        sourceAuthority = new { manifest = $"{P6LRoot}/execution-manifest.v1.json", sourceCohort = $"{P6IRoot}/audit.v1.json", sourcePackCount = 4, locatorContract = "P6L/P6M canonical", sourceEvidenceUnchanged = true },
        rows = items.Select(item => new
        {
            role = item.Role, documentId = item.DocumentId, parentOrdinal = item.ParentOrdinal, packId = item.Pack.PackId,
            ownedAtoms = item.Pack.OwnedAliases.Count, semanticRequestHash = item.Request.UserMessageSha256,
            sourceEvidenceHash = item.SourceEvidenceHash, registryFingerprint = item.Registry.Fingerprint,
            providerRequestHash = item.BodyHash, providerRequestBytes = item.Body.Length,
            maxCompletionTokens = item.Pack.MaxCompletionTokens,
        }).ToArray(),
    };

    private static bool ValidateManifest(string path, IReadOnlyList<Item> items)
    {
        try
        {
            using var current = JsonDocument.Parse(File.ReadAllText(path));
            var root = current.RootElement;
            if (root.GetProperty("status").GetString() != "PREPARED_NOT_AUTHORIZED" || root.GetProperty("providerCalls").GetInt32() != 0 ||
                root.GetProperty("goldRead").GetBoolean() || root.GetProperty("protocol").GetString() != V5FreeHeadingCandidateProtocolV1.Version ||
                root.GetProperty("protocolNotes").GetString() != "P6L canonical locator contract; response parser/binder reuse through qualification-only adapter" ||
                root.GetProperty("rows").GetArrayLength() != 4 || root.GetProperty("maxResponseUtf8Bytes").GetInt32() != ResponseCap ||
                root.GetProperty("route").GetProperty("reasoning").GetProperty("enabled").GetBoolean() != true ||
                root.GetProperty("route").GetProperty("reasoning").TryGetProperty("effort", out _) ||
                root.GetProperty("executionGate").GetProperty("maximumProviderCalls").GetInt32() != 4 ||
                root.GetProperty("executionGate").GetProperty("retry").GetInt32() != 0 ||
                root.GetProperty("executionGate").GetProperty("repair").GetBoolean() ||
                root.GetProperty("executionGate").GetProperty("fallback").GetBoolean() ||
                root.GetProperty("executionGate").GetProperty("postFilter").GetBoolean() ||
                root.GetProperty("executionGate").GetProperty("full31").GetBoolean() ||
                root.GetProperty("executionGate").GetProperty("semanticScore").GetString() != "NOT_RUN")
                return false;
            foreach (var item in items)
            {
                var row = root.GetProperty("rows").EnumerateArray().Single(node => node.GetProperty("role").GetString() == item.Role);
                if (row.GetProperty("providerRequestHash").GetString() != item.BodyHash ||
                    row.GetProperty("semanticRequestHash").GetString() != item.Request.UserMessageSha256 ||
                    row.GetProperty("sourceEvidenceHash").GetString() != item.SourceEvidenceHash ||
                    row.GetProperty("registryFingerprint").GetString() != item.Registry.Fingerprint ||
                    row.GetProperty("providerRequestBytes").GetInt32() != item.Body.Length) return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException or KeyNotFoundException) { return false; }
    }

    private static object Analyze(Item item, OpenRouterExecutionObservation? observation, string? transportError)
    {
        if (transportError is not null || observation is null)
            return new { classification = "TRANSPORT_ERROR", parserAccepted = false, rawHeadings = 0, boundHeadings = 0, quarantinedHeadings = 0, error = transportError };
        var bytes = Encoding.UTF8.GetByteCount(observation.Content);
        if (observation.FinishReason != "stop" || bytes > ResponseCap)
            return new { classification = observation.FinishReason == "length" || bytes > ResponseCap ? "RESPONSE_OVERFLOW" : "NON_TERMINAL_FINISH_REASON",
                parserAccepted = false, rawHeadings = 0, boundHeadings = 0, quarantinedHeadings = 0, responseBytes = bytes };
        try
        {
            using var document = JsonDocument.Parse(observation.Content);
            var parsed = V5FreeHeadingCandidateProtocolV1.ParseAndBind(document.RootElement, bytes, ResponseCap, item.Registry,
                Enumerable.Range(0, item.Registry.AtomCount).ToHashSet());
            return new { classification = parsed.Quarantined.Count == 0 ? "PARSER_BINDER_VALID" : "PARSER_ACCEPTED_WITH_QUARANTINE",
                parserAccepted = true, rawHeadings = document.RootElement.GetProperty("headings").GetArrayLength(),
                boundHeadings = parsed.Response.Occurrences.Count, quarantinedHeadings = parsed.Quarantined.Count,
                canonicalResponseBytes = parsed.CanonicalUtf8Bytes, quarantines = parsed.Quarantined };
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        { return new { classification = "PARSER_REJECTED", parserAccepted = false, rawHeadings = 0, boundHeadings = 0, quarantinedHeadings = 0, error = exception.Message }; }
    }

    private static string SourceEvidenceProjection(string p6mUserMessage)
    {
        using var document = JsonDocument.Parse(p6mUserMessage);
        var root = document.RootElement;
        return JsonSerializer.Serialize(new
        {
            ownedSubjects = root.GetProperty("ownedSubjects"),
            contextOnlyEvidence = root.GetProperty("contextOnlyEvidence"),
        }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    private static int? ReadUsage(JsonElement? usage, params string[] path)
    {
        if (usage is not { ValueKind: JsonValueKind.Object } current) return null;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current)) return null;
        }
        return current.ValueKind == JsonValueKind.Number && current.TryGetInt32(out var value) ? value : null;
    }
    private static int ReadCount(object value, string property) => (int)(value.GetType().GetProperty(property)?.GetValue(value) ?? 0);
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static void Print(IEnumerable<Item> items) { foreach (var item in items) Console.WriteLine($"{item.Role} {item.DocumentId}/PACK_{item.ParentOrdinal:000} body={item.Body.Length} B requestHash={item.BodyHash}"); }
    private static void Write(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine, new UTF8Encoding(false));
    private static int Fail(string message) { Console.Error.WriteLine(message); return 1; }
    private static string? GitHead(string repo) { try { using var process = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD") { WorkingDirectory = repo, RedirectStandardOutput = true, UseShellExecute = false }); var output = process!.StandardOutput.ReadToEnd().Trim(); process.WaitForExit(); return process.ExitCode == 0 ? output : null; } catch { return null; } }
}
