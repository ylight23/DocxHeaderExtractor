using DocxHeaderExtractor.DocumentProcessing.Inference;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace DocxHeaderExtractor.Infrastructure.AI;

/// <summary>
/// RPC JSON qua OpenRouter Chat Completions. Mỗi request cấm endpoint thu thập dữ liệu để huấn
/// luyện (<c>data_collection=deny</c>) và yêu cầu provider trả JSON. Schema/ID được hậu kiểm cục
/// bộ trước khi chấp nhận.
/// <para>
/// Zero-Data-Retention do <see cref="RemoteInferenceOptions.RequireZeroDataRetention"/> quyết
/// định và LUÔN được ghi tường minh: bỏ trống trường này thì OpenRouter áp mặc định của tài
/// khoản, và tài khoản đặt ZDR sẽ loại mọi endpoint của model controlled (qwen3.7-flash chỉ có
/// endpoint Alibaba, không được chứng nhận ZDR) bằng 404 trước khi tới model. Mặc định false;
/// ràng buộc còn lại cấm provider dùng dữ liệu để huấn luyện nhưng không bảo đảm xoá sau khi trả.
/// </para>
/// </summary>
public sealed class OpenRouterHeaderExtractor : IHeaderClassifier
{
    private readonly HttpClient _http;
    private readonly RemoteInferenceOptions _options;
    private readonly bool _ownsHttp;

    public OpenRouterHeaderExtractor(HttpClient http, RemoteInferenceOptions options)
    {
        _http = http;
        _options = Validate(options);
    }

    private OpenRouterHeaderExtractor(HttpClient http, RemoteInferenceOptions options, bool ownsHttp)
        : this(http, options) => _ownsHttp = ownsHttp;

    public static OpenRouterHeaderExtractor CreateOwned(RemoteInferenceOptions options) =>
        new(new HttpClient { Timeout = TimeSpan.FromMinutes(5) }, options, ownsHttp: true);

    public string ModelName => _options.Model;
    public int ContextSize => _options.ContextSize;
    public string RuntimeDescription => "OpenRouter RPC · data_collection=deny";
    public int SharedPrefixTokens => 0;

    /// <summary>Nhiệm vụ hẹp — xem <see cref="IHeaderClassifier.BoundaryCutAsync"/>.</summary>
    public async Task<string> BoundaryCutAsync(
        string systemPrompt,
        string userMessage,
        CancellationToken ct = default,
        int expectedItemCount = 0)
    {
        // Fail here rather than at the provider. This request is about to be sent with
        // response_format json_object, and that option constrains what the messages must contain;
        // discovering the mismatch as a remote 400 costs a round trip and produces an error whose
        // cause is three layers away from the code that caused it.
        TransportCompatibility.EnsureCompatible(
            systemPrompt, userMessage, TransportCompatibility.JsonObjectResponseFormat);

        var body = new
        {
            model = _options.Model,
            temperature = 0,
            // Role/pointer passes return one JSON item per supplied source id. A fixed 120-token
            // cap truncates otherwise valid multi-block responses and turns them into invisible
            // missing decisions. Keep the result bounded by the configured model profile.
            max_tokens = BoundaryOutputBudget(userMessage, expectedItemCount),
            reasoning = new { effort = _options.OpenRouterReasoningEffort },
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userMessage },
            },
            response_format = new { type = "json_object" },
            provider = new
            {
                zdr = _options.RequireZeroDataRetention,
                data_collection = "deny",
                require_parameters = true,
                allow_fallbacks = true,
            },
        };
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(body);
        using var logical = ProviderCallTelemetry.Start(_options.Observability, new ProviderLogicalCallMetadata
        {
            Stage = "BOUNDARY_CUT",
            LogicalCallId = $"boundary-cut-{Guid.NewGuid():N}",
            RequestHash = ProviderObservabilityHashing.Sha256Bytes(payloadBytes),
            RequestBytes = payloadBytes.Length,
            EstimatedInputTokens = ProviderObservabilityHashing.EstimateTokens(systemPrompt + "\n" + userMessage),
            MaxOutputTokens = body.max_tokens,
            SourceItemCount = 1,
            ContextItemCount = 1,
            ContextCharacterCount = userMessage.Length,
            Provider = "OpenRouter",
            Model = _options.Model,
        });
        using var telemetryAttempt = logical?.StartAttempt(
            $"boundary-cut-{Guid.NewGuid():N}",
            ProviderObservabilityHashing.Sha256Bytes(payloadBytes),
            payloadBytes.Length,
            ProviderObservabilityHashing.EstimateTokens(systemPrompt + "\n" + userMessage),
            body.max_tokens,
            TimeSpan.FromSeconds(_options.RequestTimeoutSeconds));

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        request.Headers.TryAddWithoutValidation("X-Title", "DocxHeaderExtractor");
        _options.DebugLog?.Invoke($"[OpenRouter] LLM REQUEST model={_options.Model} payload={JsonSerializer.Serialize(body)}");

        using var attemptDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attemptDeadline.CancelAfter(TimeSpan.FromSeconds(_options.RequestTimeoutSeconds));
        using var timeoutTelemetry = attemptDeadline.Token.Register(() =>
        {
            if (!ct.IsCancellationRequested)
                telemetryAttempt?.Fail("ATTEMPT_TIMEOUT", new { timeoutSeconds = _options.RequestTimeoutSeconds });
        });

        using var response = await _http.SendAsync(request, attemptDeadline.Token);
        telemetryAttempt?.Event("RESPONSE_HEADERS_RECEIVED", new { status = (int)response.StatusCode });
        telemetryAttempt?.Event("FIRST_RESPONSE_BYTE", new { observable = false, note = "ReadAsStringAsync is the current transport boundary." });
        var responseText = await response.Content.ReadAsStringAsync(attemptDeadline.Token);
        telemetryAttempt?.PersistRawResponse(responseText);
        telemetryAttempt?.Event("RESPONSE_BODY_COMPLETE", new { responseBytes = Encoding.UTF8.GetByteCount(responseText), responseHash = ProviderObservabilityHashing.Sha256Utf8(responseText) });
        _options.DebugLog?.Invoke(
            $"[OpenRouter] LLM RESPONSE status={(int)response.StatusCode} payload={SafeDebug(responseText)}");
        if (!response.IsSuccessStatusCode)
        {
            telemetryAttempt?.Fail("HTTP_ERROR", new { status = (int)response.StatusCode });
            throw new HttpRequestException(
                $"OpenRouter trả {(int)response.StatusCode} {response.ReasonPhrase}: {SafeError(responseText)}",
                null,
                response.StatusCode);
        }
        telemetryAttempt?.Event("PARSE_STARTED", new { responseBytes = Encoding.UTF8.GetByteCount(responseText) });
        var content = ExtractContent(responseText).Trim();
        telemetryAttempt?.PersistParsed(new { content });
        telemetryAttempt?.Event("PARSE_COMPLETED", new { resultCharacters = content.Length });
        telemetryAttempt?.Complete();
        logical?.Complete(new { resultCharacters = content.Length });
        return content;
    }

    private static string ExtractContent(string response)
    {
        using var doc = JsonDocument.Parse(response);
        if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0 &&
            choices[0].TryGetProperty("message", out var message) &&
            message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? "";
        throw new FormatException("OpenRouter response không có choices[0].message.content.");
    }

    private int BoundaryOutputBudget(string userMessage, int expectedItemCount) =>
        BoundaryOutputBudgetFor(userMessage, expectedItemCount, _options.MaxOutputTokens);

    /// <summary>
    /// The output budget this route would send for a given call, as a function rather than a value
    /// buried in a request body.
    /// <para>
    /// It is exposed so an experiment can freeze the transport parameters it intends to send and
    /// assert them against the production formula. Request-byte parity is not enough to prove two
    /// calls are the same call: max_tokens is derived from an argument that appears nowhere in
    /// those bytes, and a run that omitted it once clamped a 119-item request to the 256-token
    /// floor and truncated the reply.
    /// </para>
    /// </summary>
    internal static int BoundaryOutputBudgetFor(
        string userMessage, int expectedItemCount, int maxOutputTokens)
    {
        // The budget scales with how many source items the request asks about. Both identifier
        // spellings must be counted: the legacy boundary protocol names them "id", the canonical
        // semantic contract names them "sourceAlias". Counting only "id" silently clamped the
        // canonical route to the 256-token floor and truncated its JSON mid-object.
        // A caller that knows how many items it asks about states it. Only fall back to sniffing
        // the payload when it does not: that inference reads a field name, so renaming a field in
        // the request silently collapsed the budget to its floor and truncated the reply.
        // A canonical semantic item answers with verbatim text, role, type, scope and relation
        // hints, so it needs far more than a legacy boundary item's couple of integers.
        if (expectedItemCount > 0)
            return Math.Clamp(96 + expectedItemCount * 128, 256, maxOutputTokens);
        var legacy = CountOccurrences(userMessage, "\"id\"", StringComparison.Ordinal);
        return Math.Clamp(96 + legacy * 64, 256, maxOutputTokens);
    }

    private static int CountOccurrences(string text, string token, StringComparison comparison)
    {
        var count = 0;
        var offset = 0;
        while ((offset = text.IndexOf(token, offset, comparison)) >= 0)
        {
            count++;
            offset += token.Length;
        }
        return count;
    }

    private static string SafeError(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "không có nội dung lỗi";
        var oneLine = text.ReplaceLineEndings(" ").Trim();
        return oneLine.Length <= 500 ? oneLine : oneLine[..500] + "…";
    }

    private static string SafeDebug(string text)
    {
        var oneLine = string.IsNullOrWhiteSpace(text) ? "<empty>" : text.ReplaceLineEndings(" ").Trim();
        return oneLine.Length <= 16_000 ? oneLine : oneLine[..16_000] + "…[truncated]";
    }

    private static RemoteInferenceOptions Validate(RemoteInferenceOptions options)
    {
        options.Validate();
        if (string.IsNullOrWhiteSpace(options.ApiKey))
            throw new InvalidOperationException("Chưa có OPENROUTER_API_KEY. API key chỉ được đọc ở server, không nhập trên trình duyệt.");
        if (!options.Endpoint.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("OpenRouter endpoint bắt buộc dùng HTTPS.");
        return options;
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
