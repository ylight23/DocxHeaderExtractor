using DocxHeaderExtractor.DocumentProcessing.Inference;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DocxHeaderExtractor.Infrastructure.AI;


/// <summary>
/// Backend OpenAI-compatible cho gateway SGLang/vLLM tự host (đo tay 2026-08-19: endpoint
/// <c>/v1/chat/completions</c>, model trả về qua <c>vllm/&lt;tên&gt;</c> hoặc thẳng tên, đều
/// resolve được). Hai điểm khác LM Studio, cả hai đều đã đo trên đúng gateway này:
/// <list type="bullet">
/// <item>Model Qwen3 mặc định bật "thinking" — reasoning ăn hết ngân sách <c>max_tokens</c> và cắt
/// cụt content (đo: max_tokens=100 → content dừng giữa chừng "…HUY ĐỘNG VỐN QU"). Tắt bằng
/// <c>chat_template_kwargs.enable_thinking=false</c> thì content ra đủ ngay, nhanh hơn hẳn.</item>
/// <item>Endpoint không khoá loopback: đây là gateway LAN cố ý, không phải app desktop cùng máy.
/// Giá trị Endpoint luôn đọc từ biến môi trường phía server, không nhận từ form trình duyệt, nên
/// không mở thêm đường SSRF nào so với hai backend RPC còn lại.</item>
/// </list>
/// response_format json_schema strict đã đo hoạt động đúng trên gateway này (id/level đúng schema).
/// </summary>
public sealed class SglangHeaderExtractor : IHeaderClassifier
{
    private readonly HttpClient _http;
    private readonly RemoteInferenceOptions _options;
    private readonly bool _ownsHttp;

    // Giữ nguyên tiếng Việt có dấu thay vì \uXXXX trong body gửi đi lẫn dòng log debug.
    private static readonly JsonSerializerOptions RequestJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public SglangHeaderExtractor(HttpClient http, RemoteInferenceOptions options)
    {
        _http = http;
        _options = Validate(options);
    }

    private SglangHeaderExtractor(HttpClient http, RemoteInferenceOptions options, bool ownsHttp)
        : this(http, options) => _ownsHttp = ownsHttp;

    public static SglangHeaderExtractor CreateOwned(RemoteInferenceOptions options) =>
        new(new HttpClient { Timeout = TimeSpan.FromMinutes(10) }, options, ownsHttp: true);

    public string ModelName => _options.Model;
    public int ContextSize => _options.ContextSize;
    public string RuntimeDescription => $"SGLang/vLLM gateway RPC · {_options.Endpoint.Authority}";
    public int SharedPrefixTokens => 0;

    /// <summary>Nhiệm vụ hẹp — xem <see cref="IHeaderClassifier.BoundaryCutAsync"/>.</summary>
    public async Task<string> BoundaryCutAsync(
        string systemPrompt,
        string userMessage,
        CancellationToken ct = default,
        int expectedItemCount = 0)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = _options.Model,
            ["temperature"] = 0,
            ["max_tokens"] = _options.MaxOutputTokens,
            ["stream"] = false,
            ["messages"] = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userMessage },
            },
        };
        if (_options.SendChatTemplateKwargs)
            body["chat_template_kwargs"] = new { enable_thinking = false };
        if (_options.RequireJsonObjectResponse)
            body["response_format"] = new { type = "json_object" };

        Exception? lastError = null;
        for (var attempt = 0; attempt <= _options.TransientRequestRetries; attempt++)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(_options.RequestTimeoutSeconds));
                using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
                {
                    Content = JsonContent.Create(body, options: RequestJson),
                };
                if (!string.IsNullOrWhiteSpace(_options.ApiKey))
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                var responseText = await response.Content.ReadAsStringAsync(timeout.Token);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException(
                        $"SGLang gateway trả {(int)response.StatusCode} {response.ReasonPhrase}: {Safe(responseText, 500)}",
                        null, response.StatusCode);
                return ExtractContent(responseText).Trim();
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                lastError = new TimeoutException($"LLM request timed out after {_options.RequestTimeoutSeconds}s.");
            }
            catch (HttpRequestException error) when (IsTransient(error))
            {
                lastError = error;
            }

            if (attempt < _options.TransientRequestRetries)
                await Task.Delay(TimeSpan.FromMilliseconds(400 * (attempt + 1)), ct);
        }
        throw new HttpRequestException($"LLM request failed after {_options.TransientRequestRetries + 1} attempts: {lastError?.Message}", lastError);
    }

    private static bool IsTransient(HttpRequestException error) => error.StatusCode is null or
        System.Net.HttpStatusCode.RequestTimeout or System.Net.HttpStatusCode.TooManyRequests or
        System.Net.HttpStatusCode.BadGateway or System.Net.HttpStatusCode.ServiceUnavailable or System.Net.HttpStatusCode.GatewayTimeout;

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
            // Gateway này để reasoning ẩn dưới "reasoning" (đo 2026-08-19); giữ thêm
            // "reasoning_content" vì đó là tên field OpenAI-compatible phổ biến hơn ở nơi khác.
            var reasoningChars = ReasoningLength(message, "reasoning") + ReasoningLength(message, "reasoning_content");
            throw new FormatException(
                $"SGLang gateway trả content rỗng (finish_reason={finishReason ?? "unknown"}, " +
                $"reasoningChars={reasoningChars}). chat_template_kwargs.enable_thinking=false đã bật " +
                "sẵn; nếu vẫn rỗng thì model/gateway không tôn trọng cờ này — cần tăng max_tokens.");
        }
        throw new FormatException("SGLang gateway response không có choices[0].message.content.");
    }

    private static int ReasoningLength(JsonElement message, string propertyName) =>
        message.TryGetProperty(propertyName, out var reasoning) && reasoning.ValueKind == JsonValueKind.String
            ? reasoning.GetString()?.Length ?? 0
            : 0;

    private static string Safe(string text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return "<rỗng>";
        var oneLine = text.ReplaceLineEndings(" ").Trim();
        return oneLine.Length <= max ? oneLine : oneLine[..max] + "…";
    }

    private static RemoteInferenceOptions Validate(RemoteInferenceOptions options)
    {
        options.Validate();
        return options;
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}

