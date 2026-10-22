using System.Net;
using DocxHeaderExtractor.HeadingAgent.Protocol;
using static DocxHeaderExtractor.HeadingAgent.Protocol.AgentProtocol;
using DocxHeaderExtractor.Agents.Budget;
using DocxHeaderExtractor.Agents.Transport;
using DocxHeaderExtractor.Agents;

namespace DocxHeaderExtractor.Tests;

/// <summary>A 429 is waited out and the same bytes are sent again, as many times as the model was told; by default it fails at once.</summary>
public sealed class AgentModelRateLimitTests
{
    /// <summary>A provider that answers every POST with 429 and counts them.</summary>
    sealed class Always429 : HttpMessageHandler
    {
        int posts;
        public int Posts => posts;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref posts);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("") });
        }
    }

    static PreparedRequest Request()
    {
        const string body = "{\"model\":\"m\",\"messages\":[{\"role\":\"system\",\"content\":\"s\"},{\"role\":\"user\",\"content\":\"u\"}]}";
        return new PreparedRequest(body, body.Length, Sha(body));
    }

    [Fact]
    public void A_429_is_sent_again_after_a_wait_until_the_retries_are_spent()
    {
        var handler = new Always429();
        using var model = new AgentModel(handler, "sk-test", new AgentBudget(10, 10), rateLimitRetries: 2, rateLimitBaseDelayMs: 1);

        var failure = Assert.Throws<ReplyFailure>(() => model.Turn("U", 1, Request()));

        Assert.Equal("TRANSPORT:RATE_LIMITED_NO_RETRY", failure.Code);
        Assert.Equal(3, handler.Posts);
        Assert.Equal(2, model.RateLimitWaits);
    }

    [Fact]
    public void By_default_a_429_fails_at_once()
    {
        var handler = new Always429();
        using var model = new AgentModel(handler, "sk-test", new AgentBudget(10, 10));

        Assert.Throws<ReplyFailure>(() => model.Turn("U", 1, Request()));

        Assert.Equal(1, handler.Posts);
        Assert.Equal(0, model.RateLimitWaits);
    }
}
