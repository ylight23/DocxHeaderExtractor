using System.Net;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Inference;

namespace DocxHeaderExtractor.Tests;

public sealed class OpenRouterTests
{
    [Fact]
    public async Task Request_enforces_privacy_and_json_output()
    {
        var handler = new CaptureHandler(
            """{"choices":[{"message":{"content":" {\"ok\":true} "}}]}""");
        using var http = new HttpClient(handler);
        using var model = new OpenRouterHeaderExtractor(http, new RemoteInferenceOptions { ApiKey = "test-key" });

        var result = await model.BoundaryCutAsync("Return JSON.", "{\"sourceParts\":[]}");

        Assert.Equal("{\"ok\":true}", result);
        // The ZDR flag must always be written, never omitted: an omitted field inherits the
        // account's privacy default, which silently rejects every endpoint of the controlled
        // models. Its value follows RemoteInferenceOptions.RequireZeroDataRetention (false here).
        Assert.Contains("\"zdr\":false", handler.Body);
        Assert.Contains("\"data_collection\":\"deny\"", handler.Body);
        Assert.Contains("\"require_parameters\":true", handler.Body);
        Assert.Contains("\"response_format\":{\"type\":\"json_object\"}", handler.Body);
        Assert.DoesNotContain("json_schema", handler.Body);
        Assert.Contains("\"model\":\"qwen/qwen3.5-9b\"", handler.Body);
        Assert.Contains("\"reasoning\":{\"effort\":\"none\"}", handler.Body);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("test-key", handler.AuthorizationParameter);
    }

    [Fact]
    public async Task Boundary_cut_keeps_configured_output_budget_cap_for_32_role_ids()
    {
        var handler = new CaptureHandler(
            """{"choices":[{"message":{"content":"{}"}}]}""");
        using var http = new HttpClient(handler);
        using var model = new OpenRouterHeaderExtractor(http, new RemoteInferenceOptions
        {
            ApiKey = "test-key",
            MaxOutputTokens = 768,
        });

        var blocks = Enumerable.Range(0, 32).Select(index =>
            $"{{\"id\":\"DOC-0116:source-paragraph-{index:D3}-long-stable-id\",\"source_text\":\"A realistic source paragraph payload for output-budget testing.\",\"source_length\":64}}");
        var user = $"{{\"blocks\":[{string.Join(',', blocks)}]}}";
        await model.BoundaryCutAsync("role system. Return JSON.", user);

        using var request = JsonDocument.Parse(handler.Body);
        using var materialized = JsonDocument.Parse(request.RootElement.GetProperty("messages")[1]
            .GetProperty("content").GetString()!);
        Assert.Equal(32, materialized.RootElement.GetProperty("blocks").GetArrayLength());
        Assert.Equal(768, request.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task Reasoning_effort_is_explicitly_selectable_for_an_authorized_experiment()
    {
        var handler = new CaptureHandler(
            """{"choices":[{"message":{"content":"{}"}}]}""");
        using var http = new HttpClient(handler);
        using var model = new OpenRouterHeaderExtractor(http, new RemoteInferenceOptions
        {
            ApiKey = "test-key",
            OpenRouterReasoningEffort = "medium",
        });

        await model.BoundaryCutAsync("Return JSON.", "{\"sourceParts\":[]}");

        Assert.Contains("\"reasoning\":{\"effort\":\"medium\"}", handler.Body);
    }

    [Fact]
    public void Missing_api_key_fails_before_any_request()
    {
        using var http = new HttpClient(new CaptureHandler("{}"));
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new OpenRouterHeaderExtractor(http, new RemoteInferenceOptions()));

        Assert.Contains("OPENROUTER_API_KEY", ex.Message);
    }

    [Fact]
    public async Task Explicit_debug_log_exposes_provider_exchange_without_authorization_header()
    {
        var handler = new CaptureHandler(
            """{"choices":[{"message":{"content":"{\"items\":[{\"i\":42,\"r\":\"h\",\"l\":2}]}"}}]}""");
        var logs = new List<string>();
        using var http = new HttpClient(handler);
        using var model = new OpenRouterHeaderExtractor(http, new RemoteInferenceOptions
        {
            ApiKey = "test-key",
            DebugLog = logs.Add,
        });

        await model.BoundaryCutAsync("Return JSON.", "{\"sourceParts\":[]}");

        Assert.Contains(logs, log => log.Contains("LLM REQUEST") && log.Contains("qwen/qwen3.5-9b"));
        Assert.Contains(logs, log => log.Contains("LLM RESPONSE") && log.Contains("choices"));
        Assert.DoesNotContain(logs, log => log.Contains("test-key"));
        Assert.DoesNotContain(logs, log => log.Contains("Authorization", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class CaptureHandler(params string[] responses) : HttpMessageHandler
    {
        private readonly string[] _responses = responses;

        public List<string> Bodies { get; } = [];
        public string Body => Bodies.LastOrDefault() ?? "";
        public int RequestCount => Bodies.Count;
        public string? AuthorizationScheme { get; private set; }
        public string? AuthorizationParameter { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    _responses[Math.Min(RequestCount - 1, _responses.Length - 1)],
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }
}
