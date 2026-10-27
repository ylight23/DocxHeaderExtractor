using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using DocxHeaderExtractor.Agents.Budget;
using DocxHeaderExtractor.Agents.Transport;
using DocxHeaderExtractor.HeadingAgent.Placement;
using DocxHeaderExtractor.HeadingAgent.Protocol;
using DocxHeaderExtractor.HeadingAgent.Runtime;
using DocxHeaderExtractor.DocumentProcessing.Placement;

namespace DocxHeaderExtractor.Tests;

public sealed class ProviderRetryTests
{
    static DocxHeaderExtractor.Agents.PreparedRequest Request()
    {
        const string body = "{\"messages\":[],\"max_tokens\":256}";
        return new(body, body.Length, AgentProtocol.Sha(body));
    }

    static HttpResponseMessage Response(HttpStatusCode status, string body = "", TimeSpan? retryAfter = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body) };
        if (retryAfter is { } delay) response.Headers.RetryAfter = new RetryConditionHeaderValue(delay);
        return response;
    }

    internal sealed class Handler(Func<int, HttpResponseMessage> script) : HttpMessageHandler
    {
        int posts;
        public int Posts => posts;
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Bodies) Bodies.Add(request.Content!.ReadAsStringAsync(ct).GetAwaiter().GetResult());
            return await Task.FromResult(script(Interlocked.Increment(ref posts)));
        }
    }

    sealed class BrokenBody : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.FromException(new IOException("broken response body"));
    }

    sealed class WaitingBody : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new NotSupportedException();
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct) =>
            Task.Delay(Timeout.InfiniteTimeSpan, ct);
    }

    [Fact]
    public void Mixed_transient_failures_share_one_limit_and_keep_identical_request_bytes()
    {
        using var handler = new Handler(n => n switch
        {
            1 => Response(HttpStatusCode.TooManyRequests),
            2 => Response(HttpStatusCode.ServiceUnavailable),
            3 => Response(HttpStatusCode.OK, "data: {\"error\":{\"code\":429}}\n\n"),
            _ => Response(HttpStatusCode.TooManyRequests)
        });
        var budget = new AgentBudget(20, 10);
        using var model = new AgentModel(handler, "test", budget, rateLimitRetries: 3, rateLimitBaseDelayMs: 0, requestGate: new(6));
        var failure = Assert.Throws<ReplyFailure>(() => model.Turn("GROUP-001", 2, Request()));
        Assert.True(failure.TransportRetriesHandled);
        Assert.Equal(4, handler.Posts);
        Assert.All(handler.Bodies, b => Assert.Equal(Request().Body, b));
        Assert.Equal(new int?[] { 429, 503, 200, 429 }, budget.Attempts.Select(a => a.Transport!.HttpStatus));
        Assert.Equal(new[] { 0, 1, 2, 3 }, budget.Attempts.Select(a => a.Transport!.RetryIndex));
        Assert.All(budget.Attempts, a => { Assert.Equal("GROUP-001", a.Transport!.UnitId); Assert.Equal(2, a.Transport.Turn); });
        Assert.Equal(2, budget.Attempts.Count(a => a.Status == "BILLING_UNKNOWN"));
        Assert.Equal(429, budget.Attempts[2].Transport!.ProviderErrorCode);
        Assert.Equal("STREAM_ERROR", budget.Attempts[2].Transport!.Outcome);
    }

    [Fact]
    public void Retry_after_is_respected_before_the_next_dispatch()
    {
        var times = new List<long>();
        using var handler = new Handler(n =>
        {
            times.Add(Stopwatch.GetTimestamp());
            return n == 1 ? Response(HttpStatusCode.TooManyRequests, retryAfter: TimeSpan.FromSeconds(1)) :
                Response(HttpStatusCode.OK, FakeOpenRouter.Stream(new { content = "{}" }, "stop").Replace("PROVIDER", "Alibaba"));
        });
        var budget = new AgentBudget(10, 10);
        using var model = new AgentModel(handler, "test", budget, rateLimitRetries: 2, rateLimitBaseDelayMs: 0, requestGate: new(6));
        Assert.Equal("{}", model.Turn("U", 1, Request()).Content);
        Assert.Equal(2, handler.Posts);
        Assert.True(Stopwatch.GetElapsedTime(times[0], times[1]) >= TimeSpan.FromMilliseconds(990));
        Assert.Equal(1d, budget.Attempts[0].Transport!.RetryAfterSeconds);
        Assert.True(budget.Attempts[0].Transport!.RetryDelaySeconds >= 0.99);
        Assert.Equal("HTTP_OK", budget.Attempts[1].Transport!.Outcome);
    }

    [Fact]
    public void Retry_after_HTTP_dates_and_expired_dates_are_parsed()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(TimeSpan.FromSeconds(7), AgentModel.RetryAfter(new(now.AddSeconds(7)), now));
        Assert.Equal(TimeSpan.Zero, AgentModel.RetryAfter(new(now.AddSeconds(-1)), now));
        Assert.Null(AgentModel.RetryAfter(null, now));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, 4)]
    [InlineData(HttpStatusCode.BadRequest, 1)]
    public void HTTP_status_and_retry_after_survive_a_broken_response_body(HttpStatusCode status, int posts)
    {
        using var handler = new Handler(_ => new HttpResponseMessage(status)
        { Content = new BrokenBody(), Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero) } });
        var budget = new AgentBudget(20, 10);
        using var model = new AgentModel(handler, "test", budget, rateLimitRetries: 3, requestGate: new(6));
        Assert.Throws<ReplyFailure>(() => model.Turn("U", 1, Request()));
        Assert.Equal(posts, handler.Posts);
        Assert.All(budget.Attempts, a =>
        {
            Assert.Equal((int)status, a.Transport!.HttpStatus);
            Assert.Equal(0d, a.Transport.RetryAfterSeconds);
        });
    }

    [Fact]
    public void Attempt_timeout_covers_the_body_and_retains_unknown_billing()
    {
        using var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new WaitingBody() });
        var budget = new AgentBudget(20, 10);
        using var model = new AgentModel(handler, "test", budget, timeout: TimeSpan.FromMilliseconds(50),
            rateLimitRetries: 3, requestGate: new(6));
        var failure = Assert.Throws<ReplyFailure>(() => model.Turn("U", 1, Request()));
        Assert.Equal("TRANSPORT:TIMEOUT_OR_CANCELLED", failure.Code);
        Assert.Equal(1, handler.Posts);
        Assert.Equal("BILLING_UNKNOWN", Assert.Single(budget.Attempts).Status);
        Assert.Equal(200, budget.Attempts[0].Transport!.HttpStatus);
        Assert.True(budget.ReservedUsd > 0);
    }

    [Fact]
    public void A_long_retry_after_stops_this_turn_and_blocks_other_turns_without_sending_early()
    {
        using var handler = new Handler(_ => Response(HttpStatusCode.TooManyRequests, retryAfter: TimeSpan.FromSeconds(90)));
        var budget = new AgentBudget(20, 10);
        var gate = new ProviderRequestGate(6);
        using var model = new AgentModel(handler, "test", budget, rateLimitRetries: 3, retryWaitBudget: TimeSpan.FromSeconds(2), requestGate: gate);
        Assert.True(Assert.Throws<ReplyFailure>(() => model.Turn("U1", 1, Request())).TransportRetriesHandled);
        var other = Assert.Throws<ReplyFailure>(() => model.Turn("U2", 1, Request()));
        Assert.Equal("TRANSPORT:PROVIDER_WAIT_LIMIT", other.Code);
        Assert.True(other.TransportRetriesHandled);
        Assert.Equal(1, handler.Posts);
        Assert.Equal("PROVIDER_WAIT_LIMIT", budget.Attempts[1].Transport!.Outcome);
        Assert.Equal(0, budget.ReservedUsd);
        Assert.Equal(0, budget.Attempts[1].CostUsd);
    }

    [Fact]
    public void Cancellation_interrupts_retry_wait_without_another_attempt()
    {
        using var cts = new CancellationTokenSource();
        using var handler = new Handler(_ =>
        {
            cts.CancelAfter(50);
            return Response(HttpStatusCode.TooManyRequests, retryAfter: TimeSpan.FromSeconds(10));
        });
        var budget = new AgentBudget(20, 10);
        using var model = new AgentModel(handler, "test", budget, cancellationToken: cts.Token, rateLimitRetries: 3, requestGate: new(6));
        Assert.ThrowsAny<OperationCanceledException>(() => model.Turn("U", 1, Request()));
        Assert.Equal(1, handler.Posts);
        Assert.Single(budget.Attempts);
        Assert.Equal(0, budget.ReservedUsd);
    }

    [Fact]
    public void Queue_cancellation_releases_the_unsent_reservation_and_does_not_steal_a_permit()
    {
        var gate = new ProviderRequestGate(1);
        using var occupied = gate.Enter(TimeSpan.FromSeconds(1), CancellationToken.None);
        using var cts = new CancellationTokenSource(50);
        using var handler = new Handler(_ => Response(HttpStatusCode.OK));
        var budget = new AgentBudget(20, 10);
        using var model = new AgentModel(handler, "test", budget, cancellationToken: cts.Token, rateLimitRetries: 3, requestGate: gate);
        Assert.ThrowsAny<OperationCanceledException>(() => model.Turn("U", 1, Request()));
        Assert.Equal(0, handler.Posts);
        Assert.Equal(0, budget.ReservedUsd);
        Assert.Equal("CANCELLED", Assert.Single(budget.Attempts).Transport!.Outcome);
        Assert.Throws<ProviderRequestGate.ReplyWaitLimit>(() => gate.Enter(TimeSpan.FromMilliseconds(1), CancellationToken.None));
    }

    [Fact]
    public void Models_for_the_same_account_share_a_gate_and_other_accounts_are_isolated()
    {
        var account = Guid.NewGuid().ToString();
        Assert.Same(ProviderRequestGate.ForAccount(account), ProviderRequestGate.ForAccount(account));
        Assert.NotSame(ProviderRequestGate.ForAccount(account), ProviderRequestGate.ForAccount(account + "other"));
    }

    [Fact]
    public async Task Shared_gate_limits_inflight_requests_across_model_instances()
    {
        var gate = new ProviderRequestGate(1);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var handler = new Handler(n =>
        {
            if (n == 1) { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); }
            return Response(HttpStatusCode.PaymentRequired);
        });
        using var first = new AgentModel(handler, "test", new AgentBudget(10, 10), rateLimitRetries: 3, requestGate: gate);
        using var second = new AgentModel(handler, "test", new AgentBudget(10, 10), rateLimitRetries: 3, requestGate: gate);
        var active = Task.Run(() => Assert.Throws<ReplyFailure>(() => first.Turn("U1", 1, Request())));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            using var cts = new CancellationTokenSource(50);
            using var waiting = new AgentModel(handler, "test", new AgentBudget(10, 10), cancellationToken: cts.Token,
                rateLimitRetries: 3, requestGate: gate);
            Assert.ThrowsAny<OperationCanceledException>(() => waiting.Turn("U2", 1, Request()));
            Assert.Equal(1, handler.Posts);
        }
        finally { release.Set(); }
        await active;
        Assert.Throws<ReplyFailure>(() => second.Turn("U3", 1, Request()));
        Assert.Equal(2, handler.Posts);
    }

    [Theory]
    [InlineData(HttpStatusCode.PaymentRequired)]
    [InlineData(HttpStatusCode.BadRequest)]
    public void Permanent_HTTP_failures_are_not_retried_or_exposed_as_raw_messages(HttpStatusCode status)
    {
        using var handler = new Handler(_ => Response(status, "{\"error\":{\"code\":\"private-source-text\",\"message\":\"test secret\"}}"));
        var budget = new AgentBudget(20, 10);
        using var model = new AgentModel(handler, "test", budget, rateLimitRetries: 3, rateLimitBaseDelayMs: 0, requestGate: new(6));
        Assert.Throws<ReplyFailure>(() => model.Turn("U", 1, Request()));
        Assert.Equal(1, handler.Posts);
        Assert.DoesNotContain("private-source-text", JsonSerializer.Serialize(budget.Attempts[0].Transport));
    }

    [Fact]
    public async Task Placement_does_not_restart_the_transports_exhausted_retry_sequence()
    {
        using var handler = new Handler(_ => Response(HttpStatusCode.TooManyRequests, retryAfter: TimeSpan.Zero));
        var (bound, evidence, _) = HeadingPlacerTests.Fixture();
        var result = await ParentResolver.PlaceAsync(bound.Take(1).ToArray(), new HeadingPlacer(handler),
            PlacementScript.Access, evidence, CancellationToken.None);
        Assert.Equal(1 + AgentModel.ProductionRateLimitRetries, handler.Posts);
        Assert.Single(result.Observation.Calls);
        Assert.Equal(1, result.Observation.UnresolvedCount);
        Assert.All(result.Observation.Resources!.Attempts, a => Assert.Equal(429, a.Transport!.HttpStatus));
    }
}
