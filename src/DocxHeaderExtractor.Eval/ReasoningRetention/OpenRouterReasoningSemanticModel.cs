using System.Net.Http.Headers;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>
/// Eval-only OpenRouter adapter. It records call count and hashes responses, never private
/// chain-of-thought or raw prompts in the retention artifacts.
/// </summary>
public sealed class OpenRouterReasoningSemanticModel : IReasoningSemanticModel, IReasoningAttemptTimeoutModel,
    IReasoningCompletionTelemetrySource, IReasoningHierarchyModel, IDisposable
{
    private readonly HttpClient _http;
    private readonly RemoteInferenceOptions _options;
    private readonly ReasoningProviderTimeoutOptions _timeoutOptions;
    private readonly bool _ownsHttp;
    private int _providerCalls;
    private readonly List<ReasoningCompletionTelemetry> _completionTelemetry = [];

    public OpenRouterReasoningSemanticModel(
        RemoteInferenceOptions options,
        HttpClient? http = null,
        ReasoningProviderTimeoutOptions? timeoutOptions = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _timeoutOptions = timeoutOptions ?? ReasoningProviderTimeoutOptions.FromEnvironment();
        _timeoutOptions.Validate();
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new InvalidOperationException("PROVIDER_AUTH_FAILURE");
        _http = http ?? CreateHttpClient(_timeoutOptions);
        _ownsHttp = http is null;
    }

    public string ModelName => _options.Model;
    public string ProviderName => "OpenRouter";
    public int ContextSize => _options.ContextSize;
    public int ProviderCalls => Volatile.Read(ref _providerCalls);
    public IReadOnlyList<ReasoningCompletionTelemetry> CompletionTelemetry => _completionTelemetry;
    public ReasoningProviderTimeoutOptions TimeoutOptions => _timeoutOptions;

    public Task<ReasoningModelResponse> CompleteAsync(
        ReasoningModelRequest request,
        CancellationToken ct = default) =>
        CompleteAsync(request, _timeoutOptions.TotalRequestTimeout, ct);

    public async Task<ReasoningModelResponse> CompleteAsync(
        ReasoningModelRequest request,
        TimeSpan attemptTimeout,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (attemptTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(attemptTimeout));
        var telemetry = new ReasoningCompletionTelemetry
        {
            RequestId = request.RequestId,
            Route = request.Route,
            DocumentId = request.DocumentId,
            SemanticPassId = request.SemanticPassId,
            ContextSegmentId = request.ContextSegmentId,
            AttemptId = request.AttemptId,
            AttemptNumber = ParseAttemptNumber(request.AttemptId),
            Model = _options.Model,
            Provider = ProviderName,
            Temperature = 0,
            ConfiguredMaxOutputTokens = _options.MaxOutputTokens,
            InputCharacters = request.SystemPrompt.Length + request.UserPrompt.Length,
        };
        Interlocked.Increment(ref _providerCalls);

        var body = new
        {
            model = _options.Model,
            temperature = 0,
            max_tokens = Math.Min(_options.MaxOutputTokens, 16_384),
            reasoning = new { effort = "none" },
            messages = new[]
            {
                new { role = "system", content = request.SystemPrompt },
                new { role = "user", content = request.UserPrompt },
            },
            response_format = new { type = "json_schema", json_schema = new { name = "reasoning_v2", strict = true, schema = ResponseSchema() } },
            provider = new
            {
                zdr = true,
                data_collection = "deny",
                require_parameters = true,
                allow_fallbacks = false,
            },
        };
        using var message = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
        {
            Content = JsonContent.Create(body),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        message.Headers.TryAddWithoutValidation("X-Title", "DocxHeaderExtractor Accuracy99");

        var responseHeadersReceived = false;
        var stopwatch = Stopwatch.StartNew();
        using var totalTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        totalTimeout.CancelAfter(attemptTimeout);
        try
        {
            using var response = await SendForHeadersAsync(message, totalTimeout, ct);
            responseHeadersReceived = true;
            telemetry.HttpStatus = (int)response.StatusCode;
            var responseText = await ReadResponseBodyAsync(response, totalTimeout, ct, telemetry);
            telemetry.StreamCompletedNormally = true;
            var envelope = ReadEnvelope(responseText, telemetry);
            if (!response.IsSuccessStatusCode)
            {
                var failureClass = response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                    ? ReasoningCompletionFailureClass.ProviderAuthFailure
                    : ReasoningCompletionFailureClass.ProviderUnavailable;
                throw CompletionFailure(failureClass,
                    $"OpenRouter returned {(int)response.StatusCode} {response.ReasonPhrase}", telemetry);
            }

            if (IsOutputLimit(envelope.FinishReason))
                throw CompletionFailure(
                    ReasoningCompletionFailureClass.ProviderOutputLimit,
                    $"Provider finished with {envelope.FinishReason} before a complete semantic response.",
                    telemetry);

            if (string.IsNullOrWhiteSpace(envelope.Content))
                throw CompletionFailure(
                    ReasoningCompletionFailureClass.CompleteResponseSchemaInvalid,
                    "reasoning-provider-response-content-missing",
                    telemetry);

            var parsed = ReasoningModelResponseParser.Parse(envelope.Content);
            telemetry.JsonParseSucceeded = true;
            return parsed with
            {
                RawResponseHash = telemetry.FullContentSha256,
            };
        }
        catch (ReasoningCompletionException)
        {
            throw;
        }
        catch (ReasoningProviderTimeoutException ex)
        {
            telemetry.TimeoutStage = ex.TimeoutStage;
            telemetry.TransportException = ex.GetType().Name;
            throw CompletionFailure(ex.FailureClass, ex.Message, telemetry, ex);
        }
        catch (JsonException ex)
        {
            telemetry.JsonParseErrorOffset = ex.BytePositionInLine;
            telemetry.FailureClass = ReasoningCompletionFailureClass.CompleteResponseSchemaInvalid;
            throw CompletionFailure(
                ReasoningCompletionFailureClass.CompleteResponseSchemaInvalid,
                ex.Message,
                telemetry,
                ex);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            telemetry.TransportException = ex.GetType().Name;
            telemetry.TimeoutStage = responseHeadersReceived ? "TOTAL_OR_CLIENT_AFTER_HEADERS" : "TOTAL_OR_CLIENT_BEFORE_HEADERS";
            throw CompletionFailure(
                ReasoningCompletionFailureClass.ProviderTotalTimeout,
                ex.Message,
                telemetry,
                ex);
        }
        catch (HttpRequestException ex)
        {
            telemetry.TransportException = ex.GetType().Name;
            var connectTimeout = !responseHeadersReceived && IsConnectTimeout(ex);
            telemetry.TimeoutStage = connectTimeout ? "CONNECT" : telemetry.TimeoutStage;
            telemetry.FailureClass = responseHeadersReceived
                ? ReasoningCompletionFailureClass.TransportTruncation
                : connectTimeout
                    ? ReasoningCompletionFailureClass.ProviderConnectTimeout
                    : ReasoningCompletionFailureClass.ProviderUnavailable;
            throw CompletionFailure(telemetry.FailureClass, ex.Message, telemetry, ex);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException)
        {
            telemetry.FailureClass = ReasoningCompletionFailureClass.CompleteResponseSchemaInvalid;
            throw CompletionFailure(ReasoningCompletionFailureClass.CompleteResponseSchemaInvalid, ex.Message, telemetry, ex);
        }
        catch (Exception ex)
        {
            telemetry.TransportException = ex.GetType().Name;
            telemetry.FailureClass = ReasoningCompletionFailureClass.OtherProviderFailure;
            throw CompletionFailure(ReasoningCompletionFailureClass.OtherProviderFailure, ex.Message, telemetry, ex);
        }
        finally
        {
            telemetry.ElapsedMilliseconds = stopwatch.ElapsedMilliseconds;
            _completionTelemetry.Add(telemetry);
        }
    }

    public async Task<ReasoningHierarchyModelResponse> CompleteHierarchyAsync(
        ReasoningHierarchyModelRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var telemetry = new ReasoningCompletionTelemetry
        {
            RequestId = request.RequestId, Route = request.Route, DocumentId = request.DocumentId,
            SemanticPassId = "global-hierarchy", ContextSegmentId = "global-hierarchy",
            Model = _options.Model, Provider = ProviderName, Temperature = 0,
            ConfiguredMaxOutputTokens = Math.Min(_options.MaxOutputTokens, 8_192),
            InputCharacters = request.SystemPrompt.Length + request.UserPrompt.Length,
        };
        var stopwatch = Stopwatch.StartNew();
        Interlocked.Increment(ref _providerCalls);
        var body = new
        {
            model = _options.Model,
            temperature = 0,
            max_tokens = Math.Min(_options.MaxOutputTokens, 8_192),
            reasoning = new { effort = "none" },
            messages = new[]
            {
                new { role = "system", content = request.SystemPrompt },
                new { role = "user", content = request.UserPrompt },
            },
            response_format = new { type = "json_schema", json_schema = new { name = "reasoning_hierarchy_v1", strict = true, schema = HierarchySchema() } },
            provider = new { zdr = true, data_collection = "deny", require_parameters = true, allow_fallbacks = false },
        };
        using var message = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint) { Content = JsonContent.Create(body) };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        message.Headers.TryAddWithoutValidation("X-Title", "DocxHeaderExtractor Accuracy99");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeoutOptions.TotalRequestTimeout);
        try
        {
            using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            telemetry.HttpStatus = (int)response.StatusCode;
            var raw = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            var envelope = ReadEnvelope(raw, telemetry);
            if (!response.IsSuccessStatusCode) throw new InvalidDataException($"OPENROUTER_HIERARCHY_HTTP_{(int)response.StatusCode}");
            if (string.IsNullOrWhiteSpace(envelope.Content)) throw new FormatException("reasoning-hierarchy-content-missing");
            var parsed = ReasoningHierarchyResponseParser.Parse(envelope.Content);
            telemetry.JsonParseSucceeded = true;
            telemetry.StreamCompletedNormally = true;
            return parsed with { RawResponseHash = telemetry.FullContentSha256 };
        }
        catch (Exception ex)
        {
            telemetry.FailureClass = ex is FormatException or JsonException
                ? ReasoningCompletionFailureClass.CompleteResponseSchemaInvalid
                : ReasoningCompletionFailureClass.OtherProviderFailure;
            throw;
        }
        finally
        {
            telemetry.ElapsedMilliseconds = stopwatch.ElapsedMilliseconds;
            _completionTelemetry.Add(telemetry);
        }
    }

    private async Task<HttpResponseMessage> SendForHeadersAsync(
        HttpRequestMessage message,
        CancellationTokenSource totalTimeout,
        CancellationToken callerToken)
    {
        using var firstByteTimeout = CancellationTokenSource.CreateLinkedTokenSource(totalTimeout.Token);
        firstByteTimeout.CancelAfter(_timeoutOptions.FirstByteTimeout);
        try
        {
            return await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, firstByteTimeout.Token);
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            if (totalTimeout.IsCancellationRequested)
                throw new ReasoningProviderTimeoutException(
                    ReasoningCompletionFailureClass.ProviderTotalTimeout,
                    "OpenRouter request exceeded its total timeout before response headers.",
                    "TOTAL");
            throw new ReasoningProviderTimeoutException(
                ReasoningCompletionFailureClass.ProviderFirstByteTimeout,
                "OpenRouter request exceeded its first-byte timeout.",
                "FIRST_BYTE");
        }
    }

    private async Task<string> ReadResponseBodyAsync(
        HttpResponseMessage response,
        CancellationTokenSource totalTimeout,
        CancellationToken callerToken,
        ReasoningCompletionTelemetry telemetry)
    {
        Stream stream;
        using (var streamTimeout = CancellationTokenSource.CreateLinkedTokenSource(totalTimeout.Token))
        {
            streamTimeout.CancelAfter(_timeoutOptions.InactivityTimeout);
            try
            {
                stream = await response.Content.ReadAsStreamAsync(streamTimeout.Token);
            }
            catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
            {
                if (totalTimeout.IsCancellationRequested)
                    throw new ReasoningProviderTimeoutException(
                        ReasoningCompletionFailureClass.ProviderTotalTimeout,
                        "OpenRouter response exceeded its total timeout before the body stream was available.",
                        "TOTAL");
                throw new ReasoningProviderTimeoutException(
                    ReasoningCompletionFailureClass.ProviderStreamInactivityTimeout,
                    "OpenRouter response produced no body activity within the inactivity timeout.",
                    "STREAM_INACTIVITY");
            }
        }

        await using var responseStream = stream;
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(totalTimeout.Token);
            readTimeout.CancelAfter(_timeoutOptions.InactivityTimeout);
            int read;
            try
            {
                read = await responseStream.ReadAsync(buffer.AsMemory(), readTimeout.Token);
            }
            catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
            {
                if (totalTimeout.IsCancellationRequested)
                    throw new ReasoningProviderTimeoutException(
                        ReasoningCompletionFailureClass.ProviderTotalTimeout,
                        "OpenRouter response exceeded its total timeout while reading the body.",
                        "TOTAL");
                throw new ReasoningProviderTimeoutException(
                    ReasoningCompletionFailureClass.ProviderStreamInactivityTimeout,
                    "OpenRouter response produced no body activity within the inactivity timeout.",
                    "STREAM_INACTIVITY");
            }

            if (read == 0) break;
            await body.WriteAsync(buffer.AsMemory(0, read), callerToken);
            telemetry.ReceivedContentBytes += read;
        }

        return Encoding.UTF8.GetString(body.ToArray());
    }

    private static HttpClient CreateHttpClient(ReasoningProviderTimeoutOptions timeoutOptions)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = timeoutOptions.ConnectTimeout,
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static bool IsConnectTimeout(HttpRequestException exception) =>
        exception.InnerException is TimeoutException or OperationCanceledException ||
        exception.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase);

    private static int ParseAttemptNumber(string? attemptId)
    {
        const string marker = ":attempt-";
        if (string.IsNullOrWhiteSpace(attemptId)) return 0;
        var markerIndex = attemptId.LastIndexOf(marker, StringComparison.Ordinal);
        return markerIndex >= 0 && int.TryParse(attemptId[(markerIndex + marker.Length)..], out var number)
            ? number
            : 0;
    }

    private sealed class ReasoningProviderTimeoutException(
        string failureClass,
        string message,
        string timeoutStage) : Exception(message)
    {
        public string FailureClass { get; } = failureClass;
        public string TimeoutStage { get; } = timeoutStage;
    }

    private static ReasoningCompletionException CompletionFailure(
        string failureClass,
        string message,
        ReasoningCompletionTelemetry telemetry,
        Exception? inner = null)
    {
        telemetry.FailureClass = failureClass;
        return new ReasoningCompletionException(failureClass, message, telemetry, inner);
    }

    private static ProviderEnvelope ReadEnvelope(string responseText, ReasoningCompletionTelemetry telemetry)
    {
        telemetry.ReceivedContentCharacters = responseText.Length;
        telemetry.ReceivedContentBytes = Encoding.UTF8.GetByteCount(responseText);
        telemetry.FullContentSha256 = Sha256(responseText);
        telemetry.FirstContentHash = Sha256(responseText[..Math.Min(responseText.Length, 256)]);
        telemetry.LastContentHash = Sha256(responseText[Math.Max(0, responseText.Length - 256)..]);

        using var document = JsonDocument.Parse(responseText);
        telemetry.CompletionEnvelopeComplete = true;
        var root = document.RootElement;
        telemetry.ProviderRequestId = root.TryGetProperty("id", out var id) ? id.GetString() : null;
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            telemetry.ReportedInputTokens = ReadInt(usage, "prompt_tokens");
            telemetry.ReportedOutputTokens = ReadInt(usage, "completion_tokens");
            telemetry.ReportedTotalTokens = ReadInt(usage, "total_tokens");
            if (usage.TryGetProperty("cost", out var cost) && cost.TryGetDecimal(out var reportedCost))
                telemetry.ReportedCost = reportedCost;
        }

        if (!root.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0 ||
            choices[0].ValueKind != JsonValueKind.Object)
            throw new FormatException("reasoning-provider-response-choices-missing");

        var choice = choices[0];
        telemetry.FinishReason = choice.TryGetProperty("finish_reason", out var finish)
            ? finish.GetString() : null;
        if (!choice.TryGetProperty("message", out var message) ||
            message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.String)
            throw new FormatException("reasoning-provider-response-content-missing");

        return new ProviderEnvelope(content.GetString() ?? "", telemetry.FinishReason);
    }

    private static int? ReadInt(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : null;

    private static bool IsOutputLimit(string? finishReason) =>
        finishReason is not null && (finishReason.Equals("length", StringComparison.OrdinalIgnoreCase) ||
            finishReason.Equals("max_tokens", StringComparison.OrdinalIgnoreCase) ||
            finishReason.Equals("max_output_tokens", StringComparison.OrdinalIgnoreCase));

    private static object HierarchySchema() => new
    {
        type = "object", additionalProperties = false,
        properties = new
        {
            edges = new
            {
                type = "array",
                items = new
                {
                    type = "object", additionalProperties = false,
                    properties = new
                    {
                        childProposalId = new { type = "string" },
                        parentProposalId = new { type = new[] { "string", "null" } },
                    },
                    required = new[] { "childProposalId", "parentProposalId" },
                },
            },
        },
        required = new[] { "edges" },
    };

    private static object ResponseSchema() => new
    {
        type = "object", additionalProperties = false,
        properties = new
        {
            schemaVersion = new { type = "string" },
            headings = new
            {
                type = "array",
                items = new
                {
                    type = "object", additionalProperties = false,
                    properties = new
                    {
                        start = new { type = "integer", minimum = 0 },
                        end = new { type = "integer", minimum = 1 },
                        semanticRole = new { type = "string" },
                        proposedLevel = new { type = new[] { "integer", "null" } },
                        proposedParentLocalId = new { type = new[] { "string", "null" } },
                        confidence = new { type = "number" },
                        ownedIndex = new { type = new[] { "integer", "null" } },
                        decisionEvidence = new
                        {
                            type = "array", items = new
                            {
                                type = "object", additionalProperties = false,
                                properties = new
                                {
                                    evidenceType = new { type = "string" },
                                    sourceReference = new { type = "string" },
                                    shortEvidenceCode = new { type = "string" },
                                },
                                required = new[] { "evidenceType", "sourceReference", "shortEvidenceCode" },
                            },
                        },
                    },
                    required = new[] { "start", "end", "semanticRole", "proposedLevel", "proposedParentLocalId", "confidence", "ownedIndex", "decisionEvidence" },
                },
            },
            decisionEvidence = new
            {
                type = "array", items = new
                {
                    type = "object", additionalProperties = false,
                    properties = new
                    {
                        evidenceType = new { type = "string" },
                        sourceReference = new { type = "string" },
                        shortEvidenceCode = new { type = "string" },
                    },
                    required = new[] { "evidenceType", "sourceReference", "shortEvidenceCode" },
                },
            },
        },
        required = new[] { "schemaVersion", "headings", "decisionEvidence" },
    };

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record ProviderEnvelope(string Content, string? FinishReason);

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
