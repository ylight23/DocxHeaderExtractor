using System.Net;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.V5Qualification.P7;

namespace DocxHeaderExtractor.Tests;

/// <summary>Issue #9 D4.2 (offline, mock HTTP only): the canary send entrypoint. A fake handler stands in for the network, so no connection is
/// opened. The tests check preflight, the exact bytes sent, one attempt with no retry, redirect or fallback, the stored response, usage
/// recording with UNKNOWN for missing reasoning, and that the API key is never written to disk.</summary>
public sealed class P7F1V6DirectSendD42Tests : IDisposable
{
    const string FakeKey = "sk-or-TEST-not-a-real-key-0123456789";
    const string PinnedDir = "artifacts/v5-p7-f1-v6-d4";
    readonly string scratch = Path.Combine(Path.GetTempPath(), "dhx-d42-" + Guid.NewGuid().ToString("N"));

    public P7F1V6DirectSendD42Tests() => Directory.CreateDirectory(scratch);

    public void Dispose()
    {
        try { Directory.Delete(scratch, recursive: true); } catch (IOException) { }
    }

    sealed class FakeHandler : HttpMessageHandler
    {
        public List<(string Method, string Url, byte[] Body, string? Auth)> Calls { get; } = new();
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => Ok(DefaultResponse());
        public Exception? Throw { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(ct);
            Calls.Add((request.Method.Method, request.RequestUri!.ToString(), body, request.Headers.Authorization?.ToString()));
            if (Throw is not null) throw Throw;
            return Respond(request);
        }
    }

    static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    static string DefaultResponse(bool withReasoning = true, string? providerEcho = null) =>
        JsonSerializer.Serialize(new
        {
            id = "gen-test",
            model = "qwen/qwen3.7-flash",
            provider = providerEcho ?? "Alibaba",
            choices = new[] { new { index = 0, finish_reason = "stop", message = new { role = "assistant", content = "{\"protocolVersion\":\"P7_F1_V6_DIRECT_RESPONSE_V1\",\"decisions\":[]}" } } },
            usage = withReasoning
                ? (object)new { prompt_tokens = 420, completion_tokens = 40, cost = 0.0000231, completion_tokens_details = new { reasoning_tokens = 5 } }
                : new { prompt_tokens = 420, completion_tokens = 40, cost = 0.0000231 },
        });

    /// <summary>A scratch copy of the pinned artifact directory: the body file is the one the commit pinned.</summary>
    string CopyPinned(string name)
    {
        var dir = Path.Combine(scratch, name);
        Directory.CreateDirectory(dir);
        File.Copy(Path.Combine(TestRepository.Root(), PinnedDir, P7F1V6DirectCanarySend.BodyFile), Path.Combine(dir, P7F1V6DirectCanarySend.BodyFile));
        return dir;
    }

    static Dictionary<string, JsonElement> ReadResult(string dir)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, P7F1V6DirectCanarySend.ResultFile)));
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    [Fact]
    public void Preflight_PassesOnThePinnedBytes()
    {
        var dir = CopyPinned("pre");
        var pre = P7F1V6DirectCanarySend.Check(dir, P7F1V6DirectCanarySend.PinnedSha256, FakeKey, 1);
        Assert.True(pre.Allowed, string.Join(",", pre.Failures));
        Assert.Equal(P7F1V6DirectCanarySend.PinnedSha256, pre.Request!.Sha256);
        Assert.Equal(P7F1V6DirectCanarySend.PinnedBytes, pre.Request.Bytes);
        Assert.True(pre.ReserveUsd < P7F1V6DirectCanary.CeilingUsd);
    }

    [Fact]
    public void Send_SendsExactlyThePinnedBytes_OnceWithOneBearerHeader()
    {
        var dir = CopyPinned("happy");
        var h = new FakeHandler();
        var record = P7F1V6DirectCanarySend.Run(dir, P7F1V6DirectCanarySend.PinnedSha256, FakeKey, 1, h);

        Assert.Equal("HTTP_OK", record.Outcome);
        Assert.Single(h.Calls);
        Assert.Equal("POST", h.Calls[0].Method);
        Assert.Equal(P7F1V6OpenRouterTransport.Url, h.Calls[0].Url);
        Assert.Equal(File.ReadAllBytes(Path.Combine(dir, P7F1V6DirectCanarySend.BodyFile)), h.Calls[0].Body);
        Assert.Equal("Bearer " + FakeKey, h.Calls[0].Auth);
        Assert.True(ReadResult(dir)["boundaryMatchesPinned"].GetBoolean());
    }

    [Fact]
    public void Send_RecordsUsageFinishAndEndpoint_AndStoresTheResponseAsReceived()
    {
        var dir = CopyPinned("usage");
        var h = new FakeHandler();
        var raw = DefaultResponse();
        h.Respond = _ => Ok(raw);
        P7F1V6DirectCanarySend.Run(dir, P7F1V6DirectCanarySend.PinnedSha256, FakeKey, 1, h);

        var result = ReadResult(dir);
        Assert.Equal("stop", result["finishReason"].GetString());
        Assert.Equal("Alibaba", result["providerReportedEndpoint"].GetString());
        var usage = result["usage"];
        Assert.Equal(420, usage.GetProperty("promptTokens").GetInt32());
        Assert.Equal(40, usage.GetProperty("completionTokens").GetInt32());
        Assert.Equal(5, usage.GetProperty("reasoningTokens").GetInt32());
        Assert.Equal(0.0000231, usage.GetProperty("providerCostUsd").GetDouble(), 12);
        Assert.Equal(raw, File.ReadAllText(Path.Combine(dir, P7F1V6DirectCanarySend.ResponseFile)));
    }

    [Fact]
    public void Send_MissingReasoningUsage_IsRecordedAsUnknown_NotAsOff()
    {
        var dir = CopyPinned("noreason");
        var h = new FakeHandler();
        h.Respond = _ => Ok(DefaultResponse(withReasoning: false));
        P7F1V6DirectCanarySend.Run(dir, P7F1V6DirectCanarySend.PinnedSha256, FakeKey, 1, h);
        var usage = ReadResult(dir)["usage"];
        Assert.Equal("UNKNOWN", usage.GetProperty("reasoningTokens").GetString());
    }

    [Fact]
    public void Send_MissingEndpointInResponse_IsRecordedAsUnknown()
    {
        var dir = CopyPinned("noendpoint");
        var h = new FakeHandler();
        h.Respond = _ => Ok("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"{}\"}}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1}}");
        P7F1V6DirectCanarySend.Run(dir, P7F1V6DirectCanarySend.PinnedSha256, FakeKey, 1, h);
        Assert.Equal("UNKNOWN", ReadResult(dir)["providerReportedEndpoint"].GetString());
    }

    [Theory]
    [InlineData("confirm")]
    [InlineData("posts")]
    [InlineData("key")]
    [InlineData("body")]
    [InlineData("length")]
    public void Preflight_Failure_MakesNoPost(string which)
    {
        var dir = CopyPinned("fail-" + which);
        string confirm = P7F1V6DirectCanarySend.PinnedSha256;
        int posts = 1;
        string? key = FakeKey;
        switch (which)
        {
            case "confirm": confirm = new string('0', 64); break;
            case "posts": posts = 2; break;
            case "key": key = null; break;
            case "body":
                var path = Path.Combine(dir, P7F1V6DirectCanarySend.BodyFile);
                var bytes = File.ReadAllBytes(path);
                bytes[^3] ^= 0x01;
                File.WriteAllBytes(path, bytes);
                break;
            case "length":
                File.WriteAllBytes(Path.Combine(dir, P7F1V6DirectCanarySend.BodyFile), Encoding.UTF8.GetBytes("{}"));
                break;
        }
        var h = new FakeHandler();
        var record = P7F1V6DirectCanarySend.Run(dir, confirm, key, posts, h);

        Assert.Equal("PREFLIGHT_FAILED", record.Outcome);
        Assert.False(record.Sent);
        Assert.Empty(h.Calls);
        Assert.False(File.Exists(Path.Combine(dir, P7F1V6DirectCanarySend.ResponseFile)));
        Assert.Equal("PREFLIGHT_FAILED", ReadResult(dir)["status"].GetString());
    }

    [Fact]
    public void Preflight_RejectsAnUnsafeRoute_WithoutSending()
    {
        var dir = CopyPinned("route");
        var path = Path.Combine(dir, P7F1V6DirectCanarySend.BodyFile);
        var text = File.ReadAllText(path);
        var edited = text.Replace("\"allow_fallbacks\":false", "\"allow_fallbacks\":true");
        Assert.NotEqual(text, edited);
        File.WriteAllText(path, edited);
        // The edited body no longer matches the pin, so the SHA check refuses it before the route is even read.
        var pre = P7F1V6DirectCanarySend.Check(dir, P7F1V6DirectCanarySend.PinnedSha256, FakeKey, 1);
        Assert.False(pre.Allowed);
        Assert.Contains("BODY_SHA_MISMATCH", pre.Failures);
    }

    [Fact]
    public void Redirect_IsNotFollowed_AndIsNotRetried()
    {
        var dir = CopyPinned("redirect");
        var h = new FakeHandler();
        h.Respond = _ => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://example.invalid/elsewhere") } };
        var record = P7F1V6DirectCanarySend.Run(dir, P7F1V6DirectCanarySend.PinnedSha256, FakeKey, 1, h);
        Assert.Equal("REDIRECT_REFUSED", record.Outcome);
        Assert.Single(h.Calls);
    }

    [Fact]
    public void RateLimit_IsNotRetried()
    {
        var dir = CopyPinned("429");
        var h = new FakeHandler();
        h.Respond = _ => new HttpResponseMessage((HttpStatusCode)429) { Content = new StringContent("{}") };
        var record = P7F1V6DirectCanarySend.Run(dir, P7F1V6DirectCanarySend.PinnedSha256, FakeKey, 1, h);
        Assert.Equal("RATE_LIMITED_NO_RETRY", record.Outcome);
        Assert.Single(h.Calls);
    }

    [Fact]
    public void ServerError_IsNotRetried()
    {
        var dir = CopyPinned("500");
        var h = new FakeHandler();
        h.Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("{}") };
        var record = P7F1V6DirectCanarySend.Run(dir, P7F1V6DirectCanarySend.PinnedSha256, FakeKey, 1, h);
        Assert.Equal("SERVER_ERROR_NO_RETRY", record.Outcome);
        Assert.Single(h.Calls);
    }

    [Fact]
    public void Timeout_IsOneAttempt_NoFallbackRoute()
    {
        var dir = CopyPinned("timeout");
        var h = new FakeHandler { Throw = new TaskCanceledException("timeout") };
        var record = P7F1V6DirectCanarySend.Run(dir, P7F1V6DirectCanarySend.PinnedSha256, FakeKey, 1, h);
        Assert.Equal("TIMEOUT_OR_CANCELLED", record.Outcome);
        Assert.Single(h.Calls);
    }

    [Fact]
    public void TransportError_IsOneAttempt()
    {
        var dir = CopyPinned("transport");
        var h = new FakeHandler { Throw = new HttpRequestException("refused") };
        var record = P7F1V6DirectCanarySend.Run(dir, P7F1V6DirectCanarySend.PinnedSha256, FakeKey, 1, h);
        Assert.Equal("TRANSPORT_ERROR", record.Outcome);
        Assert.Single(h.Calls);
    }

    [Fact]
    public void ApiKey_IsNeverWrittenToAnyFile()
    {
        var dir = CopyPinned("nokey");
        var h = new FakeHandler();
        P7F1V6DirectCanarySend.Run(dir, P7F1V6DirectCanarySend.PinnedSha256, FakeKey, 1, h);
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            Assert.DoesNotContain(FakeKey, File.ReadAllText(f), StringComparison.Ordinal);
    }

    [Fact]
    public void ApiKey_EchoedByTheServer_IsNotStored()
    {
        var dir = CopyPinned("echo");
        var h = new FakeHandler();
        h.Respond = _ => Ok("{\"error\":\"bad key " + FakeKey + "\"}");
        P7F1V6DirectCanarySend.Run(dir, P7F1V6DirectCanarySend.PinnedSha256, FakeKey, 1, h);
        Assert.False(File.Exists(Path.Combine(dir, P7F1V6DirectCanarySend.ResponseFile)));
        Assert.True(ReadResult(dir)["apiKeyEchoedInResponse"].GetBoolean());
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            Assert.DoesNotContain(FakeKey, File.ReadAllText(f), StringComparison.Ordinal);
    }

    [Fact]
    public void RecordedCanaryResponse_FailsTheV1ParserOnTheDecisionKey()
    {
        // The one real canary T1 response (2026-10-12). Qwen named the decision key "line"; the V1 system prompt never named that key.
        // This records the parser's verdict on real output; it is not a judgement of the model's selections.
        var path = Path.Combine(TestRepository.Root(), PinnedDir, P7F1V6DirectCanarySend.ResponseFile);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()!;
        var lines = new[] { "S0001", "S0002", "S0003", "S0004" }.Select(a => new P7F1V6Direct.Line(a, 1, "x")).ToList();
        var r = P7F1V6Direct.Parse(content, lines, 0, 4);
        Assert.False(r.Ok);
        Assert.Equal("DECISION_KEYS_INVALID", r.Failure);
        Assert.DoesNotContain("\"alias\"", P7F1V6Direct.SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Pinned_BodyRequestsNoReasoningAndTheApprovedRoute()
    {
        var body = File.ReadAllText(Path.Combine(TestRepository.Root(), PinnedDir, P7F1V6DirectCanarySend.BodyFile));
        using var doc = JsonDocument.Parse(body);
        Assert.False(doc.RootElement.TryGetProperty("reasoning", out _));
        Assert.Equal(P7F1V6Protocol.MaxTokens, doc.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.False(doc.RootElement.GetProperty("provider").GetProperty("allow_fallbacks").GetBoolean());
    }
}
