using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DocxHeaderExtractor.Infrastructure.AI;

/// <summary>Raw streaming evidence retained by a bounded qualification canary after one completed call.</summary>
public sealed record OpenRouterExecutionObservation(
    string Content,
    string? FinishReason,
    JsonElement? Usage,
    string RawSse,
    int SseEventCount,
    int RetryCount);

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

    /// <summary>Waits between transport retries. Replaceable only so tests need not sleep.</summary>
    internal Func<TimeSpan, CancellationToken, Task> RetryWait { get; set; } = Task.Delay;

    public OpenRouterHeaderExtractor(HttpClient http, RemoteInferenceOptions options)
    {
        _http = http;
        _options = Validate(options);
    }

    private OpenRouterHeaderExtractor(HttpClient http, RemoteInferenceOptions options, bool ownsHttp)
        : this(http, options) => _ownsHttp = ownsHttp;

    public static OpenRouterHeaderExtractor CreateOwned(RemoteInferenceOptions options) =>
        // The per-attempt transport deadline owns timing; HttpClient must not cut a stream first.
        new(new HttpClient { Timeout = Timeout.InfiniteTimeSpan }, options, ownsHttp: true);

    public string ModelName => _options.Model;
    public int ContextSize => _options.ContextSize;
    public string RuntimeDescription => "OpenRouter streaming RPC · data_collection=deny";
    public int SharedPrefixTokens => 0;

    /// <summary>
    /// Nhiệm vụ hẹp — xem <see cref="IHeaderClassifier.BoundaryCutAsync"/>.
    /// <para>
    /// OpenRouter streaming transport V1: the configuration the production re-baseline qualified.
    /// The request streams (<c>stream=true</c>, <c>usage.include=true</c>); the reply is read as raw
    /// SSE bytes after the response headers and counts as transport-complete only when a terminal
    /// <c>finish_reason</c>, the <c>[DONE]</c> sentinel and a clean end of stream have all been seen.
    /// The provider deadline covers transport only; the reassembled content is handed back outside it
    /// and validated by the caller's contract, never here.
    /// </para>
    /// <para>
    /// Transport failures (HTTP 429, 502/503/504, network errors, timeouts, incomplete streams) are
    /// retried at most <see cref="RemoteInferenceOptions.TransientRequestRetries"/> times with bounded
    /// backoff, honouring <c>Retry-After</c>. A completed stream is never resent, and nothing about
    /// the content - <c>finish_reason=length</c>, invalid JSON, a contract failure - is ever retried
    /// as transport.
    /// </para>
    /// </summary>
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

        var maxTokens = BoundaryOutputBudget(userMessage, expectedItemCount);
        var body = RequestBody(systemPrompt, userMessage, maxTokens);
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(body);
        using var logical = ProviderCallTelemetry.Start(_options.Observability, new ProviderLogicalCallMetadata
        {
            Stage = "BOUNDARY_CUT",
            LogicalCallId = $"boundary-cut-{Guid.NewGuid():N}",
            RequestHash = ProviderObservabilityHashing.Sha256Bytes(payloadBytes),
            RequestBytes = payloadBytes.Length,
            EstimatedInputTokens = ProviderObservabilityHashing.EstimateTokens(systemPrompt + "\n" + userMessage),
            MaxOutputTokens = maxTokens,
            SourceItemCount = 1,
            ContextItemCount = 1,
            ContextCharacterCount = userMessage.Length,
            Provider = "OpenRouter",
            Model = _options.Model,
        });
        _options.DebugLog?.Invoke($"[OpenRouter] LLM REQUEST model={_options.Model} payload={Encoding.UTF8.GetString(payloadBytes)}");
        var (content, _) = await ExecuteLoopAsync(payloadBytes, maxTokens, systemPrompt, userMessage, "BOUNDARY_CUT", logical, ct);
        return content;
    }

    /// <summary>
    /// Generic execution surface for a caller that has already frozen its own complete provider
    /// request body (V5's semantic-claim execution, for one). Reuses the exact same HTTP/SSE
    /// transport, retry and telemetry machinery as <see cref="BoundaryCutAsync"/> without recomputing
    /// a request body or a completion budget here - the caller's frozen bytes are sent exactly as
    /// given, never rebuilt. <paramref name="maxTokens"/>, <paramref name="systemPrompt"/> and
    /// <paramref name="userMessage"/> are metadata for telemetry/compatibility only; they play no
    /// part in what is actually sent over the wire.
    /// </summary>
    public async Task<(string Content, string? FinishReason)> ExecuteAsync(
        byte[] payloadBytes, int maxTokens, string systemPrompt, string userMessage, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(payloadBytes);
        TransportCompatibility.EnsureCompatible(systemPrompt, userMessage, TransportCompatibility.JsonObjectResponseFormat);
        using var logical = ProviderCallTelemetry.Start(_options.Observability, new ProviderLogicalCallMetadata
        {
            Stage = "V5_SEMANTIC_CLAIM",
            LogicalCallId = $"v5-claim-{Guid.NewGuid():N}",
            RequestHash = ProviderObservabilityHashing.Sha256Bytes(payloadBytes),
            RequestBytes = payloadBytes.Length,
            EstimatedInputTokens = ProviderObservabilityHashing.EstimateTokens(systemPrompt + "\n" + userMessage),
            MaxOutputTokens = maxTokens,
            SourceItemCount = 1,
            ContextItemCount = 1,
            ContextCharacterCount = userMessage.Length,
            Provider = "OpenRouter",
            Model = _options.Model,
        });
        _options.DebugLog?.Invoke($"[OpenRouter] V5 REQUEST model={_options.Model} payload={Encoding.UTF8.GetString(payloadBytes)}");
        return await ExecuteLoopAsync(payloadBytes, maxTokens, systemPrompt, userMessage, "V5_SEMANTIC_CLAIM", logical, ct);
    }

    /// <summary>
    /// Executes an already-frozen json_object body once through the production SSE transport and
    /// returns the reassembled content together with the raw SSE evidence. This is intentionally
    /// additive: callers that do not need canary observability keep using <see cref="ExecuteAsync"/>.
    /// </summary>
    public async Task<OpenRouterExecutionObservation> ExecuteObservedAsync(
        byte[] payloadBytes, int maxTokens, string systemPrompt, string userMessage, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(payloadBytes);
        TransportCompatibility.EnsureCompatible(systemPrompt, userMessage, TransportCompatibility.JsonObjectResponseFormat);
        using var logical = ProviderCallTelemetry.Start(_options.Observability, new ProviderLogicalCallMetadata
        {
            Stage = "V5_SEMANTIC_DECISION_CANARY",
            LogicalCallId = $"v5-decision-canary-{Guid.NewGuid():N}",
            RequestHash = ProviderObservabilityHashing.Sha256Bytes(payloadBytes),
            RequestBytes = payloadBytes.Length,
            EstimatedInputTokens = ProviderObservabilityHashing.EstimateTokens(systemPrompt + "\n" + userMessage),
            MaxOutputTokens = maxTokens,
            SourceItemCount = 1,
            ContextItemCount = 1,
            ContextCharacterCount = userMessage.Length,
            Provider = "OpenRouter",
            Model = _options.Model,
        });

        for (var attempt = 1; ; attempt++)
        {
            var result = await StreamOnceAsync(payloadBytes, maxTokens, systemPrompt, userMessage, logical, ct);
            if (result.Content is { } content)
            {
                logical?.Complete(new { resultCharacters = content.Length, attempts = attempt, sseEvents = result.SseEventCount });
                return new OpenRouterExecutionObservation(content, result.FinishReason, result.Usage,
                    result.RawSse ?? string.Empty, result.SseEventCount, attempt - 1);
            }

            ct.ThrowIfCancellationRequested();
            if (!result.Retryable || attempt > _options.TransientRequestRetries)
                throw result.Error!;

            var delay = RetryDelay(attempt, result.StatusCode, result.RetryAfter);
            _options.DebugLog?.Invoke($"[OpenRouter] V5_SEMANTIC_DECISION_CANARY transport retry {attempt} after {delay.TotalMilliseconds:0} ms: {result.Error!.Message}");
            await RetryWait(delay, ct);
        }
    }

    /// <summary>
    /// Same frozen-body execution surface as <see cref="ExecuteAsync"/> - exact same HTTP/SSE
    /// transport, retry, deadline and telemetry machinery, reused unchanged - for a forced-tool-call
    /// request instead of a <c>response_format</c> one. Additive only: <see cref="SseReassembly"/>
    /// already captured only <c>delta.content</c>/<c>finish_reason</c>; this reads
    /// <c>delta.tool_calls[]</c> from the exact same events, which a json_object response never
    /// carries, so <see cref="ExecuteAsync"/>/<see cref="BoundaryCutAsync"/> are unaffected byte for
    /// byte. Returns the raw, unreassembled per-chunk fragments -
    /// <see cref="V5ToolCallArgumentsReassembler"/> does the reassembly, deliberately kept out of this
    /// transport class.
    /// </summary>
    public async Task<(string Content, string? FinishReason, IReadOnlyList<V5ToolCallDeltaFragment> ToolCallFragments, JsonElement? Usage)> ExecuteToolCallAsync(
        byte[] payloadBytes, int maxTokens, string systemPrompt, string userMessage, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(payloadBytes);
        using var logical = ProviderCallTelemetry.Start(_options.Observability, new ProviderLogicalCallMetadata
        {
            Stage = "V5_FORCED_TOOL_CALL",
            LogicalCallId = $"v5-forced-tool-{Guid.NewGuid():N}",
            RequestHash = ProviderObservabilityHashing.Sha256Bytes(payloadBytes),
            RequestBytes = payloadBytes.Length,
            EstimatedInputTokens = ProviderObservabilityHashing.EstimateTokens(systemPrompt + "\n" + userMessage),
            MaxOutputTokens = maxTokens,
            SourceItemCount = 1,
            ContextItemCount = 1,
            ContextCharacterCount = userMessage.Length,
            Provider = "OpenRouter",
            Model = _options.Model,
        });
        _options.DebugLog?.Invoke($"[OpenRouter] V5_FORCED_TOOL_CALL REQUEST model={_options.Model} payload={Encoding.UTF8.GetString(payloadBytes)}");

        for (var attempt = 1; ; attempt++)
        {
            var result = await StreamOnceAsync(payloadBytes, maxTokens, systemPrompt, userMessage, logical, ct);
            if (result.Content is { } content)
            {
                var fragments = result.ToolCallFragments ?? [];
                logical?.Complete(new { resultCharacters = content.Length, toolCallFragments = fragments.Count, attempts = attempt });
                return (content, result.FinishReason, fragments, result.Usage);
            }

            ct.ThrowIfCancellationRequested();
            if (!result.Retryable || attempt > _options.TransientRequestRetries)
                throw result.Error!;

            var delay = RetryDelay(attempt, result.StatusCode, result.RetryAfter);
            _options.DebugLog?.Invoke($"[OpenRouter] V5_FORCED_TOOL_CALL transport retry {attempt} after {delay.TotalMilliseconds:0} ms: {result.Error!.Message}");
            await RetryWait(delay, ct);
        }
    }

    private async Task<(string Content, string? FinishReason)> ExecuteLoopAsync(
        byte[] payloadBytes, int maxTokens, string systemPrompt, string userMessage, string stage,
        ProviderCallTelemetry? logical, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var result = await StreamOnceAsync(payloadBytes, maxTokens, systemPrompt, userMessage, logical, ct);
            if (result.Content is { } content)
            {
                logical?.Complete(new { resultCharacters = content.Length, attempts = attempt });
                return (content, result.FinishReason);
            }

            ct.ThrowIfCancellationRequested();
            if (!result.Retryable || attempt > _options.TransientRequestRetries)
                throw result.Error!;

            var delay = RetryDelay(attempt, result.StatusCode, result.RetryAfter);
            _options.DebugLog?.Invoke($"[OpenRouter] {stage} transport retry {attempt} after {delay.TotalMilliseconds:0} ms: {result.Error!.Message}");
            await RetryWait(delay, ct);
        }
    }

    private object RequestBody(string systemPrompt, string userMessage, int maxTokens)
    {
        // A pinned route disables fallbacks, so the request is served by exactly the route the
        // configuration names. Without one, OpenRouter's automatic routing policy is kept.
        object provider = string.IsNullOrWhiteSpace(_options.OpenRouterProviderRoute)
            ? new
            {
                zdr = _options.RequireZeroDataRetention,
                data_collection = "deny",
                require_parameters = true,
                allow_fallbacks = true,
            }
            : new
            {
                order = new[] { _options.OpenRouterProviderRoute },
                allow_fallbacks = false,
                require_parameters = true,
                data_collection = "deny",
                zdr = _options.RequireZeroDataRetention,
            };
        return new
        {
            model = _options.Model,
            temperature = 0,
            // Role/pointer passes return one JSON item per supplied source id. A fixed 120-token
            // cap truncates otherwise valid multi-block responses and turns them into invisible
            // missing decisions. Keep the result bounded by the configured model profile.
            max_tokens = maxTokens,
            reasoning = new { effort = _options.OpenRouterReasoningEffort },
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userMessage },
            },
            response_format = new { type = "json_object" },
            provider,
            stream = true,
            usage = new { include = true },
        };
    }

    private sealed record StreamAttempt(
        string? Content,
        bool Retryable,
        Exception? Error,
        int? StatusCode,
        TimeSpan? RetryAfter,
        string? FinishReason = null,
        IReadOnlyList<V5ToolCallDeltaFragment>? ToolCallFragments = null,
        JsonElement? Usage = null,
        string? RawSse = null,
        int SseEventCount = 0);

    private async Task<StreamAttempt> StreamOnceAsync(
        byte[] payloadBytes,
        int maxTokens,
        string systemPrompt,
        string userMessage,
        ProviderCallTelemetry? logical,
        CancellationToken ct)
    {
        var deadlineSeconds = _options.ProviderTransportTimeoutSeconds;
        using var telemetryAttempt = logical?.StartAttempt(
            $"boundary-cut-{Guid.NewGuid():N}",
            ProviderObservabilityHashing.Sha256Bytes(payloadBytes),
            payloadBytes.Length,
            ProviderObservabilityHashing.EstimateTokens(systemPrompt + "\n" + userMessage),
            maxTokens,
            TimeSpan.FromSeconds(deadlineSeconds));

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
        {
            Content = new ByteArrayContent(payloadBytes)
            {
                Headers = { ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" } },
            },
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        request.Headers.TryAddWithoutValidation("X-Title", "DocxHeaderExtractor");

        // Transport-only deadline: from sending the request to the end of the stream.
        using var transportDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        transportDeadline.CancelAfter(TimeSpan.FromSeconds(deadlineSeconds));

        var stream = new SseReassembly();
        var raw = new StringBuilder();
        try
        {
            using var response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, transportDeadline.Token);
            var status = (int)response.StatusCode;
            telemetryAttempt?.Event("RESPONSE_HEADERS_RECEIVED", new { status });
            if (!response.IsSuccessStatusCode)
            {
                var errorText = await response.Content.ReadAsStringAsync(transportDeadline.Token);
                telemetryAttempt?.PersistRawResponse(errorText);
                telemetryAttempt?.Fail("HTTP_ERROR", new
                {
                    status,
                    retryable = IsRetryableStatus(status),
                    retryAfterSeconds = RetryAfterOf(response)?.TotalSeconds,
                });
                _options.DebugLog?.Invoke($"[OpenRouter] LLM RESPONSE status={status} payload={SafeDebug(errorText)}");
                var error = new HttpRequestException(
                    $"OpenRouter trả {status} {response.ReasonPhrase}: {SafeError(errorText)}",
                    null,
                    response.StatusCode);
                return new StreamAttempt(null, IsRetryableStatus(status), error, status, RetryAfterOf(response));
            }

            await using var body = await response.Content.ReadAsStreamAsync(transportDeadline.Token);
            var decoder = Encoding.UTF8.GetDecoder();
            var bytes = new byte[8192];
            var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
            var pending = new StringBuilder();
            var firstByte = true;
            while (true)
            {
                var read = await body.ReadAsync(bytes, transportDeadline.Token);
                if (read == 0) break;
                if (firstByte)
                {
                    telemetryAttempt?.Event("FIRST_RESPONSE_BYTE", new { observable = true });
                    firstByte = false;
                }
                var count = decoder.GetChars(bytes, 0, read, chars, 0, flush: false);
                pending.Append(chars, 0, count);
                raw.Append(chars, 0, count);
                stream.Feed(pending, final: false);
            }
            var tail = decoder.GetChars([], 0, 0, chars, 0, flush: true);
            pending.Append(chars, 0, tail);
            raw.Append(chars, 0, tail);
            stream.Feed(pending, final: true);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            telemetryAttempt?.Fail("ATTEMPT_TIMEOUT", new { timeoutSeconds = deadlineSeconds });
            return new StreamAttempt(null, true,
                new TimeoutException($"OpenRouter transport did not complete within {deadlineSeconds} s."), null, null);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is null)
        {
            telemetryAttempt?.Fail("NETWORK_ERROR", new { error = ex.Message });
            return new StreamAttempt(null, true, ex, null, null);
        }
        catch (IOException ex)
        {
            telemetryAttempt?.Fail("INTERRUPTED_STREAM", new { error = ex.Message });
            return new StreamAttempt(null, true, ex, null, null);
        }
        catch (JsonException ex)
        {
            telemetryAttempt?.Fail("MALFORMED_STREAM", new { error = ex.Message });
            return new StreamAttempt(null, true,
                new HttpRequestException($"OpenRouter stream carried a malformed event: {ex.Message}"), null, null);
        }

        telemetryAttempt?.PersistRawResponse(raw.ToString());
        _options.DebugLog?.Invoke($"[OpenRouter] LLM RESPONSE stream={SafeDebug(raw.ToString())}");

        // Everything below runs after the transport deadline: reassembly is already done, and the
        // content is judged by the caller's contract, not by this transport.
        if (stream.ProviderError is { } providerError)
        {
            telemetryAttempt?.Fail("STREAM_PROVIDER_ERROR", new { error = providerError });
            return new StreamAttempt(null, true,
                new HttpRequestException($"OpenRouter stream error: {SafeError(providerError)}"), null, null);
        }
        if (!stream.TransportComplete)
        {
            telemetryAttempt?.Fail("INCOMPLETE_STREAM", new
            {
                terminalFinishReason = stream.FinishReason,
                doneObserved = stream.DoneObserved,
            });
            return new StreamAttempt(null, true,
                new HttpRequestException("OpenRouter stream ended before a terminal finish_reason and [DONE]."), null, null);
        }

        var content = stream.Content.Trim();
        telemetryAttempt?.Event("TRANSPORT_COMPLETE", new
        {
            finishReason = stream.FinishReason,
            doneObserved = stream.DoneObserved,
            cleanEof = stream.StreamEnded,
            usage = stream.Usage,
        });
        telemetryAttempt?.PersistParsed(new { content, finishReason = stream.FinishReason, usage = stream.Usage, toolCallFragments = stream.ToolCallFragments.Count });
        telemetryAttempt?.Complete();
        return new StreamAttempt(content, false, null, 200, null, stream.FinishReason, stream.ToolCallFragments, stream.Usage,
            raw.ToString(), stream.EventCount);
    }

    private static bool IsRetryableStatus(int status) => status is 429 or 502 or 503 or 504;

    private static TimeSpan? RetryAfterOf(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta) return delta;
        if (header?.Date is { } date) return date - DateTimeOffset.UtcNow;
        return null;
    }

    /// <summary>
    /// Bounded backoff: the provider's Retry-After when it sends one, otherwise exponential from
    /// 10 s (429) or 5 s (5xx/network), with a small deterministic jitter, capped at 60 s.
    /// </summary>
    internal static TimeSpan RetryDelay(int attempt, int? status, TimeSpan? retryAfter)
    {
        if (retryAfter is { } fromProvider && fromProvider > TimeSpan.Zero)
            return fromProvider < TimeSpan.FromSeconds(60) ? fromProvider : TimeSpan.FromSeconds(60);
        var baseMs = status == 429 ? 10_000 : 5_000;
        var jitter = attempt * 101 % 750;
        return TimeSpan.FromMilliseconds(Math.Min(60_000, baseMs * (1 << (attempt - 1)) + jitter));
    }

    /// <summary>
    /// Server-sent-events reassembly for one chat-completions stream: content deltas, the terminal
    /// finish_reason, usage, a provider error event, and the [DONE] sentinel.
    /// </summary>
    internal sealed class SseReassembly
    {
        private readonly StringBuilder _content = new();
        private readonly List<string> _eventLines = [];
        private readonly List<V5ToolCallDeltaFragment> _toolCallFragments = [];

        public string Content => _content.ToString();
        // A json_object response never carries delta.tool_calls, so this stays empty for every
        // existing caller (BoundaryCutAsync/ExecuteAsync) - purely additive for ExecuteToolCallAsync.
        public IReadOnlyList<V5ToolCallDeltaFragment> ToolCallFragments => _toolCallFragments;
        public string? FinishReason { get; private set; }
        public bool DoneObserved { get; private set; }
        public bool StreamEnded { get; private set; }
        public string? ProviderError { get; private set; }
        public JsonElement? Usage { get; private set; }
        public int EventCount { get; private set; }

        /// <summary>Complete only with a terminal finish_reason, [DONE] and a clean end of stream.</summary>
        public bool TransportComplete => FinishReason is not null && DoneObserved && StreamEnded && ProviderError is null;

        public void Feed(StringBuilder pending, bool final)
        {
            while (true)
            {
                var text = pending.ToString();
                var newline = text.IndexOf('\n');
                if (newline < 0)
                {
                    if (final)
                    {
                        pending.Clear();
                        if (text.Length > 0) Line(text.TrimEnd('\r'));
                        if (_eventLines.Count > 0) Dispatch();
                        StreamEnded = true;
                    }
                    return;
                }
                pending.Remove(0, newline + 1);
                Line(text[..newline].TrimEnd('\r'));
            }
        }

        private void Line(string line)
        {
            if (line.Length == 0)
            {
                if (_eventLines.Count > 0) Dispatch();
                return;
            }
            if (line.StartsWith(':')) return; // SSE comment / keep-alive
            _eventLines.Add(line);
        }

        private void Dispatch()
        {
            EventCount++;
            var data = string.Join("\n", _eventLines
                .Where(line => line.StartsWith("data:", StringComparison.Ordinal))
                .Select(line => line[5..].TrimStart()));
            _eventLines.Clear();
            if (data.Length == 0) return;
            if (data == "[DONE]")
            {
                DoneObserved = true;
                return;
            }

            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var error))
                ProviderError = error.ToString();
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                Usage = usage.Clone();
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0)
                return;
            var choice = choices[0];
            if (choice.TryGetProperty("delta", out var delta))
            {
                if (delta.TryGetProperty("content", out var piece) && piece.ValueKind == JsonValueKind.String)
                    _content.Append(piece.GetString());
                if (delta.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                    foreach (var toolCall in toolCalls.EnumerateArray())
                        _toolCallFragments.Add(ParseToolCallFragment(toolCall));
            }
            if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String)
                FinishReason = finish.GetString();
        }

        /// <summary>One <c>delta.tool_calls[]</c> entry, exactly as OpenRouter's OpenAI-compatible
        /// streaming normalizes it - <c>index</c> is the only field every fragment for one logical
        /// tool call is guaranteed to repeat; <c>id</c>/<c>function.name</c> typically arrive once,
        /// and <c>function.arguments</c> arrives as a fragment to be concatenated, never assumed
        /// whole.</summary>
        private static V5ToolCallDeltaFragment ParseToolCallFragment(JsonElement toolCall)
        {
            var index = toolCall.TryGetProperty("index", out var indexEl) && indexEl.ValueKind == JsonValueKind.Number ? indexEl.GetInt32() : 0;
            string? id = toolCall.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;
            string? name = null;
            string? argumentsChunk = null;
            if (toolCall.TryGetProperty("function", out var function))
            {
                if (function.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String) name = nameEl.GetString();
                if (function.TryGetProperty("arguments", out var argsEl) && argsEl.ValueKind == JsonValueKind.String) argumentsChunk = argsEl.GetString();
            }
            return new V5ToolCallDeltaFragment(index, id, name, argumentsChunk);
        }
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
