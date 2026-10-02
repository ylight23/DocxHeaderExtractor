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

/// <summary>Provider-free prepare plus separately-authorized P6N-B four-pack free-semantic bound-locator canary.</summary>
internal static class P6NBBoundLocatorCanary
{
    private const string ConfirmSentinel = "yes-i-authorize-p6nb-bound-locator-canary-4-calls";
    private const string RetryConfirmSentinel = "yes-i-authorize-p6nb-call4-two-retries";
    private const string Root = "artifacts/v5-p6nb-free-semantic-bound-locator";
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
        var authorized = args.Contains($"--confirm-p6nb-bound-locator={ConfirmSentinel}");
        var directory = Path.Combine(repo, Root.Replace('/', Path.DirectorySeparatorChar));
        var manifestPath = Path.Combine(directory, "execution-manifest.v1.json");
        var resultPath = Path.Combine(directory, "result.v1.json");
        var checkpointPath = Path.Combine(directory, "result.in-progress.v1.json");
        if (File.Exists(resultPath) || File.Exists(checkpointPath)) return Fail("p6n-b: result/checkpoint already exists; stop before network");
        var items = BuildItems(repo);
        if (!File.Exists(manifestPath))
        {
            Directory.CreateDirectory(directory);
            Write(manifestPath, BuildManifest(repo, items));
            Console.WriteLine("P6N-B four-pack manifest prepared; ProviderCalls=0, GoldRead=false. Separate authorization required.");
            Print(items);
            return 0;
        }
        if (!ValidateManifest(manifestPath, items)) return Fail("p6n-b: frozen request/body parity failed; no network call");
        Print(items);
        Console.WriteLine("P6N-B exact body parity PASS. ProviderCalls=0 unless separately authorized.");
        if (!authorized) return 0;
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY"))) return Fail("p6n-b: OPENROUTER_API_KEY is not set");

        var options = RemoteInferenceOptions.FromEnvironment();
        options.Model = Envelope.Model; options.OpenRouterProviderRoute = Envelope.Provider;
        options.OpenRouterReasoningEffort = "none"; // The frozen body carries enabled=true and omits effort.
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
                finishReason = observation?.FinishReason, latencyMs = watch.Elapsed.TotalMilliseconds,
                responseBytes = observation is null ? 0 : Encoding.UTF8.GetByteCount(observation.Content),
                retryCount = observation?.RetryCount ?? 0, transportError, rawSseEventCount = observation?.SseEventCount ?? 0,
                rawSseSha256 = observation is null ? null : Hash(observation.RawSse), rawSse = observation?.RawSse,
                rawResponseSha256 = observation is null ? null : Hash(observation.Content), rawResponse = observation?.Content,
                analysis,
            });
            Write(checkpointPath, Checkpoint(rows));
            Console.WriteLine($"[{item.Role}] calls={rows.Count}/4 finish={observation?.FinishReason ?? "error"} raw={ReadInt(analysis, "rawHeadings")} bound={ReadInt(analysis, "boundHeadings")} quarantine={ReadInt(analysis, "quarantinedHeadings")} prompt={ReadUsage(observation?.Usage, "prompt_tokens")?.ToString() ?? "n/a"} reasoning={ReadUsage(observation?.Usage, "completion_tokens_details", "reasoning_tokens")?.ToString() ?? "n/a"}");
        }

        Write(resultPath, new
        {
            schemaVersion = "v5-p6nb-free-semantic-bound-locator-result-v1", head = GitHead(repo),
            sourceManifest = $"{Root}/execution-manifest.v1.json", providerCalls = rows.Count, maximumAuthorizedProviderCalls = 4,
            route = new { gateway = "OpenRouter", model = "qwen/qwen3.7-flash", providerPin = "alibaba", temperature = 0, reasoning = new { enabled = true } },
            protocol = V5FreeHeadingCandidateProtocolV1.BoundLocatorVersion, responseFormat = "json_object",
            maxResponseUtf8Bytes = ResponseCap, retry = 0, repair = false, fallback = false, postFilter = false,
            goldRead = false, semanticScore = "NOT_RUN", sharedRuntime = "UNCHANGED", stopGate = "CLOSED_AFTER_4_CALLS", rows,
        });
        File.Delete(checkpointPath);
        Console.WriteLine("P6N-B gate closed after four one-attempt calls; no Gold, scoring, repair, fallback, or post-filter.");
        return 0;
    }

    /// <summary>Consumes a separately authorized maximum of two retries for only the frozen fourth request.</summary>
    public static async Task<int> RetryFourthAsync(string repo, string[] args)
    {
        if (!args.Contains($"--confirm-p6nb-call4-retry={RetryConfirmSentinel}"))
            return Fail("p6n-b retry: separate retry authorization missing; no network call");

        var directory = Path.Combine(repo, Root.Replace('/', Path.DirectorySeparatorChar));
        var manifestPath = Path.Combine(directory, "execution-manifest.v1.json");
        var resultPath = Path.Combine(directory, "result.v1.json");
        var checkpointPath = Path.Combine(directory, "call4-retry.in-progress.v1.json");
        if (File.Exists(checkpointPath)) return Fail("p6n-b retry: uncertain in-progress checkpoint exists; stop before network");
        if (!File.Exists(resultPath) || !File.Exists(manifestPath)) return Fail("p6n-b retry: frozen manifest or original result missing; no network call");

        var items = BuildItems(repo);
        if (!ValidateManifest(manifestPath, items)) return Fail("p6n-b retry: frozen request/body parity failed; no network call");
        var item = items.Single(candidate => candidate.Role == "MULTIPART_RELATION");
        var result = JsonNode.Parse(File.ReadAllText(resultPath))?.AsObject();
        if (result is null || result["schemaVersion"]?.GetValue<string>() != "v5-p6nb-free-semantic-bound-locator-result-v1" ||
            result["goldRead"]?.GetValue<bool>() != false || result["semanticScore"]?.GetValue<string>() != "NOT_RUN" ||
            result["retry"]?.GetValue<int>() != 0 || result["repair"]?.GetValue<bool>() != false ||
            result["fallback"]?.GetValue<bool>() != false || result["providerCalls"]?.GetValue<int>() is < 4 or > 5)
            return Fail("p6n-b retry: result gate/state invalid; no network call");

        var rows = result["rows"]?.AsArray();
        if (rows is null || rows.Count != 4) return Fail("p6n-b retry: expected exactly four original rows; no network call");
        var row = rows.SingleOrDefault(candidate => candidate?["role"]?.GetValue<string>() == item.Role)?.AsObject();
        if (row is null || row["providerRequestHash"]?.GetValue<string>() != item.BodyHash ||
            row["semanticRequestHash"]?.GetValue<string>() != item.Request.UserMessageSha256 ||
            row["finishReason"] is not null || row["retryCount"]?.GetValue<int>() != 0 || row["transportError"]?.GetValue<string>()?.Contains("429", StringComparison.Ordinal) != true)
            return Fail("p6n-b retry: fourth row is not the frozen initial 429 failure; no network call");
        if (rows.Where(candidate => candidate?["role"]?.GetValue<string>() != item.Role)
            .Any(candidate => candidate?["analysis"]?["classification"]?.GetValue<string>() != "PARSER_BINDER_VALID"))
            return Fail("p6n-b retry: original successful rows changed; no network call");

        var retryAttempts = row["retryAttempts"]?.AsArray() ?? new JsonArray();
        if (retryAttempts.Count >= 2 || result["providerCalls"]!.GetValue<int>() != 4 + retryAttempts.Count)
            return Fail("p6n-b retry: two retry limit already consumed or call accounting invalid; no network call");
        if (row["retryAttempts"] is null) row["retryAttempts"] = retryAttempts;
        result["initialCanaryAttempts"] = 4;
        result["authorizedAdditionalAttempts"] = 2;
        result["authorizedRetryTarget"] = new JsonObject { ["role"] = item.Role, ["providerRequestHash"] = item.BodyHash };

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")))
            return Fail("p6n-b retry: OPENROUTER_API_KEY is not set; no network call");
        var options = RemoteInferenceOptions.FromEnvironment();
        options.Model = Envelope.Model; options.OpenRouterProviderRoute = Envelope.Provider;
        options.OpenRouterReasoningEffort = "none"; // Exact body has enabled=true and omits effort.
        options.TransientRequestRetries = 0;
        options.Validate();

        while (retryAttempts.Count < 2)
        {
            var attemptNumber = retryAttempts.Count + 1;
            Write(checkpointPath, new
            {
                schemaVersion = "v5-p6nb-call4-retry-checkpoint-v1", state = "CALL_ABOUT_TO_BE_SENT",
                targetRole = item.Role, providerRequestHash = item.BodyHash, additionalAttempt = attemptNumber,
                maximumAdditionalAttempts = 2, providerCallsBeforeAttempt = result["providerCalls"]!.GetValue<int>(),
                retry = 0, repair = false, fallback = false, goldRead = false,
            });

            OpenRouterExecutionObservation? observation = null;
            string? transportError = null;
            var watch = Stopwatch.StartNew();
            try
            {
                using var client = OpenRouterHeaderExtractor.CreateOwned(options);
                observation = await client.ExecuteObservedAsync(item.Body, item.Pack.MaxCompletionTokens,
                    item.Request.SystemPrompt, item.Request.UserMessage);
            }
            catch (Exception exception) { transportError = exception.Message; }
            watch.Stop();

            var analysis = Analyze(item, observation, transportError);
            retryAttempts.Add(JsonSerializer.SerializeToNode(new
            {
                additionalAttempt = attemptNumber, providerRequestHash = item.BodyHash, providerRequestBytes = item.Body.Length,
                promptTokens = ReadUsage(observation?.Usage, "prompt_tokens"), completionTokens = ReadUsage(observation?.Usage, "completion_tokens"),
                reasoningTokens = ReadUsage(observation?.Usage, "completion_tokens_details", "reasoning_tokens"),
                finishReason = observation?.FinishReason, latencyMs = watch.Elapsed.TotalMilliseconds,
                responseBytes = observation is null ? 0 : Encoding.UTF8.GetByteCount(observation.Content),
                retryCount = observation?.RetryCount ?? 0, transportError, rawSseEventCount = observation?.SseEventCount ?? 0,
                rawSseSha256 = observation is null ? null : Hash(observation.RawSse), rawSse = observation?.RawSse,
                rawResponseSha256 = observation is null ? null : Hash(observation.Content), rawResponse = observation?.Content, analysis,
            }));
            result["providerCalls"] = 4 + retryAttempts.Count;
            result["additionalRetryAttempts"] = retryAttempts.Count;
            result["retry"] = 0;
            result["repair"] = false;
            result["fallback"] = false;
            result["goldRead"] = false;
            result["semanticScore"] = "NOT_RUN";
            result["stopGate"] = retryAttempts.Count == 2 ? "CLOSED_AFTER_TWO_AUTHORIZED_CALL4_RETRIES" : "CLOSED_AFTER_AUTHORIZED_CALL4_RETRY_BUDGET";
            Write(resultPath, result);
            File.Delete(checkpointPath);
            Console.WriteLine($"[MULTIPART_RELATION retry {attemptNumber}/2] calls={result["providerCalls"]}/6 finish={observation?.FinishReason ?? "error"} classification={ReadString(analysis, "classification")} prompt={ReadUsage(observation?.Usage, "prompt_tokens")?.ToString() ?? "n/a"} retryCount={observation?.RetryCount ?? 0}");
        }

        Console.WriteLine("P6N-B call-4 retry authorization consumed: exactly two additional attempts maximum; Gold/scoring/repair/fallback unchanged.");
        return 0;
    }

    private static List<Item> BuildItems(string repo)
    {
        var p6l = JsonNode.Parse(File.ReadAllText(Path.Combine(repo, P6LRoot, "execution-manifest.v1.json")))!.AsObject();
        var p6i = JsonNode.Parse(File.ReadAllText(Path.Combine(repo, P6IRoot, "audit.v1.json")))!.AsObject();
        if (p6l["providerCalls"]?.GetValue<int>() != 0 || p6l["goldRead"]?.GetValue<bool>() != false ||
            p6l["executionGate"]?["maximumProviderCalls"]?.GetValue<int>() != 4 ||
            p6i["providerCalls"]?.GetValue<int>() != 0 || p6i["goldRead"]?.GetValue<bool>() != false)
            throw new InvalidOperationException("p6n-b-source-authority-gate-invalid");
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
            var request = V5FreeHeadingCandidateProtocolV1.ComposeBoundLocator(canonical);
            var sourceEvidenceHash = Hash(SourceEvidenceProjection(request.UserMessage));
            var body = V5FreeHeadingCandidateProtocolV1.BuildBoundLocatorProviderBody(request, pack.MaxCompletionTokens);
            var sourceRow = p6l["rows"]!.AsArray().Single(row => row!["role"]!.GetValue<string>() == role.Role)!;
            if (sourceRow["registryFingerprint"]?.GetValue<string>() != registry.Fingerprint ||
                sourceRow["documentId"]?.GetValue<string>() != role.DocumentId ||
                sourceRow["packId"]?.GetValue<string>() != pack.PackId ||
                sourceRow["ownedAtoms"]?.GetValue<int>() != pack.OwnedAliases.Count)
                throw new InvalidOperationException($"p6n-b-source-lineage-mismatch:{role.Role}");
            result.Add(new(role.Role, role.DocumentId, role.ParentOrdinal, pack, request, registry, body.PayloadBytes, body.Hash, sourceEvidenceHash));
        }
        if (result.Count != 4 || result.Select(item => item.Role).Distinct(StringComparer.Ordinal).Count() != 4)
            throw new InvalidOperationException("p6n-b-exact-four-pack-invariant-failed");
        return result;
    }

    private static object BuildManifest(string repo, IReadOnlyList<Item> items) => new
    {
        schemaVersion = "v5-p6nb-free-semantic-bound-locator-manifest-v1", status = "PREPARED_NOT_AUTHORIZED",
        preparedAtHead = GitHead(repo), providerCalls = 0, goldRead = false, goldMutation = "NONE", sharedRuntime = "UNCHANGED",
        hypothesis = "Holding P05 source evidence, owned/halo visibility, canonical locator, and response carrier constant, does free semantic judgement with enabled reasoning differ from ontology-constrained reasoning=none P6M? The comparison changes both ontology and reasoning and cannot identify their separate causal effects.",
        route = new { gateway = "OpenRouter", model = "qwen/qwen3.7-flash", providerPin = "alibaba", temperature = 0, reasoning = new { enabled = true }, responseFormat = "json_object", streaming = true, usageInclude = true },
        protocol = V5FreeHeadingCandidateProtocolV1.BoundLocatorVersion,
        outputShape = new { root = "headings[]", heading = "sourceParts[]", part = "canonical P6L locator part: atom plus optional from/to strict substring handles" },
        prohibitedOutputFields = new[] { "semanticFunction", "isHeading", "type", "reason", "confidence", "hierarchy" },
        parser = "P6N-B sourceParts adapter, then qualified P6L locator parser", binder = "P6L RequestLocalLocatorRegistry.Decode / SemanticSourcePartBinder",
        maxResponseUtf8Bytes = ResponseCap,
        executionGate = new { maximumProviderCalls = 4, exactlyOneAttemptPerPack = true, retry = 0, repair = false, fallback = false, postFilter = false, full31 = false, goldRead = false, semanticScore = "NOT_RUN" },
        sourceAuthority = new { manifest = $"{P6LRoot}/execution-manifest.v1.json", sourceCohort = $"{P6IRoot}/audit.v1.json", sourcePackCount = 4, packing = "same frozen P05 parent packs", locatorDirectoryVisible = true, sourceEvidenceUnchanged = true },
        causalLimit = "P6M vs P6N-B changes ontology and reasoning together; result estimates only their combined arm difference.",
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
                root.GetProperty("goldRead").GetBoolean() || root.GetProperty("protocol").GetString() != V5FreeHeadingCandidateProtocolV1.BoundLocatorVersion ||
                root.GetProperty("rows").GetArrayLength() != 4 || root.GetProperty("maxResponseUtf8Bytes").GetInt32() != ResponseCap ||
                root.GetProperty("route").GetProperty("reasoning").GetProperty("enabled").GetBoolean() != true ||
                root.GetProperty("route").GetProperty("reasoning").TryGetProperty("effort", out _) ||
                root.GetProperty("route").GetProperty("responseFormat").GetString() != "json_object" ||
                root.GetProperty("executionGate").GetProperty("maximumProviderCalls").GetInt32() != 4 ||
                root.GetProperty("executionGate").GetProperty("retry").GetInt32() != 0 || root.GetProperty("executionGate").GetProperty("full31").GetBoolean()) return false;
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

    private static object Analyze(Item item, OpenRouterExecutionObservation? observation, string? transportError)
    {
        if (transportError is not null || observation is null)
            return new { classification = "TRANSPORT_ERROR", parserAccepted = false, rawHeadings = 0, boundHeadings = 0, quarantinedHeadings = 0, error = transportError };
        var bytes = Encoding.UTF8.GetByteCount(observation.Content);
        if (!string.Equals(observation.FinishReason, "stop", StringComparison.OrdinalIgnoreCase) || bytes > ResponseCap)
            return new { classification = observation.FinishReason?.Equals("length", StringComparison.OrdinalIgnoreCase) == true || bytes > ResponseCap ? "RESPONSE_OVERFLOW" : "NON_TERMINAL_FINISH_REASON",
                parserAccepted = false, rawHeadings = 0, boundHeadings = 0, quarantinedHeadings = 0, responseBytes = bytes };
        try
        {
            using var response = JsonDocument.Parse(observation.Content);
            var parsed = V5FreeHeadingCandidateProtocolV1.ParseAndBindSourceParts(response.RootElement, bytes, ResponseCap,
                item.Registry, Enumerable.Range(0, item.Registry.AtomCount).ToHashSet());
            return new
            {
                classification = parsed.Quarantined.Count == 0 ? "PARSER_BINDER_VALID" : "PARSER_ACCEPTED_WITH_QUARANTINE",
                parserAccepted = true, rawHeadings = response.RootElement.GetProperty("headings").GetArrayLength(),
                boundHeadings = parsed.Response.Occurrences.Count, quarantinedHeadings = parsed.Quarantined.Count,
                additionalParts = parsed.Response.Occurrences.Sum(occurrence => occurrence.AdditionalParts.Count),
                canonicalResponseBytes = parsed.CanonicalUtf8Bytes, quarantines = parsed.Quarantined,
            };
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        { return new { classification = "PARSER_REJECTED", parserAccepted = false, rawHeadings = 0, boundHeadings = 0, quarantinedHeadings = 0, error = exception.Message }; }
    }

    private static object Checkpoint(IReadOnlyList<object> rows) => new
    { schemaVersion = "v5-p6nb-checkpoint-v1", providerCalls = rows.Count, maximumProviderCalls = 4, retry = 0, repair = false, fallback = false, goldRead = false, rows };
    private static string SourceEvidenceProjection(string message)
    {
        using var document = JsonDocument.Parse(message); var root = document.RootElement;
        return JsonSerializer.Serialize(new { ownedSubjects = root.GetProperty("ownedSubjects"), contextOnlyEvidence = root.GetProperty("contextOnlyEvidence") },
            new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }
    private static int? ReadUsage(JsonElement? usage, params string[] path)
    {
        if (usage is not { ValueKind: JsonValueKind.Object } current) return null;
        foreach (var segment in path) if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current)) return null;
        return current.ValueKind == JsonValueKind.Number && current.TryGetInt32(out var value) ? value : null;
    }
    private static int ReadInt(object value, string property) => (int)(value.GetType().GetProperty(property)?.GetValue(value) ?? 0);
    private static string? ReadString(object value, string property) => value.GetType().GetProperty(property)?.GetValue(value) as string;
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static void Print(IEnumerable<Item> items) { foreach (var item in items) Console.WriteLine($"{item.Role} {item.DocumentId}/PACK_{item.ParentOrdinal:000} body={item.Body.Length} B hash={item.BodyHash}"); }
    private static void Write(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine, new UTF8Encoding(false));
    private static int Fail(string message) { Console.Error.WriteLine(message); return 1; }
    private static string? GitHead(string repo) { try { using var process = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD") { WorkingDirectory = repo, RedirectStandardOutput = true, UseShellExecute = false }); var output = process!.StandardOutput.ReadToEnd().Trim(); process.WaitForExit(); return process.ExitCode == 0 ? output : null; } catch { return null; } }
}
