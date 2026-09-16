using DocxHeaderExtractor.DocumentProcessing.Inference;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace DocxHeaderExtractor.Infrastructure.AI;

/// <summary>
/// RPC JSON qua OpenRouter Chat Completions. Mỗi request bắt buộc ZDR, cấm endpoint thu thập
/// dữ liệu và yêu cầu provider trả JSON. Schema/ID được hậu kiểm cục bộ trước khi chấp nhận.
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
    public string RuntimeDescription => "OpenRouter RPC · ZDR · data_collection=deny";
    public int SharedPrefixTokens => 0;

    public Task<ChunkResult> ClassifyAsync(
        string chunkXml,
        IReadOnlyList<int> allowedIndexes,
        CancellationToken ct = default) =>
        SendAsync("CLASSIFY", HeaderPrompt.System, HeaderPrompt.BuildUser(chunkXml), allowedIndexes, roles: true, ct);

    public Task<ChunkResult> CritiqueAsync(
        string chunkXml,
        IReadOnlyList<int> allowedIndexes,
        CancellationToken ct = default) =>
        SendAsync("CRITIQUE", HeaderPrompt.CriticSystem, HeaderPrompt.BuildCriticUser(chunkXml), allowedIndexes, roles: true, ct);

    public Task<ChunkResult> ClassifyHierarchyAsync(
        IReadOnlyList<HierarchyItem> context,
        IReadOnlyList<HierarchyItem> headings,
        CancellationToken ct = default) =>
        SendAsync(
            "HIERARCHY",
            HeaderPrompt.HierarchySystem,
            HeaderPrompt.BuildHierarchyUser(context, headings),
            headings.Select(h => h.Index).ToArray(),
            roles: false,
            ct);

    private async Task<ChunkResult> SendAsync(
        string stage,
        string system,
        string user,
        IReadOnlyList<int> allowedIndexes,
        bool roles,
        CancellationToken ct)
    {
        if (allowedIndexes.Count == 0)
            return new ChunkResult([], "{\"h\":[]}", 0, 0, new HashSet<int>());

        var allAllowed = allowedIndexes.Distinct().ToArray();
        var allowed = allAllowed.ToHashSet();
        var remaining = allAllowed.ToList();
        var seen = new HashSet<int>();
        var kept = new Dictionary<int, HeadingClassificationProposal>();
        var explicitNonHeadings = new HashSet<int>();
        var rejectedRoles = new Dictionary<int, SemanticRole>();
        var rawOutputs = new List<string>();
        var rejected = 0;
        long elapsedMs = 0;
        var firstShape = system + "\n" + user + "\n" + string.Join(',', allAllowed);
        using var logical = ProviderCallTelemetry.Start(_options.Observability, new ProviderLogicalCallMetadata
        {
            Stage = stage,
            LogicalCallId = $"{stage.ToLowerInvariant()}-{Guid.NewGuid():N}",
            RequestHash = ProviderObservabilityHashing.Sha256Utf8(firstShape),
            RequestBytes = Encoding.UTF8.GetByteCount(firstShape),
            EstimatedInputTokens = ProviderObservabilityHashing.EstimateTokens(firstShape),
            MaxOutputTokens = _options.MaxOutputTokens,
            CandidateCount = allAllowed.Length,
            CurrentNodeCount = headingsCount(roles, user),
            ContextItemCount = roles ? 0 : allAllowed.Length,
            ContextCharacterCount = user.Length,
            Provider = "OpenRouter",
            Model = _options.Model,
        });

        for (var attempt = 0; remaining.Count > 0 && attempt <= _options.MissingIdRetries; attempt++)
        {
            var requiredIds = string.Join(',', remaining);
            var constrainedUser = user + (attempt == 0 ? "" :
                $"\n\nLƯỢT SỬA {attempt}: câu trả lời trước đã bỏ thiếu ID. Chỉ trả quyết định cho các ID còn thiếu [{requiredIds}].") +
                (roles
                    ? $"\n\nRÀNG BUỘC OUTPUT GHI ĐÈ VÍ DỤ TRÊN: root chỉ có duy nhất key items. " +
                      $"Không tạo các mảng h,d,t,f,s,c,n,u riêng. Mảng items BẮT BUỘC có đúng {remaining.Count} phần tử, mỗi phần tử chỉ dùng keys i,r,l; " +
                      $"giá trị i theo đúng thứ tự là [{requiredIds}]. Không đánh số lại từ 0 hoặc 1. " +
                      "r chỉ là một trong h,d,t,f,s,c,n,u; r=h thì l=1..9, ngược lại l=0. Dạng chính xác: {\"items\":[...]}"
                    : $"\n\nRÀNG BUỘC OUTPUT GHI ĐÈ VÍ DỤ TRÊN: root chỉ có duy nhất key items. Mảng items BẮT BUỘC có đúng {remaining.Count} phần tử, mỗi phần tử chỉ dùng keys i,l; " +
                      $"giá trị i theo đúng thứ tự là [{requiredIds}]. Không đánh số lại từ 0 hoặc 1; l=1..9.");

            var body = new
            {
                model = _options.Model,
                temperature = 0,
                max_tokens = Math.Min(_options.MaxOutputTokens, remaining.Count * (roles ? 64 : 32) + 128),
                // Qwen3.5 otherwise consumes the compact structured-output budget with hidden
                // reasoning and can terminate with content=null. We require a bounded JSON
                // contract whose evidence is grounded locally, not a chain of thought.
                reasoning = new { effort = "none" },
                messages = new[]
                {
                    new { role = "system", content = system },
                    new { role = "user", content = constrainedUser },
                },
                // Qwen 2.5 7B công bố response_format nhưng strict JSON Schema không ổn định giữa
                // các provider. json_object tương thích hơn; HeadingProposalJson + kiểm tra đủ ID bên dưới
                // vẫn từ chối mọi output sai cấu trúc hoặc thiếu quyết định.
                response_format = new { type = "json_object" },
                provider = new
                {
                    zdr = true,
                    data_collection = "deny",
                    require_parameters = true,
                    allow_fallbacks = true,
                },
            };
            var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(body);
            var payloadHash = ProviderObservabilityHashing.Sha256Bytes(payloadBytes);
            using var telemetryAttempt = logical?.StartAttempt(
                $"{stage.ToLowerInvariant()}-{Guid.NewGuid():N}",
                payloadHash,
                payloadBytes.Length,
                ProviderObservabilityHashing.EstimateTokens(constrainedUser + system),
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

            var sw = Stopwatch.StartNew();
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attemptDeadline.Token);
            telemetryAttempt?.Event("RESPONSE_HEADERS_RECEIVED", new { status = (int)response.StatusCode });
            telemetryAttempt?.Event("FIRST_RESPONSE_BYTE", new { observable = false, note = "ReadAsStringAsync is the current transport boundary." });
            var responseText = await response.Content.ReadAsStringAsync(attemptDeadline.Token);
            telemetryAttempt?.PersistRawResponse(responseText);
            telemetryAttempt?.Event("RESPONSE_BODY_COMPLETE", new { responseBytes = Encoding.UTF8.GetByteCount(responseText), responseHash = ProviderObservabilityHashing.Sha256Utf8(responseText) });
            sw.Stop();
            elapsedMs += sw.ElapsedMilliseconds;
            _options.DebugLog?.Invoke(
                $"[OpenRouter] LLM RESPONSE status={(int)response.StatusCode} elapsedMs={sw.ElapsedMilliseconds} payload={SafeDebug(responseText)}");

            if (!response.IsSuccessStatusCode)
            {
                telemetryAttempt?.Fail("HTTP_ERROR", new { status = (int)response.StatusCode });
                var requestId = GetDiagnosticHeader(response, "x-request-id")
                    ?? GetDiagnosticHeader(response, "cf-ray")
                    ?? GetDiagnosticHeader(response, "x-openrouter-generation-id");
                var diagnostic = requestId is null ? "" : $" (request: {requestId})";
                throw new HttpRequestException(
                    $"OpenRouter trả {(int)response.StatusCode} {response.ReasonPhrase}{diagnostic}: {SafeError(responseText)}",
                    null,
                    response.StatusCode);
            }

            var raw = ExtractContent(responseText);
            rawOutputs.Add(raw);
            telemetryAttempt?.Event("PARSE_STARTED", new { responseBytes = Encoding.UTF8.GetByteCount(raw), responseHash = ProviderObservabilityHashing.Sha256Utf8(raw) });
            var parsed = HeadingProposalJson.Parse(raw, includeNonHeadings: true);
            telemetryAttempt?.PersistParsed(parsed);
            telemetryAttempt?.Event("PARSE_COMPLETED", new { parsedCount = parsed.Count });
            var requestedThisAttempt = remaining.ToHashSet();

            telemetryAttempt?.Event("BIND_STARTED", new { requestedCount = requestedThisAttempt.Count });
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
            telemetryAttempt?.PersistBinding(new { keptIndexes = kept.Keys.OrderBy(x => x).ToArray(), explicitNonHeadings = explicitNonHeadings.OrderBy(x => x).ToArray() });
            telemetryAttempt?.Event("BIND_COMPLETED", new { keptCount = kept.Count, rejectedCount = rejected });

            remaining = allAllowed.Where(i => !seen.Contains(i)).ToList();
        }

        if (remaining.Count > 0)
            throw new FormatException(
                $"OpenRouter trả {seen.Count}/{allowed.Count} quyết định hợp lệ; " +
                $"ID còn thiếu=[{string.Join(',', remaining)}] sau {_options.MissingIdRetries + 1} lượt; " +
                $"output={SafeOutput(string.Join(" | ", rawOutputs))}");

        var orderedHeadings = allAllowed.Where(kept.ContainsKey).Select(i => kept[i]).ToArray();
        logical?.Complete(new { resultCount = orderedHeadings.Length, rawResponseCount = rawOutputs.Count });
        return new ChunkResult(
            orderedHeadings,
            string.Join(Environment.NewLine, rawOutputs),
            rejected,
            elapsedMs,
            explicitNonHeadings,
            rejectedRoles);
    }

    private static int headingsCount(bool roles, string user) => roles ? 0 : user.Count(c => c == '{');

    /// <summary>Nhiệm vụ hẹp — xem <see cref="IHeaderClassifier.BoundaryCutAsync"/>.</summary>
    public async Task<string> BoundaryCutAsync(string systemPrompt, string userMessage, CancellationToken ct = default)
    {
        var body = new
        {
            model = _options.Model,
            temperature = 0,
            // Role/pointer passes return one JSON item per supplied source id. A fixed 120-token
            // cap truncates otherwise valid multi-block responses and turns them into invisible
            // missing decisions. Keep the result bounded by the configured model profile.
            max_tokens = BoundaryOutputBudget(userMessage),
            reasoning = new { effort = "none" },
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userMessage },
            },
            response_format = new { type = "json_object" },
            provider = new
            {
                zdr = true,
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
            CandidateCount = 1,
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

    private int BoundaryOutputBudget(string userMessage)
    {
        var identifiers = 0;
        var offset = 0;
        const string token = "\"id\"";
        while ((offset = userMessage.IndexOf(token, offset, StringComparison.Ordinal)) >= 0)
        {
            identifiers++;
            offset += token.Length;
        }

        return Math.Clamp(96 + identifiers * 64, 256, _options.MaxOutputTokens);
    }

    private static string SafeError(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "không có nội dung lỗi";
        var oneLine = text.ReplaceLineEndings(" ").Trim();
        return oneLine.Length <= 500 ? oneLine : oneLine[..500] + "…";
    }

    private static string SafeOutput(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "<rỗng>";
        var oneLine = text.ReplaceLineEndings(" ").Trim();
        return oneLine.Length <= 800 ? oneLine : oneLine[..800] + "…";
    }

    private static string SafeDebug(string text)
    {
        var oneLine = string.IsNullOrWhiteSpace(text) ? "<empty>" : text.ReplaceLineEndings(" ").Trim();
        return oneLine.Length <= 16_000 ? oneLine : oneLine[..16_000] + "…[truncated]";
    }

    private static string? GetDiagnosticHeader(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out var values))
            return values.FirstOrDefault();
        if (response.Content.Headers.TryGetValues(name, out values))
            return values.FirstOrDefault();
        return null;
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
