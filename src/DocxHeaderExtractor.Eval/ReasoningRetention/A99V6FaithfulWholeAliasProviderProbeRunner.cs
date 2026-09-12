using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>
/// Sends the exact WHOLE_ALIAS request bytes reconstructed from the frozen campaign contract.
/// The reconstruction must match the frozen request hash before any provider call is allowed.
/// This is deliberately independent of the live runner: no Gold, no score, no retry, no request
/// rebuild between calls, and no provider capability request is made.
/// </summary>
public static class A99V6FaithfulWholeAliasProviderProbeRunner
{
    private const string Model = "qwen/qwen3.7-flash";
    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";
    // Alibaba was the provider selected by the backend in the frozen responses, not a
    // request pin. The campaign request hash equaled its canonical unpinned hash.
    private static readonly string? RequestProviderRoute = null;
    private const string ExpectedRequestHash = "c84538be36adb82c0f3a26a33590596890177ddb4ed5b02463aff539f543ecf5";
    private const int ProbeCalls = 8;
    private const string SourcePath = "eval/a99-closed-loop/source-fidelity-audit/DOC-0205/converted-docx/025_ND_47-2020_Chia_se_du_lieu_so.docx";
    private const string FrozenPredictionPath = "eval/a99-closed-loop/source-fidelity-whole-alias-live/DOC-0205/r2/whole-alias/prediction.v1.json";
    private const string OutputRoot = "eval/a99-closed-loop/source-fidelity-whole-alias-provider-probe/DOC-0205";
    private static readonly JsonSerializerOptions OutputOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
    private static readonly string WholeAliasSystem = $"""
You identify every structurally real document heading or structural label in the supplied source.
A single source occurrence may contain zero, one, or many independent headings. Decide semantic
existence and role yourself from the complete source. Formatting, numbering, and layout are
evidence, not rules. Return only headings you discover in the supplied source.

For every heading, return:
- source: the supplied short source alias, copied exactly
- selectionMode: WHOLE_ALIAS
- role: one allowed semantic role

WHOLE_ALIAS means the harness will use the complete text of the selected source alias. Do not
return text, character offsets, source IDs, hierarchy, confidence, explanations, or chain-of-thought.
Do not invent or normalize source text. Do not use Gold. Return each semantic heading at most once.

Allowed roles: {string.Join(", ", CeilingSemanticRole.AllowedRoles)}.
Return only the JSON object described by the schema.
""";

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        var callsDirectory = Path.Combine(output, "calls");
        Directory.CreateDirectory(callsDirectory);

        var context = LoadContext(repoRoot);
        var packet = BuildPacket(context);
        var schema = WholeAliasSchema();
        var user = BuildWholeAliasUser(packet);
        var reasoning = new { enabled = true, exclude = true };
        var body = OpenRouterCeilingReasoningModel.BuildRequestBodyForAudit(
            Model, WholeAliasSystem, user, 48_000, schema, "faithful-whole-alias-live",
            reasoning, RequestProviderRoute, allowNonZdrPublicBenchmark: true);
        var bodyBytes = OpenRouterCeilingReasoningModel.SerializeRequestBodyForAudit(body);
        var bodyHash = Sha256(bodyBytes);

        var frozenPredictionPath = Path.Combine(repoRoot, FrozenPredictionPath.Replace('/', Path.DirectorySeparatorChar));
        using var frozenPrediction = JsonDocument.Parse(await File.ReadAllTextAsync(frozenPredictionPath, ct));
        var frozenCampaignHash = frozenPrediction.RootElement.GetProperty("telemetry").GetProperty("requestBodyHash").GetString();
        if (!string.Equals(bodyHash, ExpectedRequestHash, StringComparison.Ordinal) ||
            !string.Equals(bodyHash, frozenCampaignHash, StringComparison.OrdinalIgnoreCase))
        {
            await WriteJson(Path.Combine(output, "probe-summary.v1.json"), new
            {
                schemaVersion = "a99-v6-faithful-whole-alias-provider-probe-v1",
                status = "REQUEST_RECONSTRUCTION_MISMATCH", model = Model,
                expectedRequestBodySha256 = ExpectedRequestHash, frozenCampaignRequestBodySha256 = frozenCampaignHash,
                reconstructedRequestBodySha256 = bodyHash, requestBodyBytes = bodyBytes.Length,
                modelCalls = 0, providerCalls = 0, goldRead = false,
            }, ct);
            throw new InvalidDataException($"REQUEST_RECONSTRUCTION_MISMATCH:{bodyHash}");
        }

        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("PROVIDER_AUTH_FAILURE");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var calls = new List<ProviderProbeCall>();
        for (var callNumber = 1; callNumber <= ProbeCalls; callNumber++)
        {
            ct.ThrowIfCancellationRequested();
            var call = await SendOneAsync(http, apiKey, bodyBytes, bodyHash, callNumber, ct);
            calls.Add(call);
            await WriteJson(Path.Combine(callsDirectory, $"call-{callNumber:00}.v1.json"), call, ct);
            Console.WriteLine($"WHOLE_ALIAS_PROBE_CALL={callNumber}/{ProbeCalls} HTTP={call.HttpStatus?.ToString() ?? "TRANSPORT_ERROR"} RAW_SHA={call.RawResponseSha256 ?? "NONE"} PARSED_COUNT={call.RawProposalCount?.ToString() ?? "NONE"}");
        }

        var rawHashes = calls.Where(call => call.RawResponseSha256 is not null).Select(call => call.RawResponseSha256!).Distinct(StringComparer.Ordinal).ToArray();
        var parsedHashes = calls.Where(call => call.ParsedAliasSetSha256 is not null).Select(call => call.ParsedAliasSetSha256!).Distinct(StringComparer.Ordinal).ToArray();
        var counts = calls.Where(call => call.RawProposalCount is not null).GroupBy(call => call.RawProposalCount!.Value).OrderBy(group => group.Key)
            .Select(group => new { rawProposalCount = group.Key, calls = group.Count() }).ToArray();
        var classification = Classify(calls, rawHashes.Length, parsedHashes.Length);
        var report = new
        {
            schemaVersion = "a99-v6-faithful-whole-alias-provider-probe-v1",
            documentId = "DOC-0205", model = Model, endpoint = Endpoint, providerRoute = "AUTO_UNPINNED",
            requestBodyBytes = bodyBytes.Length, requestBodySha256 = bodyHash,
            expectedCampaignRequestBodySha256 = ExpectedRequestHash,
            requestBodyStableAcrossCalls = calls.All(call => call.RequestBodyBytes == bodyBytes.Length && call.RequestBodySha256 == bodyHash),
            requestRebuilt = false, automaticRetries = false, providerPinChanged = false, seedAdded = false,
            probeCalls = ProbeCalls, modelCalls = ProbeCalls, providerCalls = ProbeCalls,
            goldRead = false, predictionRead = true,
            frozenCampaignReference = new { path = FrozenPredictionPath, requestBodySha256 = frozenCampaignHash, note = "Only frozen telemetry was read to verify exact request reconstruction; Gold and scores were not opened." },
            distinctRawResponseHashes = rawHashes.Length, distinctParsedAliasSetHashes = parsedHashes.Length,
            observedRawProposalCountFamilies = counts,
            rawResponseDeterminism = rawHashes.Length == 1 ? "ALL_RAW_RESPONSES_IDENTICAL" : "RAW_RESPONSES_VARY",
            parsedAliasSetDeterminism = parsedHashes.Length == 1 ? "ALL_PARSED_ALIAS_SETS_IDENTICAL" : "PARSED_ALIAS_SETS_VARY",
            classification, calls,
        };
        await WriteJson(Path.Combine(output, "probe-summary.v1.json"), report, ct);
        Console.WriteLine($"WHOLE_ALIAS_PROBE_REQUEST_BODY_BYTES={bodyBytes.Length}");
        Console.WriteLine($"WHOLE_ALIAS_PROBE_REQUEST_BODY_SHA256={bodyHash}");
        Console.WriteLine($"WHOLE_ALIAS_PROBE_DISTINCT_RAW_RESPONSE_HASHES={rawHashes.Length}");
        Console.WriteLine($"WHOLE_ALIAS_PROBE_DISTINCT_PARSED_ALIAS_SETS={parsedHashes.Length}");
        Console.WriteLine($"WHOLE_ALIAS_PROBE_CLASSIFICATION={classification}");
        return 0;
    }

    private static async Task<ProviderProbeCall> SendOneAsync(HttpClient http, string apiKey, byte[] bodyBytes, string bodyHash, int callNumber, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = new ByteArrayContent(bodyBytes) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Headers.TryAddWithoutValidation("X-Title", "DocxHeaderExtractor Accuracy99 Whole Alias Probe");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
            var responseBytes = await response.Content.ReadAsByteArrayAsync(ct);
            stopwatch.Stop();
            return ParseResponse(callNumber, bodyBytes.Length, bodyHash, response.StatusCode, responseBytes, response.Headers, response.Content.Headers, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            stopwatch.Stop();
            return new ProviderProbeCall(callNumber, bodyBytes.Length, bodyHash, null, null, null, null, null, null, null,
                null, null, null, null, null, null, stopwatch.ElapsedMilliseconds, new Dictionary<string, string>(), ex.GetType().Name + ":" + ex.Message);
        }
    }

    private static ProviderProbeCall ParseResponse(int callNumber, int bodyBytes, string bodyHash, System.Net.HttpStatusCode status,
        byte[] responseBytes, HttpResponseHeaders headers, HttpContentHeaders contentHeaders, long latencyMs)
    {
        var rawSha = Sha256(responseBytes);
        string? responseModel = null, responseId = null, provider = null, created = null, finishReason = null, aliasSetSha = null, parseError = null;
        int? rawCount = null, inputTokens = null, reasoningTokens = null, outputTokens = null;
        try
        {
            using var document = JsonDocument.Parse(responseBytes);
            var root = document.RootElement;
            responseModel = ReadString(root, "model"); responseId = ReadString(root, "id");
            provider = ReadString(root, "provider") ?? ReadString(root, "provider_name");
            created = root.TryGetProperty("created", out var createdValue) ? createdValue.ToString() : null;
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                inputTokens = ReadInt(usage, "prompt_tokens"); outputTokens = ReadInt(usage, "completion_tokens");
                if (usage.TryGetProperty("completion_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object)
                    reasoningTokens = ReadInt(details, "reasoning_tokens");
            }
            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                var choice = choices[0]; finishReason = ReadString(choice, "finish_reason");
                if (choice.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                {
                    var aliases = ParseAliasSet(content.GetString()!, out var count);
                    rawCount = count;
                    aliasSetSha = Sha256(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(aliases)));
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidDataException)
        {
            parseError = ex.GetType().Name + ":" + ex.Message;
        }
        return new ProviderProbeCall(callNumber, bodyBytes, bodyHash, (int)status, responseBytes.Length, rawSha, aliasSetSha, rawCount,
            provider, responseModel, responseId, created, finishReason, inputTokens, reasoningTokens, outputTokens, latencyMs,
            SafeHeaders(headers, contentHeaders), parseError);
    }

    private static IReadOnlyList<string> ParseAliasSet(string content, out int count)
    {
        var start = content.IndexOf('{'); var end = content.LastIndexOf('}');
        if (start < 0 || end < start) throw new FormatException("WHOLE_ALIAS_JSON_INCOMPLETE");
        using var document = JsonDocument.Parse(content[start..(end + 1)]);
        var aliases = document.RootElement.GetProperty("headings").EnumerateArray()
            .Select(item => item.GetProperty("source").GetString() ?? throw new FormatException("WHOLE_ALIAS_SOURCE_MISSING"))
            .ToArray();
        count = aliases.Length;
        return aliases.OrderBy(item => item, StringComparer.Ordinal).ToArray();
    }

    private static string Classify(IReadOnlyList<ProviderProbeCall> calls, int rawHashes, int parsedHashes)
    {
        if (calls.Any(call => call.HttpStatus is null)) return "TRANSPORT_OR_TIMEOUT_PRESENT";
        if (rawHashes > 1 && parsedHashes == 1) return "RAW_RESPONSE_NONDETERMINISM_ALIAS_SELECTION_STABLE";
        if (rawHashes == 1 && parsedHashes == 1) return "DETERMINISTIC_RAW_AND_PARSED_ALIAS_SET";
        if (rawHashes == 1 && parsedHashes > 1) return "CLIENT_OR_PARSER_VARIATION_WITH_IDENTICAL_RAW_RESPONSE";
        if (rawHashes > 1) return "INFERENCE_OR_PROVIDER_RESPONSE_NONDETERMINISM";
        return "NO_COMPLETE_RESPONSE_TO_CLASSIFY";
    }

    private static Dictionary<string, string> SafeHeaders(HttpResponseHeaders headers, HttpContentHeaders contentHeaders)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in headers.Concat(contentHeaders))
        {
            if (pair.Key.Contains("request-id", StringComparison.OrdinalIgnoreCase) || pair.Key.Contains("trace-id", StringComparison.OrdinalIgnoreCase) ||
                pair.Key.Contains("timing", StringComparison.OrdinalIgnoreCase) || pair.Key.Contains("duration", StringComparison.OrdinalIgnoreCase) ||
                pair.Key.Contains("processing", StringComparison.OrdinalIgnoreCase)) result[pair.Key] = string.Join(",", pair.Value);
        }
        return result;
    }

    private static SourceContext LoadContext(string repoRoot)
    {
        var path = Path.Combine(repoRoot, SourcePath.Replace('/', Path.DirectorySeparatorChar));
        var source = new OpenXmlDocumentSource().Read(path) with { DocumentId = "DOC-0205" };
        var aliases = source.Paragraphs.Where(item => !string.IsNullOrWhiteSpace(item.Text))
            .Select((item, index) => new Alias($"S{index + 1:0000}", item.SourceOrdinal, item.Text)).ToArray();
        return new(aliases);
    }

    private static string BuildPacket(SourceContext context) => JsonSerializer.Serialize(new
    {
        sourceAliases = context.Aliases.Select(alias => new { alias = alias.Name, text = alias.Text, sourceOrdinal = alias.Ordinal }).ToArray(),
    });

    private static string BuildWholeAliasUser(string packet) => $"TASK=a99-faithful-whole-alias-v1\nroute={ReasoningRoute.ModelCapabilityCeiling}\n{packet}";

    private static object WholeAliasSchema() => new
    {
        type = "object", additionalProperties = false,
        properties = new
        {
            headings = new
            {
                type = "array", items = new
                {
                    type = "object", additionalProperties = false,
                    properties = new
                    {
                        source = new { type = "string", minLength = 1 },
                        selectionMode = new { type = "string", @enum = new[] { CanonicalSemanticSelectionMode.WholeAlias } },
                        isHeading = new { type = "boolean" }, role = new { type = "string", @enum = CeilingSemanticRole.AllowedRoles },
                    }, required = new[] { "source", "selectionMode", "isHeading", "role" },
                },
            },
        }, required = new[] { "headings" },
    };

    private static string? ReadString(JsonElement parent, string name) => parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static int? ReadInt(JsonElement parent, string name) => parent.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : null;
    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static async Task WriteJson(string path, object value, CancellationToken ct) => await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, OutputOptions) + Environment.NewLine, new UTF8Encoding(false), ct);

    private sealed record SourceContext(IReadOnlyList<Alias> Aliases);
    private sealed record Alias(string Name, int Ordinal, string Text);
}

public sealed record ProviderProbeCall(
    int CallNumber, int RequestBodyBytes, string RequestBodySha256, int? HttpStatus, int? RawResponseBytes,
    string? RawResponseSha256, string? ParsedAliasSetSha256, int? RawProposalCount, string? ActualProvider,
    string? ResponseModel, string? ResponseId, string? Created, string? FinishReason, int? InputTokens,
    int? ReasoningTokens, int? OutputTokens, long LatencyMs, IReadOnlyDictionary<string, string> SafeResponseHeaders,
    string? ParseError);
