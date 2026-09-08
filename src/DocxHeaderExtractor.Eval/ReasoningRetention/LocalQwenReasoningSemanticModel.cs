using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>OpenAI-compatible vLLM adapter for the fixed LAN Qwen campaign.</summary>
public sealed class LocalQwenReasoningSemanticModel : IReasoningSemanticModel,
    IReasoningAttemptTimeoutModel, IReasoningCompletionTelemetrySource, IDisposable
{
    private readonly RemoteInferenceOptions _options;
    private readonly ReasoningProviderTimeoutOptions _timeouts;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly object _telemetryGate = new();
    private readonly List<ReasoningCompletionTelemetry> _telemetry = [];
    private int _providerCalls;

    private readonly int _maxHeadingsPerResponse;

    public LocalQwenReasoningSemanticModel(
        RemoteInferenceOptions options,
        HttpClient? http = null,
        ReasoningProviderTimeoutOptions? timeoutOptions = null,
        int maxHeadingsPerResponse = 1)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        if (!_options.Endpoint.Host.Equals("192.168.11.22", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("LOCAL_QWEN_ENDPOINT_MISMATCH");
        if (maxHeadingsPerResponse < 1) throw new ArgumentOutOfRangeException(nameof(maxHeadingsPerResponse));
        _maxHeadingsPerResponse = maxHeadingsPerResponse;
        _timeouts = timeoutOptions ?? ReasoningProviderTimeoutOptions.FromEnvironment();
        _timeouts.Validate();
        _http = http ?? CreateHttpClient(_timeouts);
        _ownsHttp = http is null;
    }

    public string ModelName => _options.Model;
    public string ProviderName => "vLLM-LAN";
    public int ContextSize => _options.ContextSize;
    public int ProviderCalls => Volatile.Read(ref _providerCalls);
    public IReadOnlyList<ReasoningCompletionTelemetry> CompletionTelemetry
    {
        get { lock (_telemetryGate) return _telemetry.ToArray(); }
    }

    public Task<ReasoningModelResponse> CompleteAsync(ReasoningModelRequest request, CancellationToken ct = default) =>
        CompleteAsync(request, _timeouts.TotalRequestTimeout, ct);

    public async Task<ReasoningModelResponse> CompleteAsync(
        ReasoningModelRequest request,
        TimeSpan attemptTimeout,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (attemptTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(attemptTimeout));
        var telemetry = new ReasoningCompletionTelemetry
        {
            RequestId = request.RequestId, Route = request.Route, DocumentId = request.DocumentId,
            SemanticPassId = request.SemanticPassId, ContextSegmentId = request.ContextSegmentId,
            AttemptId = request.AttemptId, AttemptNumber = ParseAttemptNumber(request.AttemptId),
            Model = _options.Model, Provider = ProviderName, Temperature = 0,
            Seed = 42, ConfiguredMaxOutputTokens = _options.MaxOutputTokens,
            InputCharacters = request.SystemPrompt.Length + request.UserPrompt.Length,
        };
        Interlocked.Increment(ref _providerCalls);
        var body = new
        {
            model = _options.Model, temperature = 0, top_p = 1, top_k = 1, seed = 42,
            max_tokens = Math.Min(_options.MaxOutputTokens, 16_384), stream = false,
            chat_template_kwargs = new { enable_thinking = false },
            messages = new[] { new { role = "system", content = request.SystemPrompt }, new { role = "user", content = request.UserPrompt } },
            response_format = new { type = "json_schema", json_schema = new { name = "reasoning_v2", strict = true, schema = ResponseSchema(_maxHeadingsPerResponse) } },
        };
        using var message = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint) { Content = JsonContent.Create(body) };
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        var sw = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(attemptTimeout);
        try
        {
            using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            telemetry.HttpStatus = (int)response.StatusCode;
            var raw = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            telemetry.ReceivedContentCharacters = raw.Length;
            telemetry.ReceivedContentBytes = Encoding.UTF8.GetByteCount(raw);
            telemetry.FullContentSha256 = Sha256(raw);
            using var envelope = JsonDocument.Parse(raw);
            telemetry.CompletionEnvelopeComplete = true;
            if (!response.IsSuccessStatusCode) throw Failure(ReasoningCompletionFailureClass.ProviderUnavailable, $"vLLM returned {(int)response.StatusCode}", telemetry);
            var root = envelope.RootElement;
            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                throw Failure(ReasoningCompletionFailureClass.CompleteResponseSchemaInvalid, "choices-missing", telemetry);
            var choice = choices[0]; telemetry.FinishReason = choice.TryGetProperty("finish_reason", out var finish) ? finish.GetString() : null;
            if (telemetry.FinishReason is "length" or "max_tokens") throw Failure(ReasoningCompletionFailureClass.ProviderOutputLimit, "finish-limit", telemetry);
            var content = choice.GetProperty("message").GetProperty("content").GetString();
            if (string.IsNullOrWhiteSpace(content)) throw Failure(ReasoningCompletionFailureClass.CompleteResponseSchemaInvalid, "content-missing", telemetry);
            var parsed = ReasoningModelResponseParser.Parse(content);
            telemetry.JsonParseSucceeded = true; telemetry.StreamCompletedNormally = true;
            return parsed with { RawResponseHash = telemetry.FullContentSha256 };
        }
        catch (ReasoningCompletionException) { throw; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            telemetry.FailureClass = ReasoningCompletionFailureClass.ProviderTotalTimeout;
            throw Failure(telemetry.FailureClass, "local-vllm-attempt-timeout", telemetry);
        }
        catch (JsonException ex)
        {
            telemetry.FailureClass = ReasoningCompletionFailureClass.CompleteResponseSchemaInvalid;
            throw Failure(telemetry.FailureClass, ex.Message, telemetry, ex);
        }
        catch (FormatException ex)
        {
            telemetry.FailureClass = ReasoningCompletionFailureClass.CompleteResponseSchemaInvalid;
            throw Failure(telemetry.FailureClass, ex.Message, telemetry, ex);
        }
        catch (HttpRequestException ex)
        {
            telemetry.FailureClass = ReasoningCompletionFailureClass.ProviderUnavailable;
            throw Failure(telemetry.FailureClass, ex.Message, telemetry, ex);
        }
        finally
        {
            telemetry.ElapsedMilliseconds = sw.ElapsedMilliseconds;
            lock (_telemetryGate) _telemetry.Add(telemetry);
        }
    }

    private static ReasoningCompletionException Failure(string cls, string message, ReasoningCompletionTelemetry telemetry, Exception? inner = null)
    {
        telemetry.FailureClass = cls;
        return new ReasoningCompletionException(cls, message, telemetry, inner);
    }

    private static object ResponseSchema(int maxHeadingsPerResponse) => new
    {
        type = "object", additionalProperties = false,
        properties = new
        {
            schemaVersion = new { type = "string" },
            headings = new { type = "array", maxItems = maxHeadingsPerResponse, items = new
            {
                type = "object", additionalProperties = false,
                properties = new
                {
                    start = new { type = "integer", minimum = 0 }, end = new { type = "integer", minimum = 1 },
                    semanticRole = new { type = "string" }, proposedLevel = new { type = new[] { "integer", "null" } },
                    proposedParentLocalId = new { type = new[] { "string", "null" } }, confidence = new { type = "number" },
                    ownedIndex = new { type = new[] { "integer", "null" } },
                    decisionEvidence = new { type = "array", items = new { type = "object", additionalProperties = false, properties = new { evidenceType = new { type = "string" }, sourceReference = new { type = "string" }, shortEvidenceCode = new { type = "string" } }, required = new[] { "evidenceType", "sourceReference", "shortEvidenceCode" } } },
                }, required = new[] { "start", "end", "semanticRole", "proposedLevel", "proposedParentLocalId", "confidence", "ownedIndex", "decisionEvidence" },
            } },
            decisionEvidence = new { type = "array", items = new { type = "object", additionalProperties = false, properties = new { evidenceType = new { type = "string" }, sourceReference = new { type = "string" }, shortEvidenceCode = new { type = "string" } }, required = new[] { "evidenceType", "sourceReference", "shortEvidenceCode" } } },
        }, required = new[] { "schemaVersion", "headings", "decisionEvidence" },
    };

    private static HttpClient CreateHttpClient(ReasoningProviderTimeoutOptions options) => new(new SocketsHttpHandler { ConnectTimeout = options.ConnectTimeout, MaxConnectionsPerServer = 8 }) { Timeout = Timeout.InfiniteTimeSpan };
    private static int ParseAttemptNumber(string? attemptId) => int.TryParse(attemptId?.Split("attempt-", StringSplitOptions.None).LastOrDefault(), out var n) ? n : 0;
    private static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    public void Dispose() { if (_ownsHttp) _http.Dispose(); }
}
