using System.Net;
using System.Text;
using DocxHeaderExtractor.Core.Llm;

namespace DocxHeaderExtractor.Tests;

public sealed class LmStudioTests
{
    [Fact]
    public async Task Uses_loopback_openai_api_with_structured_schema()
    {
        var handler = new CaptureHandler(
            """{"choices":[{"message":{"content":"{\"items\":[{\"i\":42,\"r\":\"h\",\"l\":2}]}"}}]}""");
        using var http = new HttpClient(handler);
        using var model = new LmStudioHeaderExtractor(http, new LmStudioOptions
        {
            Model = "local/qwen",
        });

        var result = await model.ClassifyAsync("DOCUMENT_VIEW", [42]);

        Assert.Single(result.Headings);
        Assert.Equal(2, result.Headings[0].Level);
        Assert.Equal("http://127.0.0.1:1234/v1/chat/completions", handler.Uri?.ToString());
        Assert.Contains("\"model\":\"local/qwen\"", handler.Body);
        Assert.Contains("\"type\":\"json_schema\"", handler.Body);
        Assert.Contains("\"minItems\":1", handler.Body);
        Assert.DoesNotContain("\"provider\"", handler.Body);
        Assert.Null(handler.AuthorizationScheme);
    }

    [Fact]
    public void Rejects_non_loopback_endpoint_to_prevent_ssrf()
    {
        using var http = new HttpClient(new CaptureHandler("{}"));

        var error = Assert.Throws<InvalidOperationException>(() =>
            new LmStudioHeaderExtractor(http, new LmStudioOptions
            {
                Model = "x",
                Endpoint = new Uri("http://example.com/v1/chat/completions"),
            }));

        Assert.Contains("loopback", error.Message);
    }

    [Fact]
    public async Task Retries_only_missing_ids_and_keeps_explicit_rejection()
    {
        var handler = new CaptureHandler(
            """{"choices":[{"message":{"content":"{\"items\":[{\"i\":10,\"r\":\"h\",\"l\":1}]}"}}]}""",
            """{"choices":[{"message":{"content":"{\"items\":[{\"i\":20,\"r\":\"n\",\"l\":0}]}"}}]}""");
        using var http = new HttpClient(handler);
        using var model = new LmStudioHeaderExtractor(http, new LmStudioOptions
        {
            Model = "local/model",
            MissingIdRetries = 2,
        });

        var result = await model.ClassifyAsync("DOCUMENT_VIEW", [10, 20]);

        Assert.Single(result.Headings);
        Assert.Contains(20, result.ExplicitNonHeadings);
        Assert.Equal(2, handler.Bodies.Count);
        Assert.Contains("[20]", handler.Bodies[1]);
    }

    [Fact]
    public async Task Api_key_is_optional_but_is_sent_when_configured()
    {
        var handler = new CaptureHandler(
            """{"choices":[{"message":{"content":"{\"items\":[{\"i\":1,\"r\":\"n\",\"l\":0}]}"}}]}""");
        using var http = new HttpClient(handler);
        using var model = new LmStudioHeaderExtractor(http, new LmStudioOptions
        {
            Model = "local/model",
            ApiKey = "local-test-token",
        });

        await model.ClassifyAsync("DOCUMENT_VIEW", [1]);

        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("local-test-token", handler.AuthorizationParameter);
    }

    private sealed class CaptureHandler(params string[] responses) : HttpMessageHandler
    {
        private readonly string[] _responses = responses;
        public List<string> Bodies { get; } = [];
        public string Body => Bodies.LastOrDefault() ?? "";
        public Uri? Uri { get; private set; }
        public string? AuthorizationScheme { get; private set; }
        public string? AuthorizationParameter { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            var response = _responses.Length == 0 ? "{}" : _responses[Math.Min(Bodies.Count - 1, _responses.Length - 1)];
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            };
        }
    }
}
