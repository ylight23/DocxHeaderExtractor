using System.Net;
using System.Text;
using DocxHeaderExtractor.Core.Vision;

namespace DocxHeaderExtractor.Tests;

public sealed class NvidiaNimVisualQuestionTests
{
    [Fact]
    public async Task RetriesTransientGatewayFailureThenReturnsModelContent()
    {
        var handler = new SequenceHandler(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            Json("{\"choices\":[{\"message\":{\"content\":\"{\\\"role\\\":\\\"heading\\\"}\"}}]}"));
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using var question = new NvidiaNimVisualQuestion(
            new Uri("https://example.invalid/v1/chat/completions"), "test", "vision", 10, 1, http);

        var answer = await question.AskAsync([1, 2, 3], "classify", ct: CancellationToken.None);

        Assert.Equal("{\"role\":\"heading\"}", answer);
        Assert.Equal(2, handler.RequestCount);
    }

    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json"),
    };

    private sealed class SequenceHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(_responses.Dequeue());
        }
    }
}
