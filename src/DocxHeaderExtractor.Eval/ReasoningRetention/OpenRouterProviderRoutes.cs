using System.Net.Http.Headers;
using System.Text.Json;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>Provider endpoint metadata returned by OpenRouter's model-endpoints API. This is
/// diagnostic metadata only; it never changes the semantic packet or model id.</summary>
public sealed record OpenRouterProviderRouteDescriptor
{
    public required string ProviderName { get; init; }
    public required string Route { get; init; }
    public string? EndpointTag { get; init; }
    public int ContextLength { get; init; }
    public int? MaxCompletionTokens { get; init; }
    public IReadOnlyList<string> SupportedParameters { get; init; } = [];
    public double? UptimeLast30Minutes { get; init; }
    public double? UptimeLast5Minutes { get; init; }
    public double? UptimeLastDay { get; init; }
    public double? LatencyP50Ms { get; init; }
    public double? LatencyP99Ms { get; init; }
    public double? ThroughputP50TokensPerSecond { get; init; }
    public bool SupportsImplicitCaching { get; init; }

    public bool SupportsCeilingRequest =>
        Has("response_format") && Has("structured_outputs") &&
        (Has("reasoning") || Has("include_reasoning")) && Has("max_tokens");

    private bool Has(string value) => SupportedParameters.Contains(value, StringComparer.OrdinalIgnoreCase);
}

public sealed record OpenRouterProviderRoutesResult(
    bool Available, string Classification, string Reason,
    IReadOnlyList<OpenRouterProviderRouteDescriptor> Routes);

public static class OpenRouterProviderRouteResolver
{
    public static Uri EndpointsUri(RemoteInferenceOptions options) =>
        new UriBuilder(options.Endpoint)
        {
            Path = "/api/v1/models/" + options.Model + "/endpoints",
            Query = "",
        }.Uri;

    public static async Task<OpenRouterProviderRoutesResult> ResolveAsync(
        RemoteInferenceOptions options, HttpClient http, CancellationToken ct = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var request = new HttpRequestMessage(HttpMethod.Get, EndpointsUri(options));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
            using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new(false, "PROVIDER_ROUTE_METADATA_UNAVAILABLE", $"ENDPOINTS_HTTP_{(int)response.StatusCode}", []);

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
            if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty("endpoints", out var endpoints) || endpoints.ValueKind != JsonValueKind.Array)
                return new(false, "PROVIDER_ROUTE_METADATA_INVALID", "ENDPOINTS_SCHEMA_INVALID", []);

            var routes = endpoints.EnumerateArray().Select(Parse).Where(x => x is not null).Cast<OpenRouterProviderRouteDescriptor>().ToArray();
            return new(true, "", routes.Length == 0 ? "NO_PROVIDER_ROUTES" : "ROUTES_RESOLVED", routes);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(false, "PROVIDER_ROUTE_METADATA_UNAVAILABLE", "ENDPOINTS_TIMEOUT", []);
        }
        catch (HttpRequestException ex)
        {
            return new(false, "PROVIDER_ROUTE_METADATA_UNAVAILABLE", ex.GetType().Name, []);
        }
        catch (JsonException)
        {
            return new(false, "PROVIDER_ROUTE_METADATA_INVALID", "ENDPOINTS_JSON_INVALID", []);
        }
    }

    private static OpenRouterProviderRouteDescriptor? Parse(JsonElement value)
    {
        var provider = String(value, "provider_name");
        // OpenRouter's provider-selection order accepts the stable provider slug. Endpoint
        // tags are retained as metadata, but some tag variants are informational and return
        // HTTP 404 when sent as an order value.
        var route = Slug(provider!);
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(route)) return null;
        var parameters = value.TryGetProperty("supported_parameters", out var p) && p.ValueKind == JsonValueKind.Array
            ? p.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray()
            : [];
        var latency = value.TryGetProperty("latency_last_30m", out var l) && l.ValueKind == JsonValueKind.Object ? l : default;
        var throughput = value.TryGetProperty("throughput_last_30m", out var t) && t.ValueKind == JsonValueKind.Object ? t : default;
        return new OpenRouterProviderRouteDescriptor
        {
            ProviderName = provider,
            Route = route,
            EndpointTag = String(value, "tag"),
            ContextLength = Int(value, "context_length") ?? 0,
            MaxCompletionTokens = Int(value, "max_completion_tokens"),
            SupportedParameters = parameters,
            UptimeLast30Minutes = Double(value, "uptime_last_30m"),
            UptimeLast5Minutes = Double(value, "uptime_last_5m"),
            UptimeLastDay = Double(value, "uptime_last_1d"),
            LatencyP50Ms = Double(latency, "p50"),
            LatencyP99Ms = Double(latency, "p99"),
            ThroughputP50TokensPerSecond = Double(throughput, "p50"),
            SupportsImplicitCaching = value.TryGetProperty("supports_implicit_caching", out var cache) && cache.ValueKind == JsonValueKind.True,
        };
    }

    private static string? String(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    private static int? Int(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var i) ? i : null;
    private static double? Double(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var d) ? d : null;
    private static string Slug(string provider) => provider.Trim().ToLowerInvariant().Replace(' ', '-');
}
