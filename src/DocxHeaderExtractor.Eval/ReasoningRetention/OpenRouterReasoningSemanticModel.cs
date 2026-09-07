using System.Net.Http.Headers;
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
public sealed class OpenRouterReasoningSemanticModel : IReasoningSemanticModel, IDisposable
{
    private readonly HttpClient _http;
    private readonly RemoteInferenceOptions _options;
    private readonly bool _ownsHttp;
    private int _providerCalls;
    private readonly List<ReasoningCompletionTelemetry> _completionTelemetry = [];

    public OpenRouterReasoningSemanticModel(RemoteInferenceOptions options, HttpClient? http = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new InvalidOperationException("PROVIDER_AUTH_FAILURE");
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _ownsHttp = http is null;
    }

    public string ModelName => _options.Model;
    public string ProviderName => "OpenRouter";
    public int ContextSize => _options.ContextSize;
    public int ProviderCalls => Volatile.Read(ref _providerCalls);
    public IReadOnlyList<ReasoningCompletionTelemetry> CompletionTelemetry => _completionTelemetry;

    public async Task<ReasoningModelResponse> CompleteAsync(
        ReasoningModelRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var telemetry = new ReasoningCompletionTelemetry
        {
            RequestId = request.RequestId,
            Route = request.Route,
            DocumentId = request.DocumentId,
            SemanticPassId = request.SemanticPassId,
            ContextSegmentId = request.ContextSegmentId,
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
            max_tokens = Math.Min(_options.MaxOutputTokens, 4096),
            reasoning = new { effort = "none" },
            messages = new[]
            {
                new { role = "system", content = request.SystemPrompt },
                new { role = "user", content = request.UserPrompt },
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
        using var message = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
        {
            Content = JsonContent.Create(body),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        message.Headers.TryAddWithoutValidation("X-Title", "DocxHeaderExtractor Accuracy99");

        var responseHeadersReceived = false;
        try
        {
            using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
            responseHeadersReceived = true;
            telemetry.HttpStatus = (int)response.StatusCode;
            var responseText = await response.Content.ReadAsStringAsync(ct);
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
            if (!parsed.Complete || parsed.OwnedRange is null ||
                !string.Equals(parsed.RequestId, request.RequestId, StringComparison.Ordinal) ||
                !string.Equals(parsed.SemanticPassId, request.SemanticPassId, StringComparison.Ordinal))
                throw CompletionFailure(
                    ReasoningCompletionFailureClass.CompleteResponseSchemaInvalid,
                    "reasoning-response-envelope-identity-invalid",
                    telemetry);

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
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            telemetry.TransportException = ex.GetType().Name;
            telemetry.FailureClass = ReasoningCompletionFailureClass.Timeout;
            throw CompletionFailure(ReasoningCompletionFailureClass.Timeout, ex.Message, telemetry, ex);
        }
        catch (HttpRequestException ex)
        {
            telemetry.TransportException = ex.GetType().Name;
            telemetry.FailureClass = responseHeadersReceived
                ? ReasoningCompletionFailureClass.TransportTruncation
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
            _completionTelemetry.Add(telemetry);
        }
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

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record ProviderEnvelope(string Content, string? FinishReason);

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
