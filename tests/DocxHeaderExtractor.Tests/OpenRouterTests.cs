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
            """{"choices":[{"message":{"content":"{\"h\":[{\"i\":42,\"r\":\"h\",\"l\":2}]}"}}]}""");
        using var http = new HttpClient(handler);
        using var model = new OpenRouterHeaderExtractor(http, new RemoteInferenceOptions { ApiKey = "test-key" });

        var result = await model.ClassifyAsync("<p i=\"42\">2. Mục</p>", [42]);

        Assert.Single(result.Headings);
        Assert.Equal(2, result.Headings[0].Level);
        Assert.Contains("\"zdr\":true", handler.Body);
        Assert.Contains("\"data_collection\":\"deny\"", handler.Body);
        Assert.Contains("\"require_parameters\":true", handler.Body);
        Assert.Contains("\"response_format\":{\"type\":\"json_object\"}", handler.Body);
        Assert.DoesNotContain("json_schema", handler.Body);
        Assert.Contains("\"model\":\"qwen/qwen3.5-9b\"", handler.Body);
        Assert.Contains("\"reasoning\":{\"effort\":\"none\"}", handler.Body);
        Assert.Contains("[42]", handler.Body);
        Assert.Contains("items", handler.Body);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("test-key", handler.AuthorizationParameter);
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
    public async Task Critic_uses_adversarial_semantic_prompt_and_can_reject_heading()
    {
        var handler = new CaptureHandler(
            """{"choices":[{"message":{"content":"{\"items\":[{\"i\":11,\"r\":\"f\",\"l\":0}]}"}}]}""");
        using var http = new HttpClient(handler);
        using var model = new OpenRouterHeaderExtractor(http, new RemoteInferenceOptions { ApiKey = "test-key" });

        var result = await model.CritiqueAsync("<p i=\"11\">Đơn vị A gửi đơn vị B</p>", [11]);

        Assert.Empty(result.Headings);
        Assert.Contains(11, result.ExplicitNonHeadings);
        using var request = JsonDocument.Parse(handler.Body);
        var system = request.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
        Assert.Contains("CHỦ ĐỘNG tìm phản ví dụ", system);
        Assert.Contains("MỞ RA phạm vi nội dung", system);
        Assert.Contains("từ khóa riêng lẻ", system);
    }

    [Fact]
    public async Task Missing_ids_are_retried_without_weakening_validation()
    {
        var handler = new CaptureHandler(
            """{"choices":[{"message":{"content":"{\"items\":[{\"i\":10,\"r\":\"h\",\"l\":1}]}"}}]}""",
            """{"choices":[{"message":{"content":"{\"items\":[{\"i\":20,\"r\":\"n\",\"l\":0}]}"}}]}""");
        using var http = new HttpClient(handler);
        using var model = new OpenRouterHeaderExtractor(http, new RemoteInferenceOptions
        {
            ApiKey = "test-key",
            MissingIdRetries = 2,
        });

        var result = await model.ClassifyAsync("<p i=\"10\">I. Mục</p><p i=\"20\">Nội dung</p>", [10, 20]);

        Assert.Single(result.Headings);
        Assert.Equal(10, result.Headings[0].Index);
        Assert.Contains(20, result.ExplicitNonHeadings);
        Assert.Equal(2, handler.RequestCount);
        Assert.Contains("[20]", handler.Bodies[1]);
        Assert.DoesNotContain("[10,20]", handler.Bodies[1]);
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

        await model.ClassifyAsync("<p i=\"42\">Heading</p>", [42]);

        Assert.Contains(logs, log => log.Contains("LLM REQUEST") && log.Contains("qwen/qwen3.5-9b"));
        Assert.Contains(logs, log => log.Contains("LLM RESPONSE") && log.Contains("choices"));
        Assert.DoesNotContain(logs, log => log.Contains("test-key"));
        Assert.DoesNotContain(logs, log => log.Contains("Authorization", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Missing_ids_still_fail_after_bounded_retries()
    {
        var handler = new CaptureHandler(
            """{"choices":[{"message":{"content":"{\"items\":[]}"}}]}""");
        using var http = new HttpClient(handler);
        using var model = new OpenRouterHeaderExtractor(http, new RemoteInferenceOptions
        {
            ApiKey = "test-key",
            MissingIdRetries = 1,
        });

        var error = await Assert.ThrowsAsync<FormatException>(() =>
            model.ClassifyAsync("<p i=\"10\">I. Mục</p>", [10]));

        Assert.Contains("ID còn thiếu=[10]", error.Message);
        Assert.Equal(2, handler.RequestCount);
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
