using System.Net;
using System.Text;
using DocxHeaderExtractor.Core.Vision;

namespace DocxHeaderExtractor.Tests;

public sealed class SglangVlmImageQuestionTests
{
    [Fact]
    public async Task Sends_openai_image_url_and_returns_content()
    {
        var handler = new CaptureHandler("{\"choices\":[{\"message\":{\"content\":\"{\\\"id\\\":\\\"l1\\\",\\\"role\\\":\\\"heading_topic\\\"}\"}}]}");
        using var http = new HttpClient(handler);
        using var vlm = new SglangVlmImageQuestion(
            http,
            new Uri("http://example.test/v1/chat/completions"),
            "Qwen/Qwen3.6-27B",
            "test-key");

        var result = await vlm.AskAsync([1, 2, 3], "Classify candidate l1", 128);

        Assert.Contains("heading_topic", result);
        Assert.Contains("\"type\":\"image_url\"", handler.Body);
        Assert.Contains("data:image/png;base64,AQID", handler.Body);
        Assert.Contains("\"response_format\":{\"type\":\"json_object\"}", handler.Body);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("test-key", handler.AuthorizationParameter);
    }

    [Fact]
    public async Task Sends_each_crop_as_an_independent_openai_image_part()
    {
        var handler = new CaptureHandler("{\"choices\":[{\"message\":{\"content\":\"{\\\"blocks\\\":[]}\"}}]}");
        using var http = new HttpClient(handler);
        using var vlm = new SglangVlmImageQuestion(http, new Uri("http://example.test/v1/chat/completions"), "vision", "", maximumImagesPerRequest: 4);

        await vlm.AskManyAsync([[1], [2, 3]], "Classify l1 and l2", 128);

        Assert.Contains("data:image/png;base64,AQ==", handler.Body);
        Assert.Contains("data:image/png;base64,AgM=", handler.Body);
        Assert.Equal(2, handler.Body.Split("\"type\":\"image_url\"").Length - 1);
    }

    private sealed class CaptureHandler(string response) : HttpMessageHandler
    {
        public string Body { get; private set; } = "";
        public string? AuthorizationScheme { get; private set; }
        public string? AuthorizationParameter { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            };
        }
    }
}
