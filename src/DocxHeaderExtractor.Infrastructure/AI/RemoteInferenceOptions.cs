using System.Net;

namespace DocxHeaderExtractor.Infrastructure.AI;

/// <summary>
/// Provider-neutral settings for an OpenAI-compatible inference endpoint.
/// Vendor defaults and adapter behavior belong to Infrastructure.AI.
/// </summary>
public sealed class RemoteInferenceOptions
{
    public const string DefaultModel = "qwen/qwen3.5-9b";
    public Uri Endpoint { get; set; } = new("https://openrouter.ai/api/v1/chat/completions");
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = DefaultModel;
    public int ContextSize { get; set; } = 32768;
    public int MaxOutputTokens { get; set; } = 768;
    public int MissingIdRetries { get; set; } = 2;
    public int RequestTimeoutSeconds { get; set; } = 90;
    public int TransientRequestRetries { get; set; } = 2;
    public int MaxParallelRequests { get; set; } = 1;
    public bool SendChatTemplateKwargs { get; set; } = true;
    public bool RequireJsonObjectResponse { get; set; }
    public Action<string>? DebugLog { get; set; }

    public void Validate(bool requireModel = true)
    {
        if (requireModel && string.IsNullOrWhiteSpace(Model)) throw new InvalidOperationException("Thiếu model inference.");
        if (!Endpoint.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !Endpoint.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Inference endpoint phải là http hoặc https.");
        if (!Endpoint.AbsolutePath.EndsWith("/v1/chat/completions", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Inference endpoint phải kết thúc bằng /v1/chat/completions.");
        if (ContextSize is < 1024 or > 1_048_576) throw new InvalidOperationException("ContextSize phải nằm trong khoảng 1024..1048576.");
        if (MissingIdRetries is < 0 or > 5) throw new InvalidOperationException("MissingIdRetries phải nằm trong khoảng 0..5.");
        if (RequestTimeoutSeconds is < 10 or > 600) throw new InvalidOperationException("RequestTimeoutSeconds phải nằm trong khoảng 10..600.");
        if (TransientRequestRetries is < 0 or > 4) throw new InvalidOperationException("TransientRequestRetries phải nằm trong khoảng 0..4.");
        if (MaxParallelRequests is < 1 or > 16) throw new InvalidOperationException("MaxParallelRequests phải nằm trong khoảng 1..16.");
    }

    public static RemoteInferenceOptions FromEnvironment(string profile = "openrouter")
    {
        var normalized = profile.Trim().ToLowerInvariant();
        return normalized switch
        {
            "lmstudio" => FromEnvironment(
                Environment.GetEnvironmentVariable("LMSTUDIO_ENDPOINT") ?? "http://127.0.0.1:1234/v1/chat/completions",
                Environment.GetEnvironmentVariable("LMSTUDIO_API_KEY") ?? "",
                Environment.GetEnvironmentVariable("LMSTUDIO_MODEL") ?? "",
                int.TryParse(Environment.GetEnvironmentVariable("LMSTUDIO_CONTEXT_SIZE"), out var lmContext) ? lmContext : 16_384),
            "sglang" => FromEnvironment(
                Environment.GetEnvironmentVariable("SGLANG_ENDPOINT") ?? "http://127.0.0.1:30000/v1/chat/completions",
                Environment.GetEnvironmentVariable("SGLANG_API_KEY") ?? "",
                Environment.GetEnvironmentVariable("SGLANG_MODEL") ?? "",
                int.TryParse(Environment.GetEnvironmentVariable("SGLANG_CONTEXT_SIZE"), out var sgContext) ? sgContext : 8192),
            _ => FromEnvironment(
                "https://openrouter.ai/api/v1/chat/completions",
                Environment.GetEnvironmentVariable("OPENROUTER_API_KEY") ?? "",
                Environment.GetEnvironmentVariable("OPENROUTER_MODEL") ?? DefaultModel,
                32768),
        };
    }

    public Uri ModelsEndpoint => new(Endpoint, "/v1/models");

    public static bool IsLoopback(Uri endpoint) => endpoint.IsLoopback ||
        endpoint.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        IPAddress.TryParse(endpoint.Host, out var address) && IPAddress.IsLoopback(address);

    private static RemoteInferenceOptions FromEnvironment(string endpoint, string apiKey, string model, int context) => new()
    {
        Endpoint = new Uri(endpoint, UriKind.Absolute),
        ApiKey = apiKey,
        Model = model,
        ContextSize = context,
        MaxParallelRequests = int.TryParse(Environment.GetEnvironmentVariable("LMSTUDIO_PARALLEL"), out var parallel)
            ? Math.Clamp(parallel, 1, 16) : 1,
        RequestTimeoutSeconds = int.TryParse(Environment.GetEnvironmentVariable("SGLANG_REQUEST_TIMEOUT_SECONDS"), out var timeout)
            ? Math.Clamp(timeout, 10, 600) : 90,
        TransientRequestRetries = int.TryParse(Environment.GetEnvironmentVariable("SGLANG_TRANSIENT_RETRIES"), out var retries)
            ? Math.Clamp(retries, 0, 4) : 2,
    };
}
