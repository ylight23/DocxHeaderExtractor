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

/// <summary>Full-31 P6N-B execution: free semantic judgement, enabled reasoning, unchanged P05 evidence and bound locator.</summary>
internal static class P6NBBoundLocatorFull31
{
    private const string ConfirmSentinel = "yes-i-authorize-p6nb-full31-primary-calls-no-retry";
    private const string Root = "artifacts/v5-p6nb-full31-reasoning-lane";
    private const string P6IRoot = "artifacts/v5-p6i-compact-locator-directory";
    private const string P6MRoot = "artifacts/v5-p6m-p6l-full31-qualification";
    private const string P6NBFourRoot = "artifacts/v5-p6nb-free-semantic-bound-locator";
    private const string RetryArtifact = "pack-007-authorized-repeat.v1.json";
    private const string Src089 = "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf";
    private const string Src095 = "todo10_8/heading_corpus_100/07_system_generated/095_RFC9114_HTTP_3.pdf";
    private const int ResponseCap = 49_152;
    private static readonly DocumentTaskContract Contract = DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
    private static readonly V5ProviderEnvelope Envelope = new("qwen/qwen3.7-flash", "alibaba", "none", true, "json_object", 300)
    { UsageInclude = true, OpenRouterResponseCacheDisabled = true };

    private sealed record Item(string DocumentId, int ParentOrdinal, string Pdf, V5PackedDecisionRequestV3 Pack,
        V5FreeHeadingRequestV1 Request, RequestLocalLocatorRegistry Registry, byte[] Body, string BodyHash, string SourceEvidenceHash);

    public static async Task<int> RunAsync(string repo, string[] args)
    {
        var authorized = args.Contains($"--confirm-p6nb-full31={ConfirmSentinel}");
        var directory = Path.Combine(repo, Root.Replace('/', Path.DirectorySeparatorChar));
        var manifestPath = Path.Combine(directory, "execution-manifest.v1.json");
        var resultPath = Path.Combine(directory, "result.v1.json");
        var checkpointPath = Path.Combine(directory, "result.in-progress.v1.json");
        if (File.Exists(resultPath) || File.Exists(checkpointPath))
            return Fail("p6n-b-full31: result/checkpoint exists; stop before network");

        var p6i = JsonNode.Parse(File.ReadAllText(Path.Combine(repo, P6IRoot, "audit.v1.json")))!.AsObject();
        var p6mManifest = JsonNode.Parse(File.ReadAllText(Path.Combine(repo, P6MRoot, "execution-manifest.v1.json")))!.AsObject();
        var p6nbFour = JsonNode.Parse(File.ReadAllText(Path.Combine(repo, P6NBFourRoot, "execution-manifest.v1.json")))!.AsObject();
        var items = BuildItems(repo, p6i, p6mManifest, p6nbFour);
        if (items.Count != 31 || items.Sum(item => item.Pack.OwnedAliases.Count) != 2884)
            return Fail("p6n-b-full31: expected frozen 31-pack/2884-owned source universe");

        if (!File.Exists(manifestPath))
        {
            Directory.CreateDirectory(directory);
            Write(manifestPath, BuildManifest(repo, items));
            Console.WriteLine("P6N-B full31 manifest frozen provider-free: 31 bodies, 2,884 owned atoms; ProviderCalls=0, GoldRead=false.");
            PrintSummary(items);
            return 0;
        }
        if (!ValidateManifest(manifestPath, items)) return Fail("p6n-b-full31: frozen request/body/lineage parity failed; no network call");
        PrintSummary(items);
        Console.WriteLine("P6N-B full31 exact body parity PASS. Manifest preserves reasoning.enabled=true, effort omitted, ontology off, canonical locator on.");
        if (!authorized) { Console.WriteLine("ProviderCalls=0; full31 execution requires the separately specified authorization."); return 0; }
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")))
            return Fail("p6n-b-full31: OPENROUTER_API_KEY is not set");

        var options = RemoteInferenceOptions.FromEnvironment();
        options.Model = Envelope.Model;
        options.OpenRouterProviderRoute = Envelope.Provider;
        options.OpenRouterReasoningEffort = "none"; // The frozen provider body enables reasoning and intentionally omits effort.
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
                using var client = OpenRouterQualificationTransport.CreateOwned(options);
                observation = await client.ExecuteObservedAsync(item.Body, item.Pack.MaxCompletionTokens,
                    item.Request.SystemPrompt, item.Request.UserMessage);
            }
            catch (Exception exception) { transportError = exception.Message; }
            watch.Stop();
            var analysis = Analyze(item, observation, transportError);
            rows.Add(new
            {
                documentId = item.DocumentId, parentOrdinal = item.ParentOrdinal, packId = item.Pack.PackId,
                ownedAtoms = item.Pack.OwnedAliases.Count, semanticRequestHash = item.Request.UserMessageSha256,
                sourceEvidenceHash = item.SourceEvidenceHash, registryFingerprint = item.Registry.Fingerprint,
                providerRequestHash = item.BodyHash, providerRequestBytes = item.Body.Length,
                maxCompletionTokens = item.Pack.MaxCompletionTokens,
                transportAccepted = observation is not null, transportError, finishReason = observation?.FinishReason,
                usage = observation?.Usage, retryCount = observation?.RetryCount ?? 0,
                latencyMs = watch.Elapsed.TotalMilliseconds, sseEventCount = observation?.SseEventCount ?? 0,
                rawSseSha256 = observation is null ? null : Hash(observation.RawSse), rawSse = observation?.RawSse,
                rawResponseSha256 = observation is null ? null : Hash(observation.Content),
                rawResponseBytes = observation is null ? 0 : Encoding.UTF8.GetByteCount(observation.Content),
                rawResponse = observation?.Content, analysis,
            });
            Write(checkpointPath, Checkpoint(rows));
            var summary = JsonSerializer.SerializeToElement(analysis);
            Console.WriteLine($"[{item.DocumentId} PACK_{item.ParentOrdinal:000}] {summary.GetProperty("classification").GetString()} call={rows.Count}/31 prompt={ReadPromptTokens(observation?.Usage)?.ToString() ?? "n/a"} headings={summary.GetProperty("rawHeadings").GetInt32()} bound={summary.GetProperty("boundHeadings").GetInt32()} quarantine={summary.GetProperty("quarantinedHeadings").GetInt32()} ms={watch.ElapsedMilliseconds}");
        }

        Write(resultPath, new
        {
            schemaVersion = "v5-p6nb-full31-reasoning-lane-result-v1", head = GitHead(repo),
            sourceManifest = $"{Root}/execution-manifest.v1.json", providerCalls = rows.Count,
            maximumAuthorizedPrimaryCalls = 31, completedPrimaryAttempts = rows.Count,
            route = new { gateway = "OpenRouter", model = Envelope.Model, providerPin = Envelope.Provider, temperature = 0, reasoning = new { enabled = true } },
            reasoningEffort = "OMITTED", ontologyPrompt = false, locatorSchema = V5FreeHeadingCandidateProtocolV1.BoundLocatorVersion,
            responseFormat = Envelope.ResponseFormat, maxResponseUtf8Bytes = ResponseCap,
            retry = 0, repair = false, fallback = false, goldRead = false, goldMutation = "NONE",
            semanticScore = "NOT_RUN", productionPromotion = false, sharedRuntime = "UNCHANGED",
            stopGate = "CLOSED_AFTER_31_PRIMARY_ATTEMPTS", rows,
        });
        File.Delete(checkpointPath);
        Console.WriteLine("P6N-B full31 gate closed after one primary attempt per pack; no retry, repair, fallback, Gold, or production promotion.");
        return 0;
    }

    public static async Task<int> RetryPack007Async(string repo, string[] args)
    {
        const string retrySentinel = "--confirm-p6nb-full31-pack007-single-retry=yes-i-authorize-one-exact-repeat";
        var directory = Path.Combine(repo, Root.Replace('/', Path.DirectorySeparatorChar));
        var manifestPath = Path.Combine(directory, "execution-manifest.v1.json");
        var resultPath = Path.Combine(directory, "result.v1.json");
        var freezePath = Path.Combine(directory, "response-hash-freeze.v1.json");
        var retryPath = Path.Combine(directory, RetryArtifact);
        if (!File.Exists(manifestPath) || !File.Exists(resultPath) || !File.Exists(freezePath))
            return Fail("p6n-b-pack007-retry: manifest/result/hash-freeze required; no network call");
        if (File.Exists(retryPath)) return Fail("p6n-b-pack007-retry: retry artifact exists; stop before network");

        var p6i = JsonNode.Parse(File.ReadAllText(Path.Combine(repo, P6IRoot, "audit.v1.json")))!.AsObject();
        var p6mManifest = JsonNode.Parse(File.ReadAllText(Path.Combine(repo, P6MRoot, "execution-manifest.v1.json")))!.AsObject();
        var p6nbFour = JsonNode.Parse(File.ReadAllText(Path.Combine(repo, P6NBFourRoot, "execution-manifest.v1.json")))!.AsObject();
        var items = BuildItems(repo, p6i, p6mManifest, p6nbFour);
        if (!ValidateManifest(manifestPath, items)) return Fail("p6n-b-pack007-retry: frozen full31 request parity failed; no network call");

        using var resultDoc = JsonDocument.Parse(File.ReadAllText(resultPath));
        using var freezeDoc = JsonDocument.Parse(File.ReadAllText(freezePath));
        var result = resultDoc.RootElement;
        if (result.GetProperty("providerCalls").GetInt32() != 31 || result.GetProperty("retry").GetInt32() != 0 ||
            result.GetProperty("goldRead").GetBoolean() || result.GetProperty("repair").GetBoolean() || result.GetProperty("fallback").GetBoolean() ||
            result.GetProperty("productionPromotion").GetBoolean() || freezeDoc.RootElement.GetProperty("goldRead").GetBoolean() ||
            freezeDoc.RootElement.GetProperty("retry").GetInt32() != 0)
            return Fail("p6n-b-pack007-retry: original cohort authority invalid; no network call");

        var item = items.Single(candidate => candidate.DocumentId == "SRC-089" && candidate.ParentOrdinal == 7);
        var prior = result.GetProperty("rows").EnumerateArray().Single(row => row.GetProperty("documentId").GetString() == item.DocumentId &&
            row.GetProperty("parentOrdinal").GetInt32() == item.ParentOrdinal);
        var frozen = freezeDoc.RootElement.GetProperty("rows").EnumerateArray().Single(row => row.GetProperty("documentId").GetString() == item.DocumentId &&
            row.GetProperty("parentOrdinal").GetInt32() == item.ParentOrdinal);
        if (prior.GetProperty("finishReason").GetString() != "length" || prior.GetProperty("retryCount").GetInt32() != 0 ||
            prior.GetProperty("analysis").GetProperty("classification").GetString() != "RESPONSE_OVERFLOW" ||
            frozen.GetProperty("providerRequestSha256").GetString() != item.BodyHash ||
            frozen.GetProperty("rawSseSha256").GetString() != prior.GetProperty("rawSseSha256").GetString() ||
            frozen.GetProperty("rawResponseSha256").GetString() != prior.GetProperty("rawResponseSha256").GetString())
            return Fail("p6n-b-pack007-retry: failed pack/hash freeze mismatch; no network call");

        Console.WriteLine($"P6N-B exact PACK_007 retry preflight PASS; providerRequestHash={item.BodyHash}; retry artifact is new and isolated.");
        if (!args.Contains(retrySentinel)) { Console.WriteLine("ProviderCalls=0; this single retry requires its dedicated authorization flag."); return 0; }
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")))
            return Fail("p6n-b-pack007-retry: OPENROUTER_API_KEY is not set");

        var options = RemoteInferenceOptions.FromEnvironment();
        options.Model = Envelope.Model;
        options.OpenRouterProviderRoute = Envelope.Provider;
        options.OpenRouterReasoningEffort = "none";
        options.TransientRequestRetries = 0;
        options.Validate();
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
        Write(retryPath, new
        {
            schemaVersion = "v5-p6nb-full31-pack007-authorized-repeat-v1",
            sourceManifest = $"{Root}/execution-manifest.v1.json", sourceResult = $"{Root}/result.v1.json",
            sourceHashFreeze = $"{Root}/response-hash-freeze.v1.json", providerCalls = 1,
            authorization = "ONE_EXACT_REPEAT_OF_SRC-089_PACK_007_ONLY", goldRead = false, goldMutation = "NONE",
            repair = false, fallback = false, productionPromotion = false,
            treatmentUnchanged = true, providerRequestHash = item.BodyHash,
            documentId = item.DocumentId, parentOrdinal = item.ParentOrdinal, packId = item.Pack.PackId,
            semanticRequestHash = item.Request.UserMessageSha256, registryFingerprint = item.Registry.Fingerprint,
            providerRequestBytes = item.Body.Length, maxCompletionTokens = item.Pack.MaxCompletionTokens,
            transportAccepted = observation is not null, transportError, finishReason = observation?.FinishReason,
            usage = observation?.Usage, retryCount = observation?.RetryCount ?? 0,
            latencyMs = watch.Elapsed.TotalMilliseconds, sseEventCount = observation?.SseEventCount ?? 0,
            rawSseSha256 = observation is null ? null : Hash(observation.RawSse), rawSse = observation?.RawSse,
            rawResponseSha256 = observation is null ? null : Hash(observation.Content),
            rawResponseBytes = observation is null ? 0 : Encoding.UTF8.GetByteCount(observation.Content),
            rawResponse = observation?.Content, analysis,
        });
        Console.WriteLine($"P6N-B PACK_007 authorized repeat persisted: {JsonSerializer.SerializeToElement(analysis).GetProperty("classification").GetString()}, retry=0, calls=1.");
        return 0;
    }

    private static List<Item> BuildItems(string repo, JsonObject p6i, JsonObject p6mManifest, JsonObject p6nbFour)
    {
        if (p6i["providerCalls"]?.GetValue<int>() != 0 || p6i["goldRead"]?.GetValue<bool>() != false ||
            p6i["rows"]?.AsArray().Count != 31 || p6i["rows"]!.AsArray().Sum(row => row!["OwnedAtoms"]!.GetValue<int>()) != 2884 ||
            p6mManifest["packCount"]?.GetValue<int>() != 31 || p6mManifest["ownedAtomTotal"]?.GetValue<int>() != 2884 ||
            p6nbFour["protocol"]?.GetValue<string>() != V5FreeHeadingCandidateProtocolV1.BoundLocatorVersion ||
            p6nbFour["route"]?["reasoning"]?["enabled"]?.GetValue<bool>() != true ||
            p6nbFour["route"]?["reasoning"]?.AsObject().ContainsKey("effort") != false)
            throw new InvalidOperationException("p6n-b-full31-source-authority-invalid");

        var p6mRows = p6mManifest["rows"]!.AsArray();
        var fourRows = p6nbFour["rows"]!.AsArray();
        var result = new List<Item>();
        foreach (var (documentId, relativePdf, expectedCount) in new[] { ("SRC-089", Src089, 7), ("SRC-095", Src095, 24) })
        {
            var pdf = Path.Combine(repo, relativePdf.Replace('/', Path.DirectorySeparatorChar));
            var packs = V5PdfPreflightBuilder.BuildV3(pdf, documentId, Contract,
                V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId, Envelope);
            if (packs.Count != expectedCount) throw new InvalidOperationException($"p6n-b-full31-pack-count:{documentId}:{packs.Count}");
            var atoms = V5PdfPreflightBuilder.LoadAtoms(pdf).ToDictionary(atom => atom.Alias, StringComparer.Ordinal);
            foreach (var (pack, ordinal) in packs.Select((pack, index) => (pack, index + 1)))
            {
                var registry = RequestLocalLocatorRegistry.Create(pack.OwnedAliases.Select(alias => atoms[alias]).ToArray());
                var canonical = V5SparseCandidateRequestComposerV1.ComposeCompactDirectoryCanonical(Contract, pack.Packet, registry);
                var request = V5FreeHeadingCandidateProtocolV1.ComposeBoundLocator(canonical);
                var body = V5FreeHeadingCandidateProtocolV1.BuildBoundLocatorProviderBody(request, pack.MaxCompletionTokens);
                var p6iRow = p6i["rows"]!.AsArray().Single(row =>
                    row!["DocumentId"]!.GetValue<string>() == documentId && row["ParentOrdinal"]!.GetValue<int>() == ordinal)!;
                if (p6iRow["OwnedAtoms"]?.GetValue<int>() != pack.OwnedAliases.Count)
                    throw new InvalidOperationException($"p6n-b-full31-p6i-owned-parity:{documentId}:{ordinal}");
                var p6mRow = p6mRows.Single(row => row!["documentId"]!.GetValue<string>() == documentId &&
                    row["parentOrdinal"]!.GetValue<int>() == ordinal)!;
                if (p6mRow["registryFingerprint"]?.GetValue<string>() != registry.Fingerprint ||
                    p6mRow["packId"]?.GetValue<string>() != pack.PackId ||
                    p6mRow["ownedAtoms"]?.GetValue<int>() != pack.OwnedAliases.Count)
                    throw new InvalidOperationException($"p6n-b-full31-p6m-source-lineage:{documentId}:{ordinal}");

                var fourRow = fourRows.SingleOrDefault(row => row?["documentId"]?.GetValue<string>() == documentId &&
                    row["parentOrdinal"]?.GetValue<int>() == ordinal);
                if (fourRow is not null && (fourRow["providerRequestHash"]?.GetValue<string>() != body.Hash ||
                    fourRow["semanticRequestHash"]?.GetValue<string>() != request.UserMessageSha256))
                    throw new InvalidOperationException($"p6n-b-full31-four-pack-treatment-parity:{documentId}:{ordinal}");

                result.Add(new Item(documentId, ordinal, relativePdf, pack, request, registry, body.PayloadBytes, body.Hash,
                    Hash(SourceEvidenceProjection(request.UserMessage))));
            }
        }
        return result;
    }

    private static object BuildManifest(string repo, IReadOnlyList<Item> items) => new
    {
        schemaVersion = "v5-p6nb-full31-reasoning-lane-manifest-v1", status = "PREPARED_NOT_AUTHORIZED",
        preparedAtHead = GitHead(repo), providerCalls = 0, goldRead = false, goldMutation = "NONE",
        treatment = new
        {
            model = "qwen/qwen3.7-flash", provider = "Alibaba", providerPin = "alibaba", temperature = 0,
            reasoningEnabled = true, reasoningEffort = "OMITTED", ontologyPrompt = false,
            locatorSchema = V5FreeHeadingCandidateProtocolV1.BoundLocatorVersion,
            packing = "same P05 resource-bounded 31 parent packs", sourceContext = "unchanged",
            parserBinder = "P6N-B sourceParts adapter + qualified P6L registry parser/binder, unchanged",
            repair = false, fallback = false, goldDuringRun = false, productionPromotion = false,
        },
        causalLimit = "Relative to P6M, ontology-off and reasoning-enabled change together; this cohort cannot isolate their individual causal effects.",
        sourceAuthority = new
        {
            packingAudit = $"{P6IRoot}/audit.v1.json", p6mParentManifest = $"{P6MRoot}/execution-manifest.v1.json",
            p6nbFourPackManifest = $"{P6NBFourRoot}/execution-manifest.v1.json",
            packCount = items.Count, ownedAtomTotal = items.Sum(item => item.Pack.OwnedAliases.Count),
            fourSharedPackBodiesMatch = true,
        },
        executionGate = new
        {
            maximumPrimaryProviderCalls = 31, exactlyOnePrimaryAttemptPerPack = true, retry = 0,
            repair = false, fallback = false, goldRead = false, goldMutation = "NONE",
            productionPromotion = false, persistRawSseAndResponse = true,
        },
        maxResponseUtf8Bytes = ResponseCap,
        route = new
        {
            gateway = "OpenRouter", model = "qwen/qwen3.7-flash", providerPin = "alibaba", temperature = 0,
            reasoning = new { enabled = true }, effort = "OMITTED", responseFormat = "json_object",
            streaming = true, usageInclude = true, fallbacks = false,
        },
        rows = items.Select(item => new
        {
            documentId = item.DocumentId, parentOrdinal = item.ParentOrdinal, packId = item.Pack.PackId,
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
            var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            if (root["schemaVersion"]?.GetValue<string>() != "v5-p6nb-full31-reasoning-lane-manifest-v1" ||
                root["status"]?.GetValue<string>() != "PREPARED_NOT_AUTHORIZED" || root["providerCalls"]?.GetValue<int>() != 0 ||
                root["goldRead"]?.GetValue<bool>() != false || root["rows"]?.AsArray().Count != 31 ||
                root["sourceAuthority"]?["ownedAtomTotal"]?.GetValue<int>() != 2884 ||
                root["treatment"]?["reasoningEnabled"]?.GetValue<bool>() != true ||
                root["treatment"]?["reasoningEffort"]?.GetValue<string>() != "OMITTED" ||
                root["treatment"]?["ontologyPrompt"]?.GetValue<bool>() != false ||
                root["executionGate"]?["maximumPrimaryProviderCalls"]?.GetValue<int>() != 31 ||
                root["executionGate"]?["retry"]?.GetValue<int>() != 0 ||
                root["executionGate"]?["goldRead"]?.GetValue<bool>() != false)
                return false;
            var rows = root["rows"]!.AsArray();
            foreach (var item in items)
            {
                var row = rows.Single(candidate => candidate!["documentId"]!.GetValue<string>() == item.DocumentId &&
                    candidate["parentOrdinal"]!.GetValue<int>() == item.ParentOrdinal)!;
                if (row["providerRequestHash"]?.GetValue<string>() != item.BodyHash ||
                    row["semanticRequestHash"]?.GetValue<string>() != item.Request.UserMessageSha256 ||
                    row["sourceEvidenceHash"]?.GetValue<string>() != item.SourceEvidenceHash ||
                    row["registryFingerprint"]?.GetValue<string>() != item.Registry.Fingerprint ||
                    row["providerRequestBytes"]?.GetValue<int>() != item.Body.Length ||
                    row["maxCompletionTokens"]?.GetValue<int>() != item.Pack.MaxCompletionTokens)
                    return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException or KeyNotFoundException)
        { return false; }
    }

    private static object Analyze(Item item, OpenRouterExecutionObservation? observation, string? error)
    {
        if (observation is null)
            return new { classification = "TRANSPORT_ERROR", parserAccepted = false, rawHeadings = 0,
                boundHeadings = 0, quarantinedHeadings = 0, additionalParts = 0, error };
        var bytes = Encoding.UTF8.GetByteCount(observation.Content);
        if (!string.Equals(observation.FinishReason, "stop", StringComparison.OrdinalIgnoreCase) || bytes > ResponseCap)
            return new { classification = observation.FinishReason?.Equals("length", StringComparison.OrdinalIgnoreCase) == true || bytes > ResponseCap
                    ? "RESPONSE_OVERFLOW" : "NON_TERMINAL_FINISH_REASON",
                parserAccepted = false, rawHeadings = 0, boundHeadings = 0, quarantinedHeadings = 0, additionalParts = 0, responseBytes = bytes };
        try
        {
            using var json = JsonDocument.Parse(observation.Content);
            var parsed = V5FreeHeadingCandidateProtocolV1.ParseAndBindSourceParts(json.RootElement, bytes, ResponseCap,
                item.Registry, Enumerable.Range(0, item.Registry.AtomCount).ToHashSet());
            return new
            {
                classification = parsed.Quarantined.Count == 0 ? "PARSER_BINDER_VALID" : "PARSER_ACCEPTED_WITH_QUARANTINE",
                parserAccepted = true, rawHeadings = json.RootElement.GetProperty("headings").GetArrayLength(),
                boundHeadings = parsed.Response.Occurrences.Count, quarantinedHeadings = parsed.Quarantined.Count,
                additionalParts = parsed.Response.Occurrences.Sum(occurrence => occurrence.AdditionalParts.Count),
                responseBytes = bytes, canonicalResponseBytes = parsed.CanonicalUtf8Bytes, quarantines = parsed.Quarantined,
            };
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        { return new { classification = "PARSER_REJECTED", parserAccepted = false, rawHeadings = 0, boundHeadings = 0, quarantinedHeadings = 0, additionalParts = 0, error = exception.Message }; }
    }

    private static object Checkpoint(IReadOnlyList<object> rows) => new
    {
        schemaVersion = "v5-p6nb-full31-checkpoint-v1", providerCallsAlreadySent = rows.Count,
        maximumPrimaryProviderCalls = 31, retry = 0, repair = false, fallback = false, goldRead = false, stopOnReentry = true, rows,
    };

    private static string SourceEvidenceProjection(string message)
    {
        using var json = JsonDocument.Parse(message);
        var root = json.RootElement;
        return JsonSerializer.Serialize(new { ownedSubjects = root.GetProperty("ownedSubjects"), contextOnlyEvidence = root.GetProperty("contextOnlyEvidence") },
            new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }
    private static int? ReadPromptTokens(JsonElement? usage) =>
        usage is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty("prompt_tokens", out var tokens) &&
        tokens.TryGetInt32(out var count) ? count : null;
    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static void PrintSummary(IReadOnlyList<Item> items) =>
        Console.WriteLine($"P6N-B full31: {items.Count} packs, {items.Sum(item => item.Pack.OwnedAliases.Count)} owned atoms, max body {items.Max(item => item.Body.Length)} B, reasoning=enabled/effort-omitted, ontology=off.");
    private static void Write(string path, object value) => File.WriteAllText(path,
        JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine, new UTF8Encoding(false));
    private static int Fail(string message) { Console.Error.WriteLine(message); return 1; }
    private static string? GitHead(string repo)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD")
                { WorkingDirectory = repo, RedirectStandardOutput = true, UseShellExecute = false });
            var output = process!.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return process.ExitCode == 0 ? output : null;
        }
        catch { return null; }
    }
}
