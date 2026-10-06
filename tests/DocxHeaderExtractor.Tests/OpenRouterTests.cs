using System.Net;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Inference;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// OpenRouter streaming transport V1: the request streams, the reply is reassembled from SSE and
/// accepted only when the stream really completed, and transport failures are retried within a bound.
/// </summary>
public sealed class OpenRouterTests
{
    [Fact]
    public async Task Request_streams_and_enforces_privacy_and_json_output()
    {
        var handler = new CaptureHandler(Reply.Sse(" {\"ok\":true} "));
        using var model = Model(handler, new RemoteInferenceOptions { ApiKey = "test-key" });

        var result = await model.BoundaryCutAsync("Return JSON.", "{\"sourceParts\":[]}");

        Assert.Equal("{\"ok\":true}", result);
        using var body = JsonDocument.Parse(handler.Body);
        var root = body.RootElement;
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.True(root.GetProperty("usage").GetProperty("include").GetBoolean());
        Assert.Equal("none", root.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal("json_object", root.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Equal("qwen/qwen3.7-flash", root.GetProperty("model").GetString());
        // The ZDR flag must always be written, never omitted: an omitted field inherits the
        // account's privacy default, which silently rejects every endpoint of the controlled models.
        var provider = root.GetProperty("provider");
        Assert.False(provider.GetProperty("zdr").GetBoolean());
        Assert.Equal("deny", provider.GetProperty("data_collection").GetString());
        Assert.True(provider.GetProperty("require_parameters").GetBoolean());
        // No route configured: OpenRouter's automatic routing is kept.
        Assert.True(provider.GetProperty("allow_fallbacks").GetBoolean());
        Assert.False(provider.TryGetProperty("order", out _));
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("test-key", handler.AuthorizationParameter);
    }

    [Fact]
    public async Task A_configured_route_is_pinned_with_fallbacks_disabled()
    {
        var handler = new CaptureHandler(Reply.Sse("{}"));
        using var model = Model(handler, new RemoteInferenceOptions
        {
            ApiKey = "test-key",
            Model = "qwen/qwen3.7-flash",
            OpenRouterProviderRoute = "Alibaba",
        });

        await model.BoundaryCutAsync("Return JSON.", "{\"sourceParts\":[]}");

        using var body = JsonDocument.Parse(handler.Body);
        var provider = body.RootElement.GetProperty("provider");
        Assert.Equal(["Alibaba"], provider.GetProperty("order").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.False(provider.GetProperty("allow_fallbacks").GetBoolean());
        Assert.Equal("deny", provider.GetProperty("data_collection").GetString());
    }

    [Fact]
    public void The_qualified_default_model_is_pinned_to_its_route_and_other_models_are_not()
    {
        Assert.Null(Environment.GetEnvironmentVariable("OPENROUTER_PROVIDER_ROUTE"));
        var options = new RemoteInferenceOptions { ApiKey = "k" };

        options.UseOpenRouterModel(RemoteInferenceOptions.DefaultModel);
        Assert.Equal("qwen/qwen3.7-flash", options.Model);
        Assert.Equal("Alibaba", options.OpenRouterProviderRoute);

        // A route that does not serve a custom model would fail every request, so none is implied.
        options.UseOpenRouterModel("another/model");
        Assert.Equal("another/model", options.Model);
        Assert.Null(options.OpenRouterProviderRoute);
    }

    [Fact]
    public async Task Content_is_reassembled_from_deltas_split_across_network_chunks()
    {
        var sse = Reply.Sse("{\"headings\":[{\"a\":1}", ",{\"b\":2}]}");
        // Deliver the stream in tiny slices so events and even UTF-8 characters straddle reads.
        var handler = new CaptureHandler(Reply.Chunked(sse, 7));
        using var model = Model(handler, new RemoteInferenceOptions { ApiKey = "test-key" });

        var result = await model.BoundaryCutAsync("Return JSON.", "{\"sourceParts\":[]}");

        Assert.Equal("{\"headings\":[{\"a\":1},{\"b\":2}]}", result);
    }

    [Fact]
    public async Task Finish_reason_length_is_returned_to_the_contract_not_retried_as_transport()
    {
        var handler = new CaptureHandler(Reply.Sse("{\"headings\":[", finishReason: "length"));
        using var model = Model(handler, new RemoteInferenceOptions { ApiKey = "test-key" });

        var result = await model.BoundaryCutAsync("Return JSON.", "{\"sourceParts\":[]}");

        Assert.Equal("{\"headings\":[", result);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task A_stream_without_done_is_incomplete_and_retried_within_the_bound()
    {
        var incomplete = Reply.Sse("{}", done: false);
        var handler = new CaptureHandler(incomplete, incomplete, Reply.Sse("{\"ok\":1}"));
        var waits = new List<TimeSpan>();
        using var model = Model(handler, new RemoteInferenceOptions { ApiKey = "test-key", TransientRequestRetries = 2 }, waits);

        var result = await model.BoundaryCutAsync("Return JSON.", "{\"sourceParts\":[]}");

        Assert.Equal("{\"ok\":1}", result);
        Assert.Equal(3, handler.RequestCount);
        Assert.Equal(2, waits.Count);
    }

    [Fact]
    public async Task Transport_failures_stop_after_the_configured_retries()
    {
        var incomplete = Reply.Sse("{}", done: false);
        var handler = new CaptureHandler(incomplete);
        var waits = new List<TimeSpan>();
        using var model = Model(handler, new RemoteInferenceOptions { ApiKey = "test-key", TransientRequestRetries = 2 }, waits);

        await Assert.ThrowsAsync<HttpRequestException>(() => model.BoundaryCutAsync("Return JSON.", "{\"sourceParts\":[]}"));

        Assert.Equal(3, handler.RequestCount);
    }

    [Fact]
    public async Task Http_429_is_retried_honouring_retry_after()
    {
        var handler = new CaptureHandler(
            Reply.Status(HttpStatusCode.TooManyRequests, retryAfterSeconds: 3),
            Reply.Sse("{\"ok\":2}"));
        var waits = new List<TimeSpan>();
        using var model = Model(handler, new RemoteInferenceOptions { ApiKey = "test-key" }, waits);

        var result = await model.BoundaryCutAsync("Return JSON.", "{\"sourceParts\":[]}");

        Assert.Equal("{\"ok\":2}", result);
        Assert.Equal([TimeSpan.FromSeconds(3)], waits);
    }

    [Fact]
    public async Task A_client_error_is_not_retried()
    {
        var handler = new CaptureHandler(Reply.Status(HttpStatusCode.BadRequest));
        var waits = new List<TimeSpan>();
        using var model = Model(handler, new RemoteInferenceOptions { ApiKey = "test-key" }, waits);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => model.BoundaryCutAsync("Return JSON.", "{\"sourceParts\":[]}"));

        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.Equal(1, handler.RequestCount);
        Assert.Empty(waits);
    }

    [Fact]
    public async Task A_provider_error_event_mid_stream_is_a_transport_failure()
    {
        var handler = new CaptureHandler(Reply.ProviderErrorEvent(), Reply.Sse("{\"ok\":3}"));
        var waits = new List<TimeSpan>();
        using var model = Model(handler, new RemoteInferenceOptions { ApiKey = "test-key" }, waits);

        var result = await model.BoundaryCutAsync("Return JSON.", "{\"sourceParts\":[]}");

        Assert.Equal("{\"ok\":3}", result);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task Boundary_cut_keeps_configured_output_budget_cap_for_32_role_ids()
    {
        var handler = new CaptureHandler(Reply.Sse("{}"));
        using var model = Model(handler, new RemoteInferenceOptions
        {
            ApiKey = "test-key",
            MaxOutputTokens = 768,
        });

        var blocks = Enumerable.Range(0, 32).Select(index =>
            $"{{\"id\":\"DOC-0116:source-paragraph-{index:D3}-long-stable-id\",\"source_text\":\"A realistic source paragraph payload for output-budget testing.\",\"source_length\":64}}");
        var user = $"{{\"blocks\":[{string.Join(',', blocks)}]}}";
        await model.BoundaryCutAsync("role system. Return JSON.", user);

        using var request = JsonDocument.Parse(handler.Body);
        using var materialized = JsonDocument.Parse(request.RootElement.GetProperty("messages")[1]
            .GetProperty("content").GetString()!);
        Assert.Equal(32, materialized.RootElement.GetProperty("blocks").GetArrayLength());
        Assert.Equal(768, request.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task Reasoning_effort_is_explicitly_selectable_for_an_authorized_experiment()
    {
        var handler = new CaptureHandler(Reply.Sse("{}"));
        using var model = Model(handler, new RemoteInferenceOptions
        {
            ApiKey = "test-key",
            OpenRouterReasoningEffort = "medium",
        });

        await model.BoundaryCutAsync("Return JSON.", "{\"sourceParts\":[]}");

        Assert.Contains("\"reasoning\":{\"effort\":\"medium\"}", handler.Body);
    }

    [Fact]
    public void Missing_api_key_fails_before_any_request()
    {
        using var http = new HttpClient(new CaptureHandler(Reply.Sse("{}")));
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new OpenRouterHeaderExtractor(http, new RemoteInferenceOptions()));

        Assert.Contains("OPENROUTER_API_KEY", ex.Message);
    }

    [Fact]
    public async Task Explicit_debug_log_exposes_provider_exchange_without_authorization_header()
    {
        var handler = new CaptureHandler(Reply.Sse("{\"items\":[]}"));
        var logs = new List<string>();
        using var model = Model(handler, new RemoteInferenceOptions
        {
            ApiKey = "test-key",
            DebugLog = logs.Add,
        });

        await model.BoundaryCutAsync("Return JSON.", "{\"sourceParts\":[]}");

        Assert.Contains(logs, log => log.Contains("LLM REQUEST") && log.Contains("qwen/qwen3.7-flash"));
        Assert.Contains(logs, log => log.Contains("LLM RESPONSE") && log.Contains("finish_reason"));
        Assert.DoesNotContain(logs, log => log.Contains("test-key"));
        Assert.DoesNotContain(logs, log => log.Contains("Authorization", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExecuteAsync_sends_the_caller_frozen_bytes_exactly_and_returns_finish_reason()
    {
        var handler = new CaptureHandler(Reply.Sse("{\"claims\":[]}", finishReason: "stop"));
        using var model = QualificationModel(handler, new RemoteInferenceOptions { ApiKey = "test-key" });
        var frozenBody = Encoding.UTF8.GetBytes("""{"model":"frozen/exact-bytes","max_tokens":123}""");

        var (content, finishReason) = await model.ExecuteAsync(frozenBody, maxTokens: 123, "Return JSON.", "user");

        Assert.Equal("{\"claims\":[]}", content);
        Assert.Equal("stop", finishReason);
        Assert.Equal(Encoding.UTF8.GetString(frozenBody), handler.Body);
    }

    [Fact]
    public async Task ExecuteAsync_reports_a_length_finish_reason_rather_than_hiding_it()
    {
        var handler = new CaptureHandler(Reply.Sse("{\"claims\":[", finishReason: "length"));
        using var model = QualificationModel(handler, new RemoteInferenceOptions { ApiKey = "test-key" });

        var (_, finishReason) = await model.ExecuteAsync(
            Encoding.UTF8.GetBytes("{}"), maxTokens: 10, "Return JSON.", "user");

        Assert.Equal("length", finishReason);
    }

    [Fact]
    public async Task ExecuteAsync_reuses_the_same_bounded_retry_as_boundary_cut()
    {
        var incomplete = Reply.Sse("{}", done: false);
        var handler = new CaptureHandler(incomplete, Reply.Sse("{\"claims\":[]}"));
        var waits = new List<TimeSpan>();
        using var model = QualificationModel(handler, new RemoteInferenceOptions { ApiKey = "test-key" }, waits);

        var (content, _) = await model.ExecuteAsync(Encoding.UTF8.GetBytes("{}"), 10, "Return JSON.", "user");

        Assert.Equal("{\"claims\":[]}", content);
        Assert.Single(waits);
    }

    [Fact]
    public async Task ExecuteAsync_telemetry_records_retry_after_done_and_clean_eof_per_attempt()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dhx-telemetry-{Guid.NewGuid():N}");
        try
        {
            var handler = new CaptureHandler(
                Reply.Status(HttpStatusCode.TooManyRequests, retryAfterSeconds: 3),
                Reply.Sse("{\"claims\":[]}"));
            var options = new RemoteInferenceOptions
            {
                ApiKey = "test-key",
                Observability = new ProviderObservabilityOptions { RootDirectory = root, CampaignId = "t", DocumentId = "d" },
            };
            using var model = QualificationModel(handler, options);

            await model.ExecuteAsync(Encoding.UTF8.GetBytes("{}"), 10, "Return JSON.", "user");

            var events = File.ReadAllLines(Path.Combine(root, "telemetry", "events.jsonl"))
                .Select(line => JsonDocument.Parse(line).RootElement).ToArray();
            var failed = events.Single(e => e.GetProperty("eventType").GetString() == "ATTEMPT_FAILED");
            Assert.Equal("HTTP_ERROR", failed.GetProperty("reason").GetString());
            Assert.Equal(429, failed.GetProperty("data").GetProperty("status").GetInt32());
            Assert.True(failed.GetProperty("data").GetProperty("retryable").GetBoolean());
            Assert.Equal(3, failed.GetProperty("data").GetProperty("retryAfterSeconds").GetDouble());
            var complete = events.Single(e => e.GetProperty("eventType").GetString() == "TRANSPORT_COMPLETE");
            Assert.True(complete.GetProperty("doneObserved").GetBoolean());
            Assert.True(complete.GetProperty("cleanEof").GetBoolean());
            Assert.Equal("stop", complete.GetProperty("finishReason").GetString());
            // The bearer key is a request header only; it never reaches persisted telemetry.
            Assert.DoesNotContain(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories),
                path => File.ReadAllText(path).Contains("test-key", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static OpenRouterHeaderExtractor Model(
        CaptureHandler handler, RemoteInferenceOptions options, List<TimeSpan>? waits = null)
    {
        var model = new OpenRouterHeaderExtractor(new HttpClient(handler), options);
        model.RetryWait = (delay, _) =>
        {
            waits?.Add(delay);
            return Task.CompletedTask;
        };
        return model;
    }

    // ExecuteAsync is qualification-only; the retry/telemetry behaviour it shares with production is the engine's.
    private static V5Qualification.OpenRouterQualificationTransport QualificationModel(
        CaptureHandler handler, RemoteInferenceOptions options, List<TimeSpan>? waits = null)
    {
        var model = new V5Qualification.OpenRouterQualificationTransport(new HttpClient(handler), options);
        model.RetryWait = (delay, _) =>
        {
            waits?.Add(delay);
            return Task.CompletedTask;
        };
        return model;
    }

    internal static class Reply
    {
        public static Func<HttpResponseMessage> Sse(string content, string finishReason = "stop", bool done = true) =>
            Sse([content], finishReason, done);

        public static Func<HttpResponseMessage> Sse(string first, string second) => Sse([first, second], "stop", true);

        private static Func<HttpResponseMessage> Sse(string[] deltas, string finishReason, bool done)
        {
            var text = new StringBuilder();
            text.Append(": OPENROUTER PROCESSING\n\n");
            foreach (var delta in deltas)
                text.Append("data: ").Append(JsonSerializer.Serialize(new
                {
                    choices = new[] { new { delta = new { content = delta }, finish_reason = (string?)null } },
                })).Append("\n\n");
            text.Append("data: ").Append(JsonSerializer.Serialize(new
            {
                choices = new[] { new { delta = new { content = "" }, finish_reason = finishReason } },
                usage = new { prompt_tokens = 10, completion_tokens = 5 },
            })).Append("\n\n");
            if (done) text.Append("data: [DONE]\n\n");
            var bytes = Encoding.UTF8.GetBytes(text.ToString());
            return () => Stream(bytes, int.MaxValue);
        }

        public static Func<HttpResponseMessage> Chunked(Func<HttpResponseMessage> sse, int chunkSize) => () =>
        {
            var bytes = sse().Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            return Stream(bytes, chunkSize);
        };

        public static Func<HttpResponseMessage> ProviderErrorEvent() => () =>
            Stream(Encoding.UTF8.GetBytes("data: {\"error\":{\"code\":502,\"message\":\"upstream reset\"}}\n\n"), int.MaxValue);

        public static Func<HttpResponseMessage> Status(HttpStatusCode status, int? retryAfterSeconds = null) => () =>
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent("{\"error\":{\"message\":\"test\"}}", Encoding.UTF8, "application/json"),
            };
            if (retryAfterSeconds is { } seconds)
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
            return response;
        };

        private static HttpResponseMessage Stream(byte[] bytes, int chunkSize) => new(HttpStatusCode.OK)
        {
            Content = new StreamContent(new SlicedStream(bytes, chunkSize))
            {
                Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream") },
            },
        };
    }

    /// <summary>Returns at most <c>chunkSize</c> bytes per read, like a network stream.</summary>
    private sealed class SlicedStream(byte[] bytes, int chunkSize) : MemoryStream(bytes)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, chunkSize));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, chunkSize)], cancellationToken);
    }

    private sealed class CaptureHandler(params Func<HttpResponseMessage>[] responses) : HttpMessageHandler
    {
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
            return responses[Math.Min(RequestCount - 1, responses.Length - 1)]();
        }
    }
}
