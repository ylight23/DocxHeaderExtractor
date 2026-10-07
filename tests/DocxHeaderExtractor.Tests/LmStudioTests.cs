using System.Net;
using System.Text;
using DocxHeaderExtractor.DocumentProcessing.Inference;

namespace DocxHeaderExtractor.Tests;

public sealed class LmStudioTests
{
    [Fact]
    public async Task Uses_loopback_openai_api_and_returns_trimmed_content()
    {
        var handler = new CaptureHandler(
            """{"choices":[{"message":{"content":"  {\"ok\":true}\n"}}]}""");
        using var http = new HttpClient(handler);
        using var model = new LmStudioInferenceTransport(http, new RemoteInferenceOptions
        {
            Model = "local/qwen",
            Endpoint = new Uri("http://127.0.0.1:1234/v1/chat/completions"),
        });

        var result = await model.BoundaryCutAsync("SYSTEM", "USER");

        Assert.Equal("{\"ok\":true}", result);
        Assert.Equal("http://127.0.0.1:1234/v1/chat/completions", handler.Uri?.ToString());
        Assert.Contains("\"model\":\"local/qwen\"", handler.Body);
        Assert.Contains("\"content\":\"SYSTEM\"", handler.Body);
        Assert.Contains("\"content\":\"USER\"", handler.Body);
        Assert.DoesNotContain("\"provider\"", handler.Body);
        Assert.Null(handler.AuthorizationScheme);
    }

    [Fact]
    public void Rejects_non_loopback_endpoint_to_prevent_ssrf()
    {
        using var http = new HttpClient(new CaptureHandler("{}"));

        var error = Assert.Throws<InvalidOperationException>(() =>
            new LmStudioInferenceTransport(http, new RemoteInferenceOptions
            {
                Model = "x",
                Endpoint = new Uri("http://example.com/v1/chat/completions"),
            }));

        Assert.Contains("loopback", error.Message);
    }

    [Fact]
    public async Task Api_key_is_optional_but_is_sent_when_configured()
    {
        var handler = new CaptureHandler(
            """{"choices":[{"message":{"content":"{\"items\":[{\"i\":1,\"r\":\"n\",\"l\":0}]}"}}]}""");
        using var http = new HttpClient(handler);
        using var model = new LmStudioInferenceTransport(http, new RemoteInferenceOptions
        {
            Model = "local/model",
            Endpoint = new Uri("http://127.0.0.1:1234/v1/chat/completions"),
            ApiKey = "local-test-token",
        });

        await model.BoundaryCutAsync("SYSTEM", "USER");

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
