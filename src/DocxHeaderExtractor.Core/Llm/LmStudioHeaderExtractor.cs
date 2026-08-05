using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DocxHeaderExtractor.Core.Llm;

public sealed class LmStudioOptions
{
    public Uri Endpoint { get; set; } = new("http://127.0.0.1:1234/v1/chat/completions");
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";
    public int ContextSize { get; set; } = 16_384;
    public int MaxOutputTokens { get; set; } = 768;
    public int MissingIdRetries { get; set; } = 2;

    /// <summary>
    /// Số khối gửi song song. Mỗi khối là một request /v1/chat/completions độc lập, không có state
    /// phía server, nên nội dung TỪNG request không đổi khi tăng số này.
    /// <para>
    /// MẶC ĐỊNH 1 dù đường đi song song đã có sẵn. Đo trên máy này: LM Studio xử lý chồng lấn thật
    /// (khối 3 xong ở 260 s trong khi khối 1 mất 325 s — xếp hàng FIFO thì không thể vậy), và
    /// continuous batching có thể làm lệch số học của chính lượt suy luận đó. Chưa có lần đối chiếu
    /// tuần tự-vs-song song nào chạy trọn vẹn để khẳng định kết quả trùng khít, nên bật sẵn là đánh
    /// cược vào điều chưa đo. Ngoài ra độ trễ mỗi request phình theo số slot (354 s → 569 s) trong
    /// khi HttpClient chỉ chờ 10 phút.
    /// </para>
    /// Đặt LMSTUDIO_PARALLEL=N sau khi đã tự đối chiếu output trên đúng model của mình.
    /// </summary>
    public int MaxParallelRequests { get; set; } = 1;

    /// <summary>Hook debug cục bộ để hiển thị request trước khi gửi tới LM Studio.</summary>
    public Action<string>? DebugLog { get; set; }

    public void Validate(bool requireModel = true)
    {
        if (requireModel && string.IsNullOrWhiteSpace(Model))
            throw new InvalidOperationException(
                "Chưa chọn model LM Studio. Hãy nạp model trong LM Studio hoặc đặt LMSTUDIO_MODEL.");
        if ((!Endpoint.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !Endpoint.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) ||
            !IsLoopback(Endpoint))
            throw new InvalidOperationException(
                "LMSTUDIO_ENDPOINT phải là địa chỉ loopback http(s)://127.0.0.1, localhost hoặc [::1].");
        if (!Endpoint.AbsolutePath.EndsWith("/v1/chat/completions", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("LMSTUDIO_ENDPOINT phải kết thúc bằng /v1/chat/completions.");
        if (ContextSize is < 4096 or > 1_048_576)
            throw new InvalidOperationException("LM Studio ContextSize phải nằm trong khoảng 4096..1048576.");
        if (MissingIdRetries is < 0 or > 5)
            throw new InvalidOperationException("LM Studio MissingIdRetries phải nằm trong khoảng 0..5.");
        if (MaxParallelRequests is < 1 or > 16)
            throw new InvalidOperationException("LMSTUDIO_PARALLEL phải nằm trong khoảng 1..16.");
    }

    public Uri ModelsEndpoint => new(Endpoint, "/v1/models");

    public static LmStudioOptions FromEnvironment()
    {
        var endpointText = Environment.GetEnvironmentVariable("LMSTUDIO_ENDPOINT")
            ?? "http://127.0.0.1:1234/v1/chat/completions";
        if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint))
            throw new InvalidOperationException("LMSTUDIO_ENDPOINT không phải URI hợp lệ.");

        return new LmStudioOptions
        {
            Endpoint = endpoint,
            ApiKey = Environment.GetEnvironmentVariable("LMSTUDIO_API_KEY") ?? "",
            Model = Environment.GetEnvironmentVariable("LMSTUDIO_MODEL") ?? "",
            ContextSize = int.TryParse(Environment.GetEnvironmentVariable("LMSTUDIO_CONTEXT_SIZE"), out var context)
                ? context
                : 16_384,
            MaxParallelRequests = int.TryParse(Environment.GetEnvironmentVariable("LMSTUDIO_PARALLEL"), out var parallel)
                ? Math.Clamp(parallel, 1, 16)
                : 1,
        };
    }

    public static bool IsLoopback(Uri endpoint) =>
        endpoint.IsLoopback ||
        endpoint.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        IPAddress.TryParse(endpoint.Host, out var address) && IPAddress.IsLoopback(address);
}

/// <summary>
/// Backend OpenAI-compatible của LM Studio. Mỗi request độc lập, dùng structured output schema
/// và vẫn hậu kiểm đủ ID cục bộ. Endpoint bị khóa vào loopback để form trình duyệt không trở
/// thành SSRF proxy tới máy khác.
/// </summary>
public sealed class LmStudioHeaderExtractor : IHeaderClassifier
{
    private readonly HttpClient _http;
    private readonly LmStudioOptions _options;
    private readonly bool _ownsHttp;

    // Giữ nguyên tiếng Việt có dấu thay vì \uXXXX — dùng cho cả body gửi đi lẫn dòng log debug.
    private static readonly JsonSerializerOptions RequestJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public LmStudioHeaderExtractor(HttpClient http, LmStudioOptions options)
    {
        _http = http;
        _options = Validate(options);
    }

    private LmStudioHeaderExtractor(HttpClient http, LmStudioOptions options, bool ownsHttp)
        : this(http, options) => _ownsHttp = ownsHttp;

    public static LmStudioHeaderExtractor CreateOwned(LmStudioOptions options) =>
        new(new HttpClient { Timeout = TimeSpan.FromMinutes(10) }, options, ownsHttp: true);

    public string ModelName => _options.Model;
    public int ContextSize => _options.ContextSize;
    public string RuntimeDescription => $"LM Studio local RPC · {_options.Endpoint.Authority}";
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

            var body = new
            {
                model = _options.Model,
                temperature = 0,
                // Bộ sampler khai báo tường minh, không phó mặc mặc định của LM Studio. Hai lý do:
                // (1) mặc định là cấu hình của một phần mềm bên ngoài, đổi theo phiên bản hoặc theo
                // preset người dùng chọn trong GUI — kết quả vì thế không tái lập được; (2) nó phải
                // trùng bộ sampler mà backend GGUF local đang dùng (TopK=1, TopP=0.9,
                // RepeatPenalty=1.0, Seed=1234 trong LlamaHeaderExtractor), nếu không thì mọi so
                // sánh local-vs-LM Studio đang so hai thứ khác nhau chứ không phải hai backend.
                top_k = 1,
                top_p = 0.9,
                repeat_penalty = 1.0,
                seed = LlamaOptions.SharedSamplerSeed,
                // Heading classification is a structured extraction task. LM Studio's
                // reasoning models can spend the whole small output budget on hidden
                // reasoning and leave message.content empty; disable that channel so
                // the JSON response has deterministic budget for the schema.
                reasoning_effort = "none",
                max_tokens = Math.Min(_options.MaxOutputTokens, remaining.Count * (roles ? 64 : 32) + 128),
                stream = false,
                messages = new[]
                {
                    new { role = "system", content = system },
                    new { role = "user", content = constrainedUser },
                },
                response_format = BuildResponseFormat(remaining, roles),
            };

            _options.DebugLog?.Invoke($"→ LM Studio request: {JsonSerializer.Serialize(body, RequestJson)}");

            using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
            {
                // Cùng bộ option với dòng log ở trên: encoder mặc định escape mọi ký tự ngoài ASCII
                // thành \uXXXX, nên prompt tiếng Việt trong dev log đọc thành rác (trông như lỗi
                // font). Trên dây thì cả hai cách đều là JSON hợp lệ, chỉ khác kích thước body.
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
                    $"LM Studio trả {(int)response.StatusCode} {response.ReasonPhrase}: {Safe(responseText, 500)}",
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
                $"LM Studio trả {seen.Count}/{allowed.Count} quyết định hợp lệ; " +
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

    private static string Safe(string text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return "<rỗng>";
        var oneLine = text.ReplaceLineEndings(" ").Trim();
        return oneLine.Length <= max ? oneLine : oneLine[..max] + "…";
    }

    private static LmStudioOptions Validate(LmStudioOptions options)
    {
        options.Validate();
        return options;
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
