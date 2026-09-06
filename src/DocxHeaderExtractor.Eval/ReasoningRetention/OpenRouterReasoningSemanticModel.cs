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

    public async Task<ReasoningModelResponse> CompleteAsync(
        ReasoningModelRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
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

        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        Interlocked.Increment(ref _providerCalls);
        var responseText = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"OpenRouter returned {(int)response.StatusCode} {response.ReasonPhrase}");

        var content = ExtractContent(responseText);
        var parsed = ReasoningModelResponseParser.Parse(content);
        return parsed with
        {
            RawResponseHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant(),
        };
    }

    private static string ExtractContent(string response)
    {
        using var document = JsonDocument.Parse(response);
        if (document.RootElement.TryGetProperty("choices", out var choices) &&
            choices.GetArrayLength() > 0 &&
            choices[0].TryGetProperty("message", out var message) &&
            message.TryGetProperty("content", out var content) &&
            content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? "";
        throw new FormatException("reasoning-provider-response-content-missing");
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
