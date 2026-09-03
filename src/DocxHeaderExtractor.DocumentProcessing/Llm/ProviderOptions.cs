using System.Net;

namespace DocxHeaderExtractor.Core.Llm;

public sealed class OpenRouterOptions
{
    public const string DefaultModel = "qwen/qwen3.5-9b";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = DefaultModel;
    public Uri Endpoint { get; set; } = new("https://openrouter.ai/api/v1/chat/completions");
    public int ContextSize { get; set; } = 32768;
    public int MaxOutputTokens { get; set; } = 768;
    public int MissingIdRetries { get; set; } = 2;
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
            throw new InvalidOperationException("Chưa có OPENROUTER_API_KEY. API key chỉ được đọc ở server, không nhập trên trình duyệt.");
        if (string.IsNullOrWhiteSpace(Model)) throw new InvalidOperationException("Thiếu model OpenRouter.");
        if (MissingIdRetries is < 0 or > 5) throw new InvalidOperationException("MissingIdRetries phải nằm trong khoảng 0..5.");
        if (Endpoint.Scheme != Uri.UriSchemeHttps) throw new InvalidOperationException("OpenRouter endpoint bắt buộc dùng HTTPS.");
    }
    public static OpenRouterOptions FromEnvironment() => new()
    {
        ApiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY") ?? "",
        Model = Environment.GetEnvironmentVariable("OPENROUTER_MODEL") ?? DefaultModel,
    };
}

public sealed class LmStudioOptions
{
    public Uri Endpoint { get; set; } = new("http://127.0.0.1:1234/v1/chat/completions");
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";
    public int ContextSize { get; set; } = 16_384;
    public int MaxOutputTokens { get; set; } = 768;
    public int MissingIdRetries { get; set; } = 2;
    public int MaxParallelRequests { get; set; } = 1;
    public Action<string>? DebugLog { get; set; }
    public void Validate(bool requireModel = true)
    {
        if (requireModel && string.IsNullOrWhiteSpace(Model)) throw new InvalidOperationException("Chưa chọn model LM Studio. Hãy nạp model trong LM Studio hoặc đặt LMSTUDIO_MODEL.");
        if ((!Endpoint.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && !Endpoint.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) || !IsLoopback(Endpoint)) throw new InvalidOperationException("LMSTUDIO_ENDPOINT phải là địa chỉ loopback http(s)://127.0.0.1, localhost hoặc [::1].");
        if (!Endpoint.AbsolutePath.EndsWith("/v1/chat/completions", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("LMSTUDIO_ENDPOINT phải kết thúc bằng /v1/chat/completions.");
        if (ContextSize is < 4096 or > 1_048_576) throw new InvalidOperationException("LM Studio ContextSize phải nằm trong khoảng 4096..1048576.");
        if (MissingIdRetries is < 0 or > 5) throw new InvalidOperationException("LM Studio MissingIdRetries phải nằm trong khoảng 0..5.");
        if (MaxParallelRequests is < 1 or > 16) throw new InvalidOperationException("LMSTUDIO_PARALLEL phải nằm trong khoảng 1..16.");
    }
    public Uri ModelsEndpoint => new(Endpoint, "/v1/models");
    public static LmStudioOptions FromEnvironment()
    {
        var endpointText = Environment.GetEnvironmentVariable("LMSTUDIO_ENDPOINT") ?? "http://127.0.0.1:1234/v1/chat/completions";
        if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint)) throw new InvalidOperationException("LMSTUDIO_ENDPOINT không phải URI hợp lệ.");
        return new LmStudioOptions
        {
            Endpoint = endpoint,
            ApiKey = Environment.GetEnvironmentVariable("LMSTUDIO_API_KEY") ?? "",
            Model = Environment.GetEnvironmentVariable("LMSTUDIO_MODEL") ?? "",
            ContextSize = int.TryParse(Environment.GetEnvironmentVariable("LMSTUDIO_CONTEXT_SIZE"), out var context) ? context : 16_384,
            MaxParallelRequests = int.TryParse(Environment.GetEnvironmentVariable("LMSTUDIO_PARALLEL"), out var parallel) ? Math.Clamp(parallel, 1, 16) : 1,
        };
    }
    public static bool IsLoopback(Uri endpoint) => endpoint.IsLoopback || endpoint.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || IPAddress.TryParse(endpoint.Host, out var address) && IPAddress.IsLoopback(address);
}

public sealed class SglangOptions
{
    public Uri Endpoint { get; set; } = new("http://127.0.0.1:30000/v1/chat/completions");
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";
    public int ContextSize { get; set; } = 8192;
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
        if (requireModel && string.IsNullOrWhiteSpace(Model)) throw new InvalidOperationException("Chưa cấu hình SGLANG_MODEL.");
        if (Endpoint.Scheme != Uri.UriSchemeHttp && Endpoint.Scheme != Uri.UriSchemeHttps) throw new InvalidOperationException("SGLANG_ENDPOINT phải là http hoặc https.");
        if (!Endpoint.AbsolutePath.EndsWith("/v1/chat/completions", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("SGLANG_ENDPOINT phải kết thúc bằng /v1/chat/completions.");
        if (ContextSize is < 1024 or > 1_048_576) throw new InvalidOperationException("SGLANG_CONTEXT_SIZE phải nằm trong khoảng 1024..1048576.");
        if (MissingIdRetries is < 0 or > 5) throw new InvalidOperationException("SGLang MissingIdRetries phải nằm trong khoảng 0..5.");
        if (RequestTimeoutSeconds is < 10 or > 600) throw new InvalidOperationException("SGLANG_REQUEST_TIMEOUT_SECONDS phải nằm trong khoảng 10..600.");
        if (TransientRequestRetries is < 0 or > 4) throw new InvalidOperationException("SGLang TransientRequestRetries phải nằm trong khoảng 0..4.");
        if (MaxParallelRequests is < 1 or > 16) throw new InvalidOperationException("SGLANG_PARALLEL phải nằm trong khoảng 1..16.");
    }
    public static SglangOptions FromEnvironment()
    {
        var endpointText = Environment.GetEnvironmentVariable("SGLANG_ENDPOINT") ?? "http://127.0.0.1:30000/v1/chat/completions";
        if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint)) throw new InvalidOperationException("SGLANG_ENDPOINT không phải URI hợp lệ.");
        return new SglangOptions
        {
            Endpoint = endpoint,
            ApiKey = Environment.GetEnvironmentVariable("SGLANG_API_KEY") ?? "",
            Model = Environment.GetEnvironmentVariable("SGLANG_MODEL") ?? "",
            ContextSize = int.TryParse(Environment.GetEnvironmentVariable("SGLANG_CONTEXT_SIZE"), out var context) ? context : 8192,
            MaxParallelRequests = int.TryParse(Environment.GetEnvironmentVariable("SGLANG_PARALLEL"), out var parallel) ? Math.Clamp(parallel, 1, 16) : 1,
            RequestTimeoutSeconds = int.TryParse(Environment.GetEnvironmentVariable("SGLANG_REQUEST_TIMEOUT_SECONDS"), out var timeout) ? Math.Clamp(timeout, 10, 600) : 90,
            TransientRequestRetries = int.TryParse(Environment.GetEnvironmentVariable("SGLANG_TRANSIENT_RETRIES"), out var retries) ? Math.Clamp(retries, 0, 4) : 2,
        };
    }
}
