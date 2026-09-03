using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Core.Llm;

/// <summary>OpenRouter transport adapter. Parsing and authority remain outside this class.</summary>
public sealed class OpenRouterFactProposalModel : IFactProposalModel
{
    private readonly HttpClient _http;
    private readonly OpenRouterOptions _options;

    public OpenRouterFactProposalModel(HttpClient http, OpenRouterOptions options)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    public async Task<string> CompleteAsync(
        FactProposalModelRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var body = new
        {
            model = _options.Model,
            temperature = 0,
            max_tokens = _options.MaxOutputTokens,
            reasoning = new { effort = "none" },
            messages = new[]
            {
                new { role = "system", content = FactProposalModelPrompt.System },
                new { role = "user", content = FactProposalModelPrompt.BuildUser(request) },
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
        message.Headers.TryAddWithoutValidation("X-Title", "DocxHeaderExtractor fact proposal");
        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, "OpenRouter");
        return ExtractContent(responseText, "OpenRouter");
    }

    private static void EnsureSuccess(HttpResponseMessage response, string provider)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"{provider} fact proposal request failed with {(int)response.StatusCode} {response.ReasonPhrase}",
                null,
                response.StatusCode);
    }

    internal static string ExtractContent(string responseText, string provider)
    {
        try
        {
            using var document = JsonDocument.Parse(responseText);
            var content = document.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();
            if (string.IsNullOrWhiteSpace(content))
                throw new FormatException($"{provider} response has empty message.content.");
            return content;
        }
        catch (FormatException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or IndexOutOfRangeException)
        {
            throw new FormatException($"{provider} response has no choices[0].message.content.", exception);
        }
    }
}

/// <summary>SGLang/vLLM transport adapter with a dynamic closed JSON schema.</summary>
public sealed class SglangFactProposalModel : IFactProposalModel
{
    private readonly HttpClient _http;
    private readonly SglangOptions _options;

    public SglangFactProposalModel(HttpClient http, SglangOptions options)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    public async Task<string> CompleteAsync(
        FactProposalModelRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var body = new Dictionary<string, object?>
        {
            ["model"] = _options.Model,
            ["temperature"] = 0,
            ["max_tokens"] = _options.MaxOutputTokens,
            ["stream"] = false,
            ["messages"] = new[]
            {
                new { role = "system", content = FactProposalModelPrompt.System },
                new { role = "user", content = FactProposalModelPrompt.BuildUser(request) },
            },
            ["chat_template_kwargs"] = new { enable_thinking = false },
            ["response_format"] = new
            {
                type = "json_schema",
                json_schema = new
                {
                    name = "fact_proposal",
                    strict = true,
                    schema = BuildClosedSchema(request),
                },
            },
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
        {
            Content = JsonContent.Create(body),
        };
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"SGLang fact proposal request failed with {(int)response.StatusCode} {response.ReasonPhrase}",
                null,
                response.StatusCode);
        return OpenRouterFactProposalModel.ExtractContent(responseText, "SGLang");
    }

    private static object BuildClosedSchema(FactProposalModelRequest request) => new
    {
        type = "object",
        additionalProperties = false,
        properties = new
        {
            proposals = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    additionalProperties = false,
                    properties = new
                    {
                        proposalId = new { type = "string" },
                        contextChunkId = new { type = "string", @enum = new[] { request.ContextChunkId } },
                        schemaKey = new { type = "string", @enum = new[] { request.Schema.Key } },
                        fields = new
                        {
                            type = "array",
                            items = new
                            {
                                type = "object",
                                additionalProperties = false,
                                properties = new
                                {
                                    fieldName = new { type = "string", @enum = request.Schema.Fields.Select(field => field.Name).ToArray() },
                                    sourceId = new { type = "string", @enum = request.Sources.Select(source => source.SourceId).Distinct().ToArray() },
                                    span = new
                                    {
                                        type = "object",
                                        additionalProperties = false,
                                        properties = new
                                        {
                                            start = new { type = "integer", minimum = 0 },
                                            end = new { type = "integer", minimum = 1 },
                                        },
                                        required = new[] { "start", "end" },
                                    },
                                },
                                required = new[] { "fieldName", "sourceId", "span" },
                            },
                        },
                        confidence = new { type = "number" },
                    },
                    required = new[] { "proposalId", "contextChunkId", "schemaKey", "fields" },
                },
            },
        },
        required = new[] { "proposals" },
    };
}
