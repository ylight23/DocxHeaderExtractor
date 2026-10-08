using DocxHeaderExtractor.DocumentProcessing.Inference;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DocxHeaderExtractor.Infrastructure.AI;


/// <summary>
/// Backend OpenAI-compatible của LM Studio. Mỗi request độc lập, dùng structured output schema
/// và vẫn hậu kiểm đủ ID cục bộ. Endpoint bị khóa vào loopback để form trình duyệt không trở
/// thành SSRF proxy tới máy khác.
/// </summary>
public sealed class LmStudioInferenceTransport : IInferenceTransport
{
    private readonly HttpClient _http;
    private readonly RemoteInferenceOptions _options;
    private readonly bool _ownsHttp;

    // Giữ nguyên tiếng Việt có dấu thay vì \uXXXX — dùng cho cả body gửi đi lẫn dòng log debug.
    private static readonly JsonSerializerOptions RequestJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public LmStudioInferenceTransport(HttpClient http, RemoteInferenceOptions options)
    {
        _http = http;
        _options = Validate(options);
    }

    private LmStudioInferenceTransport(HttpClient http, RemoteInferenceOptions options, bool ownsHttp)
        : this(http, options) => _ownsHttp = ownsHttp;

    public static LmStudioInferenceTransport CreateOwned(RemoteInferenceOptions options) =>
        new(new HttpClient { Timeout = TimeSpan.FromMinutes(10) }, options, ownsHttp: true);

    public string ModelName => _options.Model;
    public int ContextSize => _options.ContextSize;
    public string RuntimeDescription => $"LM Studio local RPC · {_options.Endpoint.Authority}";
    public int SharedPrefixTokens => 0;

    /// <summary>Nhiệm vụ hẹp — xem <see cref="IInferenceTransport.BoundaryCutAsync"/>.</summary>
    public async Task<string> BoundaryCutAsync(
        string systemPrompt,
        string userMessage,
        CancellationToken ct = default,
        int expectedItemCount = 0)
    {
        var body = new
        {
            model = _options.Model,
            temperature = 0,
            top_k = 1,
            top_p = 0.9,
            repeat_penalty = 1.0,
            seed = LocalModelOptions.SharedSamplerSeed,
            reasoning_effort = "none",
            max_tokens = 120,
            stream = false,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userMessage },
            },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
        {
            Content = JsonContent.Create(body, options: RequestJson),
        };
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

        if (_options.DebugLog is { } debugLog)
            debugLog($"[LM Studio] LLM REQUEST model={_options.Model} payload={await request.Content.ReadAsStringAsync(ct)}");

        using var response = await _http.SendAsync(request, ct);
        var responseText = await response.Content.ReadAsStringAsync(ct);
        _options.DebugLog?.Invoke($"[LM Studio] LLM RESPONSE status={(int)response.StatusCode} payload={responseText}");
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"LM Studio trả {(int)response.StatusCode} {response.ReasonPhrase}: {responseText}",
                null,
                response.StatusCode);
        return ExtractContent(responseText).Trim();
    }

    private static string ExtractContent(string response)
    {
        using var doc = JsonDocument.Parse(response);
        if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0 &&
            choices[0].TryGetProperty("message", out var message) &&
            message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
        {
            var value = content.GetString() ?? "";
            if (!string.IsNullOrWhiteSpace(value)) return value;

            var finishReason = choices[0].TryGetProperty("finish_reason", out var finish)
                ? finish.GetString()
                : null;
            var reasoningChars = message.TryGetProperty("reasoning_content", out var reasoning) &&
                                 reasoning.ValueKind == JsonValueKind.String
                ? reasoning.GetString()?.Length ?? 0
                : 0;
            throw new FormatException(
                $"LM Studio trả content rỗng (finish_reason={finishReason ?? "unknown"}, " +
                $"reasoningChars={reasoningChars}). Model có thể đã dùng hết max_tokens cho reasoning; " +
                "hãy dùng model Instruct không reasoning hoặc giảm reasoning trong LM Studio.");
        }
        throw new FormatException("LM Studio response không có choices[0].message.content.");
    }

    private static RemoteInferenceOptions Validate(RemoteInferenceOptions options)
    {
        options.Validate();
        if (!RemoteInferenceOptions.IsLoopback(options.Endpoint))
            throw new InvalidOperationException("LMSTUDIO_ENDPOINT phải là địa chỉ loopback.");
        if (options.ContextSize < 4096)
            throw new InvalidOperationException("LM Studio ContextSize phải nằm trong khoảng 4096..1048576.");
        return options;
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
