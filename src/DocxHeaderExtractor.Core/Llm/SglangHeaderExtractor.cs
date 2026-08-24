using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DocxHeaderExtractor.Core.Llm;

public sealed class SglangOptions
{
    public Uri Endpoint { get; set; } = new("http://127.0.0.1:30000/v1/chat/completions");
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";

    /// <summary>
    /// Chưa đo context thật của model/gateway này (Qwen3.8-27B qua vLLM). Đặt bảo thủ; server tự
    /// khai context lớn hơn thì <c>AdoptBackendContextBudget</c> chỉ NÂNG chunk budget, không hạ.
    /// </summary>
    public int ContextSize { get; set; } = 8192;

    public int MaxOutputTokens { get; set; } = 768;
    public int MissingIdRetries { get; set; } = 2;
    public int RequestTimeoutSeconds { get; set; } = 90;
    public int TransientRequestRetries { get; set; } = 2;

    /// <summary>
    /// Chưa đối chiếu song song-vs-tuần tự trên đúng gateway này (khác máy LM Studio đã đo ở
    /// <see cref="LmStudioOptions.MaxParallelRequests"/>). Mặc định 1; đặt SGLANG_PARALLEL sau khi
    /// tự đối chiếu output.
    /// </summary>
    public int MaxParallelRequests { get; set; } = 1;

    /// <summary>
    /// SGLang accepts a Qwen-specific thinking switch. Hosted OpenAI-compatible providers do not
    /// necessarily recognize that field, so adapters must disable it rather than relying on the
    /// server to ignore unknown JSON properties.
    /// </summary>
    public bool SendChatTemplateKwargs { get; set; } = true;

    /// <summary>Ask compatible hosted APIs to return a JSON object for narrow pointer/role passes.</summary>
    public bool RequireJsonObjectResponse { get; set; }

    /// <summary>Hook debug cục bộ để hiển thị request trước khi gửi.</summary>
    public Action<string>? DebugLog { get; set; }

    public void Validate(bool requireModel = true)
    {
        if (requireModel && string.IsNullOrWhiteSpace(Model))
            throw new InvalidOperationException("Chưa cấu hình SGLANG_MODEL.");
        if (Endpoint.Scheme != Uri.UriSchemeHttp && Endpoint.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("SGLANG_ENDPOINT phải là http hoặc https.");
        if (!Endpoint.AbsolutePath.EndsWith("/v1/chat/completions", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("SGLANG_ENDPOINT phải kết thúc bằng /v1/chat/completions.");
        if (ContextSize is < 1024 or > 1_048_576)
            throw new InvalidOperationException("SGLANG_CONTEXT_SIZE phải nằm trong khoảng 1024..1048576.");
        if (MissingIdRetries is < 0 or > 5)
            throw new InvalidOperationException("SGLang MissingIdRetries phải nằm trong khoảng 0..5.");
        if (RequestTimeoutSeconds is < 10 or > 600)
            throw new InvalidOperationException("SGLANG_REQUEST_TIMEOUT_SECONDS phải nằm trong khoảng 10..600.");
        if (TransientRequestRetries is < 0 or > 4)
            throw new InvalidOperationException("SGLANG_TRANSIENT_RETRIES phải nằm trong khoảng 0..4.");
        if (MaxParallelRequests is < 1 or > 16)
            throw new InvalidOperationException("SGLANG_PARALLEL phải nằm trong khoảng 1..16.");
    }

    public static SglangOptions FromEnvironment()
    {
        var endpointText = Environment.GetEnvironmentVariable("SGLANG_ENDPOINT")
            ?? "http://127.0.0.1:30000/v1/chat/completions";
        if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint))
            throw new InvalidOperationException("SGLANG_ENDPOINT không phải URI hợp lệ.");

        return new SglangOptions
        {
            Endpoint = endpoint,
            ApiKey = Environment.GetEnvironmentVariable("SGLANG_API_KEY") ?? "",
            Model = Environment.GetEnvironmentVariable("SGLANG_MODEL") ?? "",
            ContextSize = int.TryParse(Environment.GetEnvironmentVariable("SGLANG_CONTEXT_SIZE"), out var context)
                ? context
                : 8192,
            MaxParallelRequests = int.TryParse(Environment.GetEnvironmentVariable("SGLANG_PARALLEL"), out var parallel)
                ? Math.Clamp(parallel, 1, 16)
                : 1,
            RequestTimeoutSeconds = int.TryParse(Environment.GetEnvironmentVariable("SGLANG_REQUEST_TIMEOUT_SECONDS"), out var timeout)
                ? Math.Clamp(timeout, 10, 600)
                : 90,
            TransientRequestRetries = int.TryParse(Environment.GetEnvironmentVariable("SGLANG_TRANSIENT_RETRIES"), out var retries)
                ? Math.Clamp(retries, 0, 4)
                : 2,
        };
    }
}

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
    private readonly SglangOptions _options;
    private readonly bool _ownsHttp;

    // Giữ nguyên tiếng Việt có dấu thay vì \uXXXX trong body gửi đi lẫn dòng log debug.
    private static readonly JsonSerializerOptions RequestJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public SglangHeaderExtractor(HttpClient http, SglangOptions options)
    {
        _http = http;
        _options = Validate(options);
    }

    private SglangHeaderExtractor(HttpClient http, SglangOptions options, bool ownsHttp)
        : this(http, options) => _ownsHttp = ownsHttp;

    public static SglangHeaderExtractor CreateOwned(SglangOptions options) =>
        new(new HttpClient { Timeout = TimeSpan.FromMinutes(10) }, options, ownsHttp: true);

    public string ModelName => _options.Model;
    public int ContextSize => _options.ContextSize;
    public string RuntimeDescription => $"SGLang/vLLM gateway RPC · {_options.Endpoint.Authority}";
    public int SharedPrefixTokens => 0;

    public Task<ChunkResult> ClassifyAsync(
        string documentView,
        IReadOnlyList<int> allowedIndexes,
        CancellationToken ct = default) =>
        SendAsync(HeaderPrompt.System, HeaderPrompt.BuildUser(documentView), allowedIndexes, roles: true, ct);

    public Task<ChunkResult> CritiqueAsync(
        string documentView,
        IReadOnlyList<int> allowedIndexes,
        CancellationToken ct = default) =>
        SendAsync(HeaderPrompt.CriticSystem, HeaderPrompt.BuildCriticUser(documentView), allowedIndexes, roles: true, ct);

    public Task<ChunkResult> ClassifyHierarchyAsync(
        IReadOnlyList<HierarchyItem> context,
        IReadOnlyList<HierarchyItem> headings,
        CancellationToken ct = default) =>
        SendAsync(
            HeaderPrompt.HierarchySystem,
            HeaderPrompt.BuildHierarchyUser(context, headings),
            headings.Select(h => h.Index).ToArray(),
            roles: false,
            ct);

    private async Task<ChunkResult> SendAsync(
        string system,
        string user,
        IReadOnlyList<int> allowedIndexes,
        bool roles,
        CancellationToken ct)
    {
        if (allowedIndexes.Count == 0)
            return new ChunkResult([], "{\"items\":[]}", 0, 0, new HashSet<int>());

        var allAllowed = allowedIndexes.Distinct().ToArray();
        var allowed = allAllowed.ToHashSet();
        var remaining = allAllowed.ToList();
        var seen = new HashSet<int>();
        var kept = new Dictionary<int, ModelHeading>();
        var explicitNonHeadings = new HashSet<int>();
        var rejectedRoles = new Dictionary<int, SemanticRole>();
        var rawOutputs = new List<string>();
        var rejected = 0;
        long elapsedMs = 0;

        for (var attempt = 0; remaining.Count > 0 && attempt <= _options.MissingIdRetries; attempt++)
        {
            var requiredIds = string.Join(',', remaining);
            var constrainedUser = user + (attempt == 0 ? "" :
                $"\n\nLƯỢT SỬA {attempt}: chỉ trả quyết định cho các ID còn thiếu [{requiredIds}].") +
                $"\n\nOUTPUT: items phải có đúng {remaining.Count} phần tử theo thứ tự ID [{requiredIds}].";

            var body = new Dictionary<string, object?>
            {
                ["model"] = _options.Model,
                ["temperature"] = 0,
                ["max_tokens"] = Math.Min(_options.MaxOutputTokens, remaining.Count * (roles ? 64 : 32) + 128),
                ["stream"] = false,
                ["messages"] = new[]
                {
                    new { role = "system", content = system },
                    new { role = "user", content = constrainedUser },
                },
                ["response_format"] = BuildResponseFormat(remaining, roles),
            };
            if (_options.SendChatTemplateKwargs)
                body["chat_template_kwargs"] = new { enable_thinking = false };

            _options.DebugLog?.Invoke($"→ SGLang request: {JsonSerializer.Serialize(body, RequestJson)}");

            using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
            {
                Content = JsonContent.Create(body, options: RequestJson),
            };
            if (!string.IsNullOrWhiteSpace(_options.ApiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

            var sw = Stopwatch.StartNew();
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var responseText = await response.Content.ReadAsStringAsync(ct);
            sw.Stop();
            elapsedMs += sw.ElapsedMilliseconds;

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(
                    $"SGLang gateway trả {(int)response.StatusCode} {response.ReasonPhrase}: {Safe(responseText, 500)}",
                    null,
                    response.StatusCode);

            var raw = ExtractContent(responseText);
            rawOutputs.Add(raw);
            var parsed = ModelJson.Parse(raw, includeNonHeadings: true);
            var requestedThisAttempt = remaining.ToHashSet();

            foreach (var decision in parsed)
            {
                if (!allowed.Contains(decision.Index) || !requestedThisAttempt.Contains(decision.Index))
                {
                    rejected++;
                    continue;
                }
                if (!seen.Add(decision.Index)) continue;
                if ((roles && decision.Role != SemanticRole.Heading) || decision.Level <= 0)
                {
                    if (decision.Role != SemanticRole.Uncertain)
                    {
                        explicitNonHeadings.Add(decision.Index);
                        rejectedRoles[decision.Index] = decision.Role;
                    }
                    continue;
                }
                decision.Level = Math.Clamp(decision.Level, 1, 9);
                kept[decision.Index] = decision;
            }

            remaining = allAllowed.Where(i => !seen.Contains(i)).ToList();
        }

        if (remaining.Count > 0)
            throw new FormatException(
                $"SGLang gateway trả {seen.Count}/{allowed.Count} quyết định hợp lệ; " +
                $"ID còn thiếu=[{string.Join(',', remaining)}] sau {_options.MissingIdRetries + 1} lượt; " +
                $"output={Safe(string.Join(" | ", rawOutputs), 800)}");

        return new ChunkResult(
            allAllowed.Where(kept.ContainsKey).Select(i => kept[i]).ToArray(),
            string.Join(Environment.NewLine, rawOutputs),
            rejected,
            elapsedMs,
            explicitNonHeadings,
            rejectedRoles);
    }

    private static object BuildResponseFormat(IReadOnlyCollection<int> indexes, bool roles)
    {
        var required = roles ? new[] { "i", "r", "l" } : new[] { "i", "l" };
        object itemSchema;
        if (!roles)
        {
            itemSchema = new
            {
                type = "object",
                properties = new
                {
                    i = new { type = "integer", @enum = indexes },
                    l = new { type = "integer", minimum = 1, maximum = 9 },
                },
                required,
                additionalProperties = false,
            };
        }
        else
        {
            // JSON Schema không biểu diễn được quan hệ r→l bằng minimum chung.
            // Tách hai nhánh để model không thể trả r=h,l=0 (hoặc role khác với l>0).
            itemSchema = new
            {
                oneOf = new object[]
                {
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            i = new { type = "integer", @enum = indexes },
                            r = new { type = "string", @enum = new[] { "h" } },
                            l = new { type = "integer", minimum = 1, maximum = 9 },
                        },
                        required,
                        additionalProperties = false,
                    },
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            i = new { type = "integer", @enum = indexes },
                            r = new { type = "string", @enum = new[] { "d", "t", "f", "s", "c", "n", "u" } },
                            l = new { type = "integer", @enum = new[] { 0 } },
                        },
                        required,
                        additionalProperties = false,
                    },
                },
            };
        }

        return new
        {
            type = "json_schema",
            json_schema = new
            {
                name = roles ? "heading_roles" : "heading_levels",
                strict = true,
                schema = new
                {
                    type = "object",
                    properties = new
                    {
                        items = new
                        {
                            type = "array",
                            minItems = indexes.Count,
                            maxItems = indexes.Count,
                            items = itemSchema,
                        },
                    },
                    required = new[] { "items" },
                    additionalProperties = false,
                },
            },
        };
    }

    /// <summary>Nhiệm vụ hẹp — xem <see cref="IHeaderClassifier.BoundaryCutAsync"/>.</summary>
    public async Task<string> BoundaryCutAsync(string systemPrompt, string userMessage, CancellationToken ct = default)
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

    private static SglangOptions Validate(SglangOptions options)
    {
        options.Validate();
        return options;
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
