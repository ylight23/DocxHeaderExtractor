using System.Net;
using DocxHeaderExtractor.DocumentProcessing.Vision;

namespace DocxHeaderExtractor.Tests;

public sealed class OpenRouterVisualQuestionTests
{
    [Fact]
    public async Task DoesNotRetryPaymentRequired()
    {
        var handler = new StatusHandler(HttpStatusCode.PaymentRequired);
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using var question = new OpenRouterVisualQuestion(
            new Uri("https://example.invalid/v1/chat/completions"), "test", "qwen/qwen3.5-9b", 10, 3, http);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            question.AskAsync([1, 2, 3], "classify", ct: CancellationToken.None));

        Assert.Equal(HttpStatusCode.PaymentRequired, error.StatusCode);
        Assert.Equal(1, handler.RequestCount);
        var attempt = Assert.Single(question.LastAttemptOutcomes);
        Assert.Equal("failed", attempt.Status);
        Assert.Equal(402, attempt.HttpStatus);
    }

    private sealed class StatusHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(statusCode));
        }
    }
}
