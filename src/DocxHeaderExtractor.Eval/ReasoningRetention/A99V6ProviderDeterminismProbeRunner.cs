using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>Provider determinism probe using the already-frozen provider JSON bytes. The body is
/// loaded as a string from the prior request-equivalence artifact and sent as ByteArrayContent;
/// no request reconstruction, seed, provider pin, or automatic retry is permitted.</summary>
public static class A99V6ProviderDeterminismProbeRunner
{
    private const string DocumentId = "DOC-0205";
    private const string RequestArtifact = "eval/a99-closed-loop/request-equivalence/DOC-0205/audit.v1.json";
    private const string OldReference = "eval/a99-closed-loop/production-acceptance/runs/flash-restart-20260912-02/DOC-0205/r1/prediction.v1.json";
    private const string NewReference = "eval/a99-closed-loop/production-v6-accuracy-full-e2e/DOC-0205/r1/prediction.v1.json";
    private const string OutputRoot = "eval/a99-closed-loop/request-equivalence/DOC-0205/provider-determinism-probe";
    private const string Model = "qwen/qwen3.7-flash";
    private const int ProbeCalls = 8;
    private const int ExpectedRequestBytes = 139_212;
    private static readonly JsonSerializerOptions OutputOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var artifactPath = Path.Combine(repoRoot, RequestArtifact.Replace('/', Path.DirectorySeparatorChar));
        using var artifact = JsonDocument.Parse(await File.ReadAllTextAsync(artifactPath, ct));
        var bodyJson = artifact.RootElement.GetProperty("providerRequest").GetProperty("newSerializedJson").GetString()
            ?? throw new InvalidDataException("FROZEN_PROVIDER_JSON_MISSING");
        var bodyBytes = Encoding.UTF8.GetBytes(bodyJson);
        var requestSha = Sha256(bodyBytes);
        if (bodyBytes.Length != ExpectedRequestBytes)
            throw new InvalidDataException($"FROZEN_PROVIDER_JSON_SIZE_MISMATCH:{bodyBytes.Length}");
        var oldFamily = await LoadReferenceAsync(Path.Combine(repoRoot, OldReference.Replace('/', Path.DirectorySeparatorChar)), ct);
        var newFamily = await LoadReferenceAsync(Path.Combine(repoRoot, NewReference.Replace('/', Path.DirectorySeparatorChar)), ct);

        var options = RemoteInferenceOptions.FromEnvironment("openrouter");
        if (string.IsNullOrWhiteSpace(options.ApiKey)) throw new InvalidOperationException("PROVIDER_AUTH_FAILURE");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var output = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        var callsDir = Path.Combine(output, "calls");
        Directory.CreateDirectory(callsDir);
        var calls = new List<ProbeCall>();
        for (var callNumber = 1; callNumber <= ProbeCalls; callNumber++)
        {
            ct.ThrowIfCancellationRequested();
            var call = await SendOneAsync(http, options.Endpoint, options.ApiKey, bodyBytes, requestSha, callNumber, oldFamily.Raw, newFamily.Raw, ct);
            calls.Add(call);
            await File.WriteAllTextAsync(Path.Combine(callsDir, $"call-{callNumber:00}.v1.json"), JsonSerializer.Serialize(call, OutputOptions) + Environment.NewLine, ct);
            Console.WriteLine($"PROBE_CALL={callNumber}/{ProbeCalls} HTTP={call.HttpStatus?.ToString() ?? "TRANSPORT_ERROR"} RAW_SHA={call.RawResponseSha256 ?? "NONE"} PARSED_COUNT={call.RawHeadingCount?.ToString() ?? "NONE"}");
        }

        var parsedHashes = calls.Where(call => call.ParsedHeadingSha256 is not null).Select(call => call.ParsedHeadingSha256!).Distinct(StringComparer.Ordinal).ToArray();
        var rawHashes = calls.Where(call => call.RawResponseSha256 is not null).Select(call => call.RawResponseSha256!).Distinct(StringComparer.Ordinal).ToArray();
        var report = new
        {
            schemaVersion = "a99-v6-provider-determinism-probe-v1",
            documentId = DocumentId,
            model = Model,
            endpoint = options.Endpoint.ToString(),
            providerRoute = "MODEL_DEFAULT",
            providerPinAdded = false,
            seedAdded = false,
            automaticRetries = false,
            requestRebuilt = false,
            requestBodyBytes = bodyBytes.Length,
            requestBodySha256 = requestSha,
            expectedRequestBodySha256 = artifact.RootElement.GetProperty("providerRequest").GetProperty("newSha256").GetString(),
            requestBodyStableAcrossCalls = calls.All(call => call.RequestBodyBytes == bodyBytes.Length && call.RequestBodySha256 == requestSha),
            probeCalls = ProbeCalls,
            modelCalls = ProbeCalls,
            providerCalls = ProbeCalls,
            goldRead = false,
            predictionRead = true,
            referencePredictions = new { oldRawSha256 = oldFamily.RawSha256, newRawSha256 = newFamily.RawSha256, referenceRepeat = "r1", note = "Only frozen raw reference arrays were read; no Gold or score artifact was opened." },
            distinctRawResponseHashes = rawHashes.Length,
            distinctParsedHeadingHashes = parsedHashes.Length,
            rawResponseDeterminism = rawHashes.Length == 1 ? "ALL_RAW_RESPONSES_IDENTICAL" : "RAW_RESPONSES_VARY",
            parsedHeadingDeterminism = parsedHashes.Length == 1 ? "ALL_PARSED_HEADINGS_IDENTICAL" : "PARSED_HEADINGS_VARY",
            calls,
            classification = Classify(calls, rawHashes.Length, parsedHashes.Length),
        };
        await File.WriteAllTextAsync(Path.Combine(output, "probe-summary.v1.json"), JsonSerializer.Serialize(report, OutputOptions) + Environment.NewLine, ct);
        Console.WriteLine($"REQUEST_BODY_BYTES={bodyBytes.Length}");
        Console.WriteLine($"REQUEST_BODY_SHA256={requestSha}");
        Console.WriteLine($"DISTINCT_RAW_RESPONSE_HASHES={rawHashes.Length}");
        Console.WriteLine($"DISTINCT_PARSED_HEADING_HASHES={parsedHashes.Length}");
        Console.WriteLine($"PROBE_CLASSIFICATION={Classify(calls, rawHashes.Length, parsedHashes.Length)}");
        return 0;
    }

    private static async Task<ProbeCall> SendOneAsync(HttpClient http, Uri endpoint, string apiKey, byte[] bodyBytes, string requestSha, int callNumber, IReadOnlyList<RawHeading> oldFamily, IReadOnlyList<RawHeading> newFamily, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new ByteArrayContent(bodyBytes),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Headers.TryAddWithoutValidation("X-Title", "DocxHeaderExtractor Accuracy99 Determinism Probe");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
            var responseBytes = await response.Content.ReadAsByteArrayAsync(ct);
            stopwatch.Stop();
            return ParseResponse(callNumber, requestSha, bodyBytes.Length, response.StatusCode, responseBytes, response.Headers, response.Content.Headers, stopwatch.ElapsedMilliseconds, oldFamily, newFamily);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            stopwatch.Stop();
            return new ProbeCall(callNumber, bodyBytes.Length, requestSha, null, null, null, null, null, null, null, null,
                null, null, null, null, null, stopwatch.ElapsedMilliseconds, null, null,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), ex.GetType().Name + ":" + ex.Message);
        }
    }

    private static ProbeCall ParseResponse(int callNumber, string requestSha, int requestBytes, System.Net.HttpStatusCode status, byte[] responseBytes,
        HttpResponseHeaders headers, HttpContentHeaders contentHeaders, long latencyMs, IReadOnlyList<RawHeading> oldFamily, IReadOnlyList<RawHeading> newFamily)
    {
        var rawSha = Sha256(responseBytes);
        string? responseModel = null, responseId = null, actualProvider = null, finishReason = null, created = null, parsedSha = null;
        int? rawCount = null, inputTokens = null, reasoningTokens = null, outputTokens = null;
        string? parseError = null;
        FamilyComparison? comparisonVsOld = null;
        FamilyComparison? comparisonVsNew = null;
        try
        {
            using var response = JsonDocument.Parse(responseBytes);
            var root = response.RootElement;
            responseModel = ReadString(root, "model");
            responseId = ReadString(root, "id");
            actualProvider = ReadString(root, "provider") ?? ReadString(root, "provider_name");
            created = root.TryGetProperty("created", out var createdValue) ? createdValue.ToString() : null;
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                inputTokens = ReadInt(usage, "prompt_tokens");
                outputTokens = ReadInt(usage, "completion_tokens");
                if (usage.TryGetProperty("completion_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object)
                    reasoningTokens = ReadInt(details, "reasoning_tokens");
            }
            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                var choice = choices[0];
                finishReason = ReadString(choice, "finish_reason");
                if (choice.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object &&
                    message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                {
                    var parsed = SemanticTextExactBindingContract.Parse(content.GetString()!);
                    rawCount = parsed.Headings.Count;
                    parsedSha = Sha256(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(parsed.Headings)));
                    var probe = parsed.Headings.Select(item => new RawHeading(item.Source, item.Text)).ToArray();
                    comparisonVsOld = CompareFamily(probe, oldFamily);
                    comparisonVsNew = CompareFamily(probe, newFamily);
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidDataException)
        {
            parseError = ex.GetType().Name + ":" + ex.Message;
        }
        return new ProbeCall(callNumber, requestBytes, requestSha, (int)status, responseBytes.Length, rawSha, parsedSha,
            rawCount, actualProvider, responseModel, responseId, created, finishReason, inputTokens, reasoningTokens,
            outputTokens, latencyMs, comparisonVsOld, comparisonVsNew, SafeHeaders(headers, contentHeaders), parseError);
    }

    private static FamilyComparison CompareFamily(IReadOnlyList<RawHeading> probe, IReadOnlyList<RawHeading> reference)
    {
        var same = 0;
        var referenceUnmatched = 0;
        var probeUnmatched = 0;
        foreach (var alias in probe.Select(item => item.Source).Union(reference.Select(item => item.Source), StringComparer.Ordinal))
        {
            var probeCounts = probe.Where(item => item.Source == alias).GroupBy(item => item.Text, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            var referenceCounts = reference.Where(item => item.Source == alias).GroupBy(item => item.Text, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            foreach (var text in probeCounts.Keys.Union(referenceCounts.Keys, StringComparer.Ordinal))
            {
                var probeCount = probeCounts.GetValueOrDefault(text);
                var referenceCount = referenceCounts.GetValueOrDefault(text);
                same += Math.Min(probeCount, referenceCount);
                referenceUnmatched += Math.Max(0, referenceCount - probeCount);
                probeUnmatched += Math.Max(0, probeCount - referenceCount);
            }
        }
        return new FamilyComparison(same, Math.Min(referenceUnmatched, probeUnmatched),
            referenceUnmatched > probeUnmatched ? referenceUnmatched - probeUnmatched : 0,
            probeUnmatched > referenceUnmatched ? probeUnmatched - referenceUnmatched : 0);
    }

    private static string Classify(IReadOnlyList<ProbeCall> calls, int rawHashCount, int parsedHashCount)
    {
        if (calls.Any(call => call.HttpStatus is null)) return "TRANSPORT_OR_TIMEOUT_PRESENT";
        if (rawHashCount == 1 && parsedHashCount == 1) return "DETERMINISTIC_RAW_AND_PARSED_RESPONSE";
        if (rawHashCount == 1 && parsedHashCount > 1) return "CLIENT_OR_PARSER_VARIATION_WITH_IDENTICAL_RAW_RESPONSE";
        if (rawHashCount > 1) return "INFERENCE_OR_PROVIDER_RESPONSE_NONDETERMINISM";
        return "NO_COMPLETE_RESPONSE_TO_CLASSIFY";
    }

    private static Dictionary<string, string> SafeHeaders(HttpResponseHeaders headers, HttpContentHeaders contentHeaders)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in headers.Concat(contentHeaders))
        {
            var name = pair.Key;
            if (name.Contains("request-id", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("trace-id", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("timing", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("duration", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("processing", StringComparison.OrdinalIgnoreCase))
                result[name] = string.Join(",", pair.Value);
        }
        return result;
    }

    private static string? ReadString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static int? ReadInt(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : null;
    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static async Task<ReferenceFamily> LoadReferenceAsync(string path, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, ct));
        var rawElement = document.RootElement.GetProperty("rawModelHeadings");
        var raw = rawElement.EnumerateArray().Select(item => new RawHeading(item.GetProperty("source").GetString()!, item.GetProperty("text").GetString()!)).ToArray();
        return new ReferenceFamily(raw, Sha256(Encoding.UTF8.GetBytes(rawElement.GetRawText())));
    }
}

public sealed record ProbeCall(
    int CallNumber,
    int RequestBodyBytes,
    string RequestBodySha256,
    int? HttpStatus,
    int? RawResponseBytes,
    string? RawResponseSha256,
    string? ParsedHeadingSha256,
    int? RawHeadingCount,
    string? ActualProvider,
    string? ResponseModel,
    string? ResponseId,
    string? Created,
    string? FinishReason,
    int? InputTokens,
    int? ReasoningTokens,
    int? OutputTokens,
    long LatencyMs,
    FamilyComparison? ComparisonVsOld,
    FamilyComparison? ComparisonVsNew,
    IReadOnlyDictionary<string, string> SafeResponseHeaders,
    string? ParseError);

public sealed record FamilyComparison(int SameAliasSameText, int TextDrift, int AliasPresentAbsent, int NewExtraAlias);

internal sealed record RawHeading(string Source, string Text);
internal sealed record ReferenceFamily(IReadOnlyList<RawHeading> Raw, string RawSha256);
