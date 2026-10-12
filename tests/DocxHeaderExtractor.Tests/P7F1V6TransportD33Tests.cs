using System.Net;
using System.Net.Http;
using System.Text;
using DocxHeaderExtractor.V5Qualification.P7;

namespace DocxHeaderExtractor.Tests;

/// <summary>Issue #9 D3.3 (offline): the transport against a mock HttpMessageHandler. No connection is opened.</summary>
public sealed class P7F1V6TransportD33Tests
{
    const string Key = "test-key-not-real";

    /// <summary>Records every request body that reaches the handler, and answers with a fixed response or throws.</summary>
    sealed class MockHandler : HttpMessageHandler
    {
        public Func<HttpResponseMessage>? Respond;
        public Exception? Throw;
        public int Calls;
        public readonly List<byte[]> Bodies = new();
        public readonly List<string?> Authorization = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Bodies.Add(await request.Content!.ReadAsByteArrayAsync(cancellationToken));
            Authorization.Add(request.Headers.Authorization?.ToString());
            if (Throw is not null) throw Throw;
            return Respond!();
        }
    }

    static P7F1V6Protocol.PreparedRequest Prepared(string payload = "{\"owned\":[\"Trích xuất\"]}") =>
        P7F1V6Protocol.Prepare(P7F1V6Protocol.SystemPrompt(P7F1V6Hier.Kind.Local), payload, "alibaba", 32768);

    static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    const string Ok = "{\"choices\":[{\"index\":0,\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"content\":\"{}\"}}]," +
        "\"usage\":{\"prompt_tokens\":2000,\"completion_tokens\":300,\"completion_tokens_details\":{\"reasoning_tokens\":120}}}";

    [Fact] public void The_bytes_the_handler_receives_are_the_prepared_bytes_and_the_boundary_hash_matches()
    {
        var h = new MockHandler { Respond = () => Json(HttpStatusCode.OK, Ok) };
        var t = new P7F1V6OpenRouterTransport(h, TimeSpan.FromSeconds(30));
        var p = Prepared();
        var r = t.Send(p, Key);
        Assert.Equal("HTTP_OK", r.Outcome);
        Assert.Equal(1, h.Calls);
        Assert.Equal(p.Body, Encoding.UTF8.GetString(h.Bodies[0]));
        Assert.Equal(p.Sha256, r.BoundarySha256);
        Assert.Equal(p.Bytes, r.BoundaryBytes);
    }

    [Fact] public void The_key_goes_only_into_the_authorization_header_and_never_into_the_result()
    {
        var h = new MockHandler { Respond = () => Json(HttpStatusCode.OK, Ok) };
        var r = new P7F1V6OpenRouterTransport(h, TimeSpan.FromSeconds(30)).Send(Prepared(), Key);
        Assert.Equal("Bearer " + Key, h.Authorization[0]);
        Assert.DoesNotContain(Key, r.Body ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain(Key, r.Outcome, StringComparison.Ordinal);
        Assert.DoesNotContain(Key, r.BoundarySha256 ?? "", StringComparison.Ordinal);
    }

    [Fact] public void A_prepared_request_whose_hash_does_not_match_is_never_sent()
    {
        var h = new MockHandler { Respond = () => Json(HttpStatusCode.OK, Ok) };
        var p = Prepared() with { Sha256 = new string('0', 64) };
        var r = new P7F1V6OpenRouterTransport(h, TimeSpan.FromSeconds(30)).Send(p, Key);
        Assert.Equal("PREPARED_SHA_MISMATCH", r.Outcome);
        Assert.Equal(0, h.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Found, "REDIRECT_REFUSED")]
    [InlineData(HttpStatusCode.BadRequest, "CLIENT_ERROR")]
    [InlineData((HttpStatusCode)429, "RATE_LIMITED_NO_RETRY")]
    [InlineData(HttpStatusCode.InternalServerError, "SERVER_ERROR_NO_RETRY")]
    [InlineData(HttpStatusCode.BadGateway, "SERVER_ERROR_NO_RETRY")]
    public void Each_non_success_status_is_classified_and_sent_once(HttpStatusCode code, string outcome)
    {
        var h = new MockHandler { Respond = () => Json(code, "{\"error\":\"x\"}") };
        var r = new P7F1V6OpenRouterTransport(h, TimeSpan.FromSeconds(30)).Send(Prepared(), Key);
        Assert.Equal(outcome, r.Outcome);
        Assert.Equal(1, h.Calls);
        Assert.Equal(1, r.Attempts);
    }

    [Fact] public void A_timeout_or_cancellation_is_reported_and_not_retried()
    {
        var h = new MockHandler { Throw = new TaskCanceledException("timeout") };
        var r = new P7F1V6OpenRouterTransport(h, TimeSpan.FromSeconds(30)).Send(Prepared(), Key);
        Assert.Equal("TIMEOUT_OR_CANCELLED", r.Outcome);
        Assert.Equal(1, h.Calls);
        Assert.Null(r.Status);
    }

    [Fact] public void A_transport_failure_is_reported_and_not_retried()
    {
        var h = new MockHandler { Throw = new HttpRequestException("connection reset") };
        var r = new P7F1V6OpenRouterTransport(h, TimeSpan.FromSeconds(30)).Send(Prepared(), Key);
        Assert.Equal("TRANSPORT_ERROR", r.Outcome);
        Assert.Equal(1, h.Calls);
    }

    [Fact] public void The_production_handler_does_not_follow_redirects_or_keep_cookies()
    {
        var handler = P7F1V6OpenRouterTransport.CreateProductionHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        handler.Dispose();
    }

    [Fact] public void A_200_response_without_usage_is_audited_as_USAGE_MISSING()
    {
        var h = new MockHandler { Respond = () => Json(HttpStatusCode.OK, "{\"choices\":[{\"index\":0,\"finish_reason\":\"stop\",\"message\":{\"content\":\"{}\"}}]}") };
        var r = new P7F1V6OpenRouterTransport(h, TimeSpan.FromSeconds(30)).Send(Prepared(), Key);
        Assert.Equal("HTTP_OK", r.Outcome);
        var audit = P7F1V6Usage.Check(r.Body!, Prepared().Bytes, 32768);
        Assert.Contains("USAGE_MISSING", audit.Codes);
    }

    [Fact] public void A_200_response_with_usage_is_audited_and_its_cost_is_kept_apart()
    {
        var h = new MockHandler { Respond = () => Json(HttpStatusCode.OK, Ok) };
        var r = new P7F1V6OpenRouterTransport(h, TimeSpan.FromSeconds(30)).Send(Prepared(), Key);
        var audit = P7F1V6Usage.Check(r.Body!, Prepared().Bytes, 32768);
        Assert.Equal(new[] { "OK" }, audit.Codes);
        Assert.Equal(120, audit.ReasoningTokens);
        Assert.Null(audit.ProviderCostUsd);
        Assert.NotNull(audit.ComputedCostUsd);
    }

    [Fact] public void Every_model_call_sends_exactly_the_prepared_bytes_through_the_boundary()
    {
        var cands = new P7F1V6Hier.Cand[] { new("L0001:S0", 1, "Intro"), new("L0002:S0", 1, "Body") };
        var opts = new P7F1V6Hier.Options("Trích xuất các mục có tiêu đề", "src-sha", ShardSize: 2, Context: 1, FanIn: 3, PassUpCap: 8,
            SourceUniverseSha256: "u", ParserVersion: "p", EvidenceStoreVersion: "e");
        var h = new MockHandler { Respond = () => Json(HttpStatusCode.OK, Ok) };
        var t = new P7F1V6OpenRouterTransport(h, TimeSpan.FromSeconds(30));
        var calls = new List<(string Prepared, string Boundary, string Outcome)>();
        var run = P7F1V6Hier.Run(cands, opts, (P7F1V6Hier.PreparedNodeModel)((string n, P7F1V6Hier.Kind k, string p, P7F1V6Protocol.PreparedRequest r) =>
        {
            var sent = t.Send(r, Key);
            calls.Add((r.Sha256, sent.BoundarySha256 ?? "", sent.Outcome));
            return sent.Body ?? "";
        }));
        // The chat reply is not a node reply, so the run stops at the interpretation; the boundary is what this test checks.
        Assert.Equal("STOPPED_INTERPRETATION_INVALID", run.RunStatus);
        Assert.Single(calls);
        Assert.Equal(calls[0].Prepared, calls[0].Boundary);
        Assert.Equal("HTTP_OK", calls[0].Outcome);
        Assert.Equal(1, h.Calls);
    }
}
