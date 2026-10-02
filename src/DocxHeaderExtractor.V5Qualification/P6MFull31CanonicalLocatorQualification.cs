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

/// <summary>Executes exactly one P6L clarified request per frozen P6I parent pack; no split/retry/Gold.</summary>
internal static class P6MFull31CanonicalLocatorQualification
{
    private const string ConfirmSentinel = "yes-i-authorize-p6m-full31-one-call-per-parent-pack";
    private const string Root = "artifacts/v5-p6m-p6l-full31-qualification";
    private const string P6IRoot = "artifacts/v5-p6i-compact-locator-directory";
    private const string Src089 = "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf";
    private const string Src095 = "todo10_8/heading_corpus_100/07_system_generated/095_RFC9114_HTTP_3.pdf";
    private const int ResponseCap = 49_152;
    private static readonly DocumentTaskContract Contract = DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
    private static readonly V5ProviderEnvelope Envelope = new("qwen/qwen3.7-flash", "alibaba", "none", true, "json_object", 300)
    { UsageInclude = true, OpenRouterResponseCacheDisabled = true };

    private sealed record Item(string DocumentId, int ParentOrdinal, string Pdf, V5PackedDecisionRequestV3 Pack,
        V5SparseCandidateModelRequestV1 Request, RequestLocalLocatorRegistry Registry, byte[] Body, string BodyHash);

    public static async Task<int> RunAsync(string repo, string[] args)
    {
        var authorized = args.Contains($"--confirm-p6m-full31={ConfirmSentinel}");
        var artifact = Path.Combine(repo, Root.Replace('/', Path.DirectorySeparatorChar));
        var manifestPath = Path.Combine(artifact, "execution-manifest.v1.json");
        var resultPath = Path.Combine(artifact, "result.v1.json");
        var checkpointPath = Path.Combine(artifact, "result.in-progress.v1.json");
        if (File.Exists(resultPath) || File.Exists(checkpointPath)) return Fail("p6m: result/checkpoint exists; stop before network");

        var p6i = JsonNode.Parse(File.ReadAllText(Path.Combine(repo, P6IRoot, "audit.v1.json")))!.AsObject();
        if (p6i["providerCalls"]?.GetValue<int>() != 0 || p6i["goldRead"]?.GetValue<bool>() != false ||
            p6i["rows"]?.AsArray().Count != 31 || p6i["rows"]!.AsArray().Sum(row => row!["OwnedAtoms"]!.GetValue<int>()) != 2884)
            return Fail("p6m: P6I 31-pack authority gate invalid");
        var items = BuildRequests(repo, p6i);
        if (items.Count != 31 || items.Sum(item => item.Pack.OwnedAliases.Count) != 2884) return Fail("p6m: full31 request count/ownership invariant failed");
        var manifest = BuildManifest(repo, items);

        if (!File.Exists(manifestPath))
        {
            Directory.CreateDirectory(artifact);
            Write(manifestPath, manifest);
            Console.WriteLine("p6m manifest prepared and frozen; providerCalls=0. Reinvoke with explicit P6M authorization to execute exactly 31 parent calls.");
            PrintSummary(items);
            return 0;
        }
        if (!ValidateManifest(manifestPath, items)) return Fail("p6m: frozen 31-body manifest parity failed; no network call");
        PrintSummary(items);
        Console.WriteLine("p6m parity PASS: all 31 clarified bodies reproduce manifest hashes and P6I source/context lineage.");
        if (!authorized) { Console.WriteLine("p6m: providerCalls=0; explicit 31-call authorization required."); return 0; }
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY"))) return Fail("p6m: OPENROUTER_API_KEY is not set");

        var options = RemoteInferenceOptions.FromEnvironment();
        options.Model = Envelope.Model; options.OpenRouterProviderRoute = Envelope.Provider;
        options.OpenRouterReasoningEffort = Envelope.Reasoning; options.TransientRequestRetries = 0; options.Validate();

        var rows = new List<object>();
        Write(checkpointPath, new { schemaVersion = "v5-p6m-full31-checkpoint-v1", providerCallsAlreadySent = 0, maximumProviderCalls = 31, retry = 0, repair = false, fallback = false, goldRead = false, stopOnReentry = true, rows });
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
            catch (Exception ex) { transportError = ex.Message; }
            watch.Stop();
            var analysis = Analyze(item, observation?.Content, observation?.FinishReason, transportError);
            rows.Add(new
            {
                documentId = item.DocumentId, parentOrdinal = item.ParentOrdinal, packId = item.Pack.PackId,
                ownedAtoms = item.Pack.OwnedAliases.Count, semanticRequestHash = item.Request.UserMessageSha256,
                registryFingerprint = item.Registry.Fingerprint, providerRequestHash = item.BodyHash,
                providerRequestBytes = item.Body.Length, maxCompletionTokens = item.Pack.MaxCompletionTokens,
                transportError, transportAccepted = observation is not null, finishReason = observation?.FinishReason,
                usage = observation?.Usage, retryCount = observation?.RetryCount ?? 0,
                latencyMs = watch.Elapsed.TotalMilliseconds, sseEventCount = observation?.SseEventCount ?? 0,
                rawSseSha256 = observation is null ? null : Hash(observation.RawSse), rawSse = observation?.RawSse,
                rawResponseSha256 = observation is null ? null : Hash(observation.Content),
                rawResponseBytes = observation is null ? 0 : Encoding.UTF8.GetByteCount(observation.Content),
                rawResponse = observation?.Content, analysis,
            });
            Write(checkpointPath, new { schemaVersion = "v5-p6m-full31-checkpoint-v1", providerCallsAlreadySent = rows.Count, maximumProviderCalls = 31, retry = 0, repair = false, fallback = false, goldRead = false, stopOnReentry = true, rows });
            var summary = JsonSerializer.SerializeToElement(analysis);
            Console.WriteLine($"[{item.DocumentId} PACK_{item.ParentOrdinal:000}] {summary.GetProperty("classification").GetString()} promptTokens={ReadPromptTokens(observation?.Usage)?.ToString() ?? "n/a"} raw={summary.GetProperty("rawOccurrences").GetInt32()} bound={summary.GetProperty("boundOccurrences").GetInt32()} quarantine={summary.GetProperty("quarantinedOccurrences").GetInt32()} elapsedMs={watch.ElapsedMilliseconds}");
        }

        Write(resultPath, new
        {
            schemaVersion = "v5-p6m-p6l-full31-result-v1", head = GitHead(repo),
            sourceManifest = $"{Root}/execution-manifest.v1.json", providerCalls = rows.Count, maximumProviderCalls = 31,
            route = new { gateway = "OpenRouter", model = Envelope.Model, providerPin = Envelope.Provider, temperature = 0, reasoning = Envelope.Reasoning },
            protocol = V5SparseCandidateRequestComposerV1.CompactDirectoryCanonicalVersion,
            retry = 0, repair = false, fallback = false, goldRead = false, semanticScore = "NOT_RUN",
            adaptiveSplit = "NOT_EXECUTED; parent response overflow classified without reissuing or splitting",
            sharedRuntime = "UNCHANGED", rows,
        });
        File.Delete(checkpointPath);
        Console.WriteLine("p6m gate closed after 31 parent attempts; no retries, splits, repairs, fallback, Gold, or semantic scoring.");
        return 0;
    }

    private static List<Item> BuildRequests(string repo, JsonObject p6i)
    {
        var result = new List<Item>();
        foreach (var (documentId, relativePdf, expectedCount) in new[] { ("SRC-089", Src089, 7), ("SRC-095", Src095, 24) })
        {
            var pdf = Path.Combine(repo, relativePdf.Replace('/', Path.DirectorySeparatorChar));
            var packs = V5PdfPreflightBuilder.BuildV3(pdf, documentId, Contract, V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId, Envelope);
            if (packs.Count != expectedCount) throw new InvalidOperationException($"p6m:pack-count:{documentId}:{packs.Count}");
            var atoms = V5PdfPreflightBuilder.LoadAtoms(pdf).ToDictionary(atom => atom.Alias, StringComparer.Ordinal);
            foreach (var (pack, index) in packs.Select((pack, index) => (pack, index + 1)))
            {
                var registry = RequestLocalLocatorRegistry.Create(pack.OwnedAliases.Select(alias => atoms[alias]).ToArray());
                var previous = V5SparseCandidateRequestComposerV1.ComposeCompactDirectory(Contract, pack.Packet, registry);
                var request = V5SparseCandidateRequestComposerV1.ComposeCompactDirectoryCanonical(Contract, pack.Packet, registry);
                AssertOnlyContractDelta(previous, request);
                var previousBody = OpenRouterQwen37JsonObjectCarrierV2_1.BuildFromRaw(previous.SystemPrompt, previous.UserMessage, pack.MaxCompletionTokens, Envelope);
                var body = OpenRouterQwen37JsonObjectCarrierV2_1.BuildFromRaw(request.SystemPrompt, request.UserMessage, pack.MaxCompletionTokens, Envelope);
                var p6iRow = p6i["rows"]!.AsArray().Single(row => row!["DocumentId"]!.GetValue<string>() == documentId && row["ParentOrdinal"]!.GetValue<int>() == index)!;
                if (p6iRow["CompactUserMessageSha256"]?.GetValue<string>() != previous.UserMessageSha256 ||
                    p6iRow["CompactProviderBodySha256"]?.GetValue<string>() != previousBody.Hash ||
                    p6iRow["CompactProviderBodyBytes"]?.GetValue<int>() != previousBody.Bytes ||
                    p6iRow["OwnedAtoms"]?.GetValue<int>() != pack.OwnedAliases.Count)
                    throw new InvalidOperationException($"p6m:P6I-lineage-parity:{documentId}:{index}");
                result.Add(new(documentId, index, pdf, pack, request, registry, body.PayloadBytes, body.Hash));
            }
        }
        return result;
    }

    private static void AssertOnlyContractDelta(V5SparseCandidateModelRequestV1 oldRequest, V5SparseCandidateModelRequestV1 newRequest)
    {
        var oldNode = JsonNode.Parse(oldRequest.UserMessage)!.AsObject();
        var newNode = JsonNode.Parse(newRequest.UserMessage)!.AsObject();
        oldNode.Remove("protocolVersion"); oldNode.Remove("responseContract");
        newNode.Remove("protocolVersion"); newNode.Remove("responseContract"); newNode.Remove("locatorCanonicalRule");
        if (!JsonNode.DeepEquals(oldNode, newNode)) throw new InvalidOperationException("p6m:non-contract-request-delta");
    }

    private static object BuildManifest(string repo, IReadOnlyList<Item> items) => new
    {
        schemaVersion = "v5-p6m-p6l-full31-manifest-v1", status = "PREPARED_NOT_AUTHORIZED",
        preparedAtHead = GitHead(repo), providerCalls = 0, goldRead = false, goldMutation = "NONE",
        protocol = V5SparseCandidateRequestComposerV1.CompactDirectoryCanonicalVersion,
        sourceManifest = $"{P6IRoot}/audit.v1.json", sourceP6IProtocol = V5SparseCandidateRequestComposerV1.CompactDirectoryVersion,
        route = new { gateway = "OpenRouter", model = Envelope.Model, providerPin = Envelope.Provider, temperature = 0, reasoning = Envelope.Reasoning, responseFormat = Envelope.ResponseFormat, streaming = true, usageInclude = true },
        executionGate = new { maximumProviderCalls = 31, oneAttemptPerParentPack = true, retry = 0, repair = false, fallback = false, adaptiveSplit = false, full31 = true, goldRead = false },
        packCount = items.Count, ownedAtomTotal = items.Sum(item => item.Pack.OwnedAliases.Count),
        maxResponseUtf8Bytes = ResponseCap,
        rows = items.Select(item => new { documentId = item.DocumentId, parentOrdinal = item.ParentOrdinal, packId = item.Pack.PackId,
            ownedAtoms = item.Pack.OwnedAliases.Count, semanticRequestHash = item.Request.UserMessageSha256,
            registryFingerprint = item.Registry.Fingerprint, providerRequestHash = item.BodyHash,
            providerRequestBytes = item.Body.Length, maxCompletionTokens = item.Pack.MaxCompletionTokens }).ToArray(),
    };

    private static bool ValidateManifest(string path, IReadOnlyList<Item> items)
    {
        try
        {
            var manifest = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            if (manifest["status"]?.GetValue<string>() != "PREPARED_NOT_AUTHORIZED" || manifest["providerCalls"]?.GetValue<int>() != 0 ||
                manifest["goldRead"]?.GetValue<bool>() != false || manifest["protocol"]?.GetValue<string>() != V5SparseCandidateRequestComposerV1.CompactDirectoryCanonicalVersion ||
                manifest["packCount"]?.GetValue<int>() != 31 || manifest["ownedAtomTotal"]?.GetValue<int>() != 2884 ||
                manifest["executionGate"]?["maximumProviderCalls"]?.GetValue<int>() != 31 || manifest["executionGate"]?["retry"]?.GetValue<int>() != 0)
                return false;
            var rows = manifest["rows"]!.AsArray();
            if (rows.Count != 31) return false;
            foreach (var item in items)
            {
                var row = rows.Single(node => node!["documentId"]!.GetValue<string>() == item.DocumentId && node["parentOrdinal"]!.GetValue<int>() == item.ParentOrdinal)!;
                if (row["providerRequestHash"]?.GetValue<string>() != item.BodyHash || row["semanticRequestHash"]?.GetValue<string>() != item.Request.UserMessageSha256 ||
                    row["registryFingerprint"]?.GetValue<string>() != item.Registry.Fingerprint || row["providerRequestBytes"]?.GetValue<int>() != item.Body.Length)
                    return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException) { return false; }
    }

    private static object Analyze(Item item, string? raw, string? finish, string? transportError)
    {
        if (transportError is not null || raw is null) return new { classification = "TRANSPORT_ERROR", parserAccepted = false, rawOccurrences = 0, boundOccurrences = 0, quarantinedOccurrences = 0, wholeAtomParts = 0, strictSubstringParts = 0, fullSpanPairs = 0, additionalParts = 0, functions = Array.Empty<string>(), error = transportError };
        var rawBytes = Encoding.UTF8.GetByteCount(raw);
        if (string.Equals(finish, "length", StringComparison.OrdinalIgnoreCase) || rawBytes > ResponseCap)
            return new { classification = "RESPONSE_OVERFLOW", parserAccepted = false, rawOccurrences = 0, boundOccurrences = 0, quarantinedOccurrences = 0, wholeAtomParts = 0, strictSubstringParts = 0, fullSpanPairs = 0, additionalParts = 0, functions = Array.Empty<string>(), error = rawBytes > ResponseCap ? "response-byte-cap-exceeded" : "finish-reason-length", splitWouldBeNeeded = true, splitExecuted = false };
        JsonElement[] rawOccurrences = [];
        var whole = 0; var strict = 0; var fullSpan = 0; var additional = 0; var functions = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            rawOccurrences = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("occurrences", out var array) && array.ValueKind == JsonValueKind.Array ? array.EnumerateArray().ToArray() : [];
            foreach (var occurrence in rawOccurrences)
            {
                foreach (var (part, isAdditional) in Parts(occurrence))
                {
                    if (isAdditional) additional++;
                    if (!part.TryGetProperty("from", out var fromValue) || !part.TryGetProperty("to", out var toValue)) { whole++; continue; }
                    strict++;
                    var atomHandle = part.TryGetProperty("atom", out var atomValue) ? atomValue.GetString() : null;
                    var atomIndex = item.Registry.Atoms.Select((atom, index) => (atom, index)).FirstOrDefault(pair => item.Registry.AtomHandle(pair.index) == atomHandle).index;
                    var text = item.Registry.Atoms[atomIndex].Text;
                    if (item.Registry.TryBoundary(fromValue.GetString() ?? "", out var fromAtom, out var from) &&
                        item.Registry.TryBoundary(toValue.GetString() ?? "", out var toAtom, out var to) && fromAtom == atomIndex && toAtom == atomIndex && from == 0 && to == text.Length) fullSpan++;
                }
                if (occurrence.ValueKind == JsonValueKind.Object && occurrence.TryGetProperty("functions", out var funcArray) && funcArray.ValueKind == JsonValueKind.Array)
                    foreach (var function in funcArray.EnumerateArray()) if (function.ValueKind == JsonValueKind.String) functions.Add(function.GetString()!);
            }
            var owned = (IReadOnlySet<int>)Enumerable.Range(0, item.Registry.AtomCount).ToHashSet();
            var parsed = item.Registry.Parse(root, rawBytes, ResponseCap, owned);
            foreach (var occurrence in parsed.Response.Occurrences) _ = item.Registry.Decode(occurrence);
            var classification = parsed.Quarantined.Count > 0 ? "PARSER_ACCEPTED_WITH_QUARANTINE" : "PARSER_AND_BINDER_VALID";
            return new { classification, parserAccepted = true, rawOccurrences = rawOccurrences.Length, boundOccurrences = parsed.Response.Occurrences.Count,
                quarantinedOccurrences = parsed.Quarantined.Count, wholeAtomParts = whole, strictSubstringParts = strict, fullSpanPairs = fullSpan,
                additionalParts = additional, functions = functions.OrderBy(value => value, StringComparer.Ordinal).ToArray(), canonicalResponseBytes = parsed.CanonicalUtf8Bytes,
                quarantines = parsed.Quarantined };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        { return new { classification = "PARSER_REJECTED", parserAccepted = false, rawOccurrences, boundOccurrences = 0, quarantinedOccurrences = 0, wholeAtomParts = whole, strictSubstringParts = strict, fullSpanPairs = fullSpan, additionalParts = additional, functions = functions.OrderBy(value => value, StringComparer.Ordinal).ToArray(), error = ex.Message }; }

        static IEnumerable<(JsonElement Part, bool Additional)> Parts(JsonElement occurrence)
        {
            if (occurrence.ValueKind != JsonValueKind.Object) yield break;
            if (occurrence.TryGetProperty("primary", out var primary) && primary.ValueKind == JsonValueKind.Object) yield return (primary, false);
            if (occurrence.TryGetProperty("additionalParts", out var extras) && extras.ValueKind == JsonValueKind.Array)
                foreach (var extra in extras.EnumerateArray()) if (extra.ValueKind == JsonValueKind.Object) yield return (extra, true);
        }
    }

    private static int? ReadPromptTokens(JsonElement? usage) => usage is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty("prompt_tokens", out var tokens) && tokens.TryGetInt32(out var count) ? count : null;
    private static void PrintSummary(IReadOnlyList<Item> items) => Console.WriteLine($"P6M: {items.Count} packs; {items.Sum(item => item.Pack.OwnedAliases.Count)} owned atoms; max body {items.Max(item => item.Body.Length)} B; protocol {V5SparseCandidateRequestComposerV1.CompactDirectoryCanonicalVersion}.");
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static void Write(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine, new UTF8Encoding(false));
    private static int Fail(string message) { Console.Error.WriteLine(message); return 1; }
    private static string? GitHead(string repo) { try { using var process = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD") { WorkingDirectory = repo, RedirectStandardOutput = true, UseShellExecute = false }); var output = process!.StandardOutput.ReadToEnd().Trim(); process.WaitForExit(); return process.ExitCode == 0 ? output : null; } catch { return null; } }
}
