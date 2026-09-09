using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>
/// Transport-only request ladder for qwen/qwen3.7-flash. It deliberately does not use the
/// heading packet, semantic prompts, binder, validator, projection, or Gold. Each step adds
/// exactly one request-layer contract so a provider 404 can be attributed to the first change.
/// </summary>
public static class OpenRouterQwen37RequestLadderRunner
{
    private const string Model = "qwen/qwen3.7-flash";
    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";
    private const string OutputRoot = "eval/a99-closed-loop/qwen37-flash-control";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(output);
        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            await WriteJsonAsync(Path.Combine(output, "request-ladder.v1.json"), new
            {
                schemaVersion = "a99-qwen37-flash-request-ladder-v1", status = "BLOCKED_API_KEY_MISSING",
                model = Model, goldReadBeforeFreeze = false,
            }, ct);
            Console.WriteLine("FINAL_CLASSIFICATION=FLASH_EXECUTION_BLOCKED");
            return 1;
        }

        using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        using var lease = await A99OpenRouterLiveProviderLease.AcquireAsync(repoRoot, "qwen37-request-ladder", Model, ct);
        Console.WriteLine($"LIVE_PROVIDER_LOCK=acquired concurrentCampaignsDetected={lease.ConcurrentCampaignsDetected} providerConcurrency={lease.ProviderConcurrency}");

        var requests = new List<LadderRequest>();
        var p0 = MinimalBody();
        var p0Result = await ProbeAsync("P0_DOCUMENTED_MINIMAL", p0, http, key, requests, ct);
        if (!p0Result.IsSuccess)
            return await FinishAsync(output, requests, null, "P0_FAILED_STOP", ct);

        var p1 = p0.DeepClone().AsObject();
        p1["response_format"] = StructuredResponseFormat();
        var p1Result = await ProbeAsync("P1_STRUCTURED_OUTPUT", p1, http, key, requests, ct);
        if (!p1Result.IsSuccess)
            return await FinishAsync(output, requests, null, "P1_FAILED_STOP", ct);

        var p2Zdr = p1.DeepClone().AsObject();
        p2Zdr["provider"] = new JsonObject { ["zdr"] = true };
        var p2ZdrResult = await ProbeAsync("P2A_PRIVACY_ZDR", p2Zdr, http, key, requests, ct);
        if (!p2ZdrResult.IsSuccess)
            return await FinishAsync(output, requests, null, "P2A_FAILED_STOP", ct);

        var p2Data = p2Zdr.DeepClone().AsObject();
        p2Data["provider"]!.AsObject()["data_collection"] = "deny";
        var p2DataResult = await ProbeAsync("P2B_PRIVACY_DATA_COLLECTION_DENY", p2Data, http, key, requests, ct);
        if (!p2DataResult.IsSuccess)
            return await FinishAsync(output, requests, null, "P2B_FAILED_STOP", ct);

        var p3 = p2Data.DeepClone().AsObject();
        p3["provider"]!.AsObject()["require_parameters"] = true;
        var p3Result = await ProbeAsync("P3_REQUIRE_PARAMETERS", p3, http, key, requests, ct);
        if (!p3Result.IsSuccess)
            return await FinishAsync(output, requests, null, "P3_FAILED_STOP", ct);

        var p4Fallback = p3.DeepClone().AsObject();
        p4Fallback["provider"]!.AsObject()["allow_fallbacks"] = false;
        var p4FallbackResult = await ProbeAsync("P4A_ALLOW_FALLBACKS_FALSE", p4Fallback, http, key, requests, ct);
        if (!p4FallbackResult.IsSuccess)
            return await FinishAsync(output, requests, null, "P4A_FAILED_STOP", ct);

        var routeResolution = await OpenRouterProviderRouteResolver.ResolveAsync(
            new RemoteInferenceOptions { Endpoint = new Uri(Endpoint), ApiKey = key, Model = Model }, http, ct);
        var route = routeResolution.Routes.FirstOrDefault(x => x.SupportsCeilingRequest)?.Route;
        if (string.IsNullOrWhiteSpace(route))
            return await FinishAsync(output, requests, routeResolution, "P4_PROVIDER_ROUTE_UNAVAILABLE", ct);

        Console.WriteLine($"P4_PROVIDER_ROUTE={route}");
        var p4 = p4Fallback.DeepClone().AsObject();
        p4["provider"]!.AsObject()["order"] = new JsonArray(JsonValue.Create(route));
        var p4Result = await ProbeAsync("P4B_PROVIDER_ROUTE", p4, http, key, requests, ct);
        var classification = p4Result.IsSuccess ? "REQUEST_CONTRACT_PROVEN" : "P4B_PROVIDER_ROUTE_FAILED";
        return await FinishAsync(output, requests, routeResolution, classification, ct);
    }

    private static async Task<int> FinishAsync(string output, IReadOnlyList<LadderRequest> requests,
        OpenRouterProviderRoutesResult? routeResolution, string classification, CancellationToken ct)
    {
        var firstFailure = requests.FirstOrDefault(x => !x.IsSuccess);
        var oldFields = new[]
        {
            "temperature", "max_tokens", "messages.system", "messages.user.semantic_packet",
            "reasoning.exclude", "response_format", "provider.zdr", "provider.data_collection",
            "provider.require_parameters", "provider.allow_fallbacks",
        };
        var minimalFields = new[] { "model", "messages[0].user.tiny", "reasoning.enabled" };
        await WriteJsonAsync(Path.Combine(output, "request-ladder.v1.json"), new
        {
            schemaVersion = "a99-qwen37-flash-request-ladder-v1", status = classification,
            model = Model, endpoint = Endpoint, apiKeyPresent = true, goldReadBeforeFreeze = false,
            requests, firstFailingStage = firstFailure?.Stage, firstFailureStatus = firstFailure?.Status,
            firstFailureErrorBody = firstFailure?.ErrorBody,
            providerMetadata = routeResolution?.Routes,
            documentedWorkingRequest = new { model = Model, messages = "one tiny user message", reasoning = new { enabled = true } },
            oldFlashRunnerRequest = new { model = Model, temperature = 0, max_tokens = 48_000,
                messages = new[] { "system semantic prompt", "user semantic packet" },
                reasoning = new { enabled = true, exclude = true }, response_format = "json_schema",
                provider = new { zdr = true, data_collection = "deny", require_parameters = true, allow_fallbacks = false } },
            requestDiff = new { documentedRequestFields = minimalFields, additionalOldRunnerFields = oldFields },
            completedUtc = DateTimeOffset.UtcNow,
        }, ct);
        Console.WriteLine($"FINAL_CLASSIFICATION={(classification == "REQUEST_CONTRACT_PROVEN" ? classification : "FLASH_EXECUTION_BLOCKED")}");
        return classification == "REQUEST_CONTRACT_PROVEN" ? 0 : 1;
    }

    private static async Task<LadderResult> ProbeAsync(string stage, JsonObject body, HttpClient http, string key,
        ICollection<LadderRequest> requests, CancellationToken ct)
    {
        var json = body.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        var stopwatch = Stopwatch.StartNew();
        var result = new LadderResult(false, 0, null, null, null, null, null, null, null, 0, null, null);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            var responseBody = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            result = ParseResult(response.StatusCode, responseBody, stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            result = new LadderResult(false, 0, "REQUEST_TIMEOUT", null, null, null, null, null, null, stopwatch.ElapsedMilliseconds, null, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            result = new LadderResult(false, 0, ex.GetType().Name, null, null, null, null, null, null, stopwatch.ElapsedMilliseconds, null, null);
        }

        requests.Add(new LadderRequest(stage, result.IsSuccess, result.Status, result.RawErrorBody ?? result.Error, result.ReportedModel,
            result.Provider, result.FinishReason, result.InputTokens, result.ReasoningTokens, result.OutputTokens,
            result.ElapsedMs, Sha256(json), json.Length, result.StructuredOutputParsed));
        Console.WriteLine($"{stage}={(result.IsSuccess ? "HTTP_200" : $"HTTP_{result.Status}:{result.Error}")} elapsedMs={result.ElapsedMs}");
        return result;
    }

    private static LadderResult ParseResult(System.Net.HttpStatusCode status, string body, long elapsedMs)
    {
        string? model = null, provider = null, finish = null, error = null;
        int? input = null, reasoning = null, output = null;
        bool? structured = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            model = String(root, "model");
            provider = String(root, "provider") ?? String(root, "provider_name");
            if (root.TryGetProperty("error", out var e)) error = e.ValueKind == JsonValueKind.Object ? String(e, "message") ?? e.ToString() : e.ToString();
            finish = root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
                ? String(choices[0], "finish_reason") : null;
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                input = Int(usage, "prompt_tokens"); output = Int(usage, "completion_tokens");
                reasoning = usage.TryGetProperty("completion_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object
                    ? Int(details, "reasoning_tokens") : null;
            }
            structured = status == System.Net.HttpStatusCode.OK && root.TryGetProperty("choices", out var c) && c.ValueKind == JsonValueKind.Array && c.GetArrayLength() > 0;
        }
        catch (JsonException) { error ??= "RESPONSE_JSON_INVALID"; }
        if (status != System.Net.HttpStatusCode.OK) error ??= $"HTTP_{(int)status}";
        return new LadderResult(status == System.Net.HttpStatusCode.OK, (int)status, error, model, provider,
            finish, input, reasoning, output, elapsedMs, structured,
            status == System.Net.HttpStatusCode.OK ? null : body.Length > 16_384 ? body[..16_384] : body);
    }

    private static JsonObject MinimalBody() => new()
    {
        ["model"] = Model,
        ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "Reply with the word OK." }),
        ["reasoning"] = new JsonObject { ["enabled"] = true },
    };

    private static JsonObject StructuredResponseFormat() => new()
    {
        ["type"] = "json_schema",
        ["json_schema"] = new JsonObject
        {
            ["name"] = "a99_minimal_probe",
            ["strict"] = true,
            ["schema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["ok"] = new JsonObject { ["type"] = "boolean" } },
                ["required"] = new JsonArray("ok"),
                ["additionalProperties"] = false,
            },
        },
    };

    private static string? String(JsonElement parent, string name) => parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static int? Int(JsonElement parent, string name) => parent.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : null;
    private static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static async Task WriteJsonAsync(string path, object value, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine);
        await File.WriteAllBytesAsync(path, bytes, ct);
    }

    private sealed record LadderResult(bool IsSuccess, int Status, string? Error, string? ReportedModel,
        string? Provider, string? FinishReason, int? InputTokens, int? ReasoningTokens, int? OutputTokens,
        long ElapsedMs, bool? StructuredOutputParsed, string? RawErrorBody);

    private sealed record LadderRequest(string Stage, bool IsSuccess, int Status, string? ErrorBody,
        string? ReportedModel, string? Provider, string? FinishReason, int? InputTokens, int? ReasoningTokens,
        int? OutputTokens, long ElapsedMs, string RequestBodySha256, int RequestBodyCharacters, bool? StructuredOutputParsed);
}
