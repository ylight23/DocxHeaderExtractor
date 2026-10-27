using System.Net.Http.Headers;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using static DocxHeaderExtractor.Agents.OpenRouterWire;
using DocxHeaderExtractor.Agents.Budget;

namespace DocxHeaderExtractor.Agents.Transport;

/// <summary>
/// The audited OpenRouter model of the agent (ported from the qualified V6 transport, SSE reader, SSE guard, usage audit and audited model): one
/// POST of the prepared bytes, no redirect, one timeout, the key only in the Authorization header. The streamed reply must end with [DONE] and
/// carry no error or unknown event; the response must come from Alibaba and qwen/qwen3.7-flash and pass the usage and finish audit; the provider
/// cost is recorded against the call's reserve. Any failure is a <see cref="ReplyFailure"/>; the raw bytes are not kept.
/// </summary>
internal sealed class AgentModel : IDisposable
{
    public const string Url = "https://openrouter.ai/api/v1/chat/completions";

    readonly HttpClient client;
    readonly string apiKey;
    readonly AgentBudget budget;
    readonly CancellationToken cancellationToken;
    readonly object usageGate = new();
    readonly int rateLimitRetries;
    readonly int rateLimitBaseDelayMs;
    readonly TimeSpan retryWaitBudget;
    readonly ProviderRequestGate? requestGate;

    /// <summary>One shared retry limit for transient transport failures, including 429. No caller may restart an exhausted turn.</summary>
    public const int ProductionRateLimitRetries = 3;
    public static readonly TimeSpan ProductionRetryWaitBudget = TimeSpan.FromSeconds(30);

    /// <param name="rateLimitRetries">Shared retry limit for 429 and allowlisted transient failures. Zero preserves the single-attempt
    /// transport used by legacy replay/tests. Production passes <see cref="ProductionRateLimitRetries"/>.</param>
    public AgentModel(HttpMessageHandler handler, string apiKey, AgentBudget budget, TimeSpan? timeout = null, CancellationToken cancellationToken = default,
        int rateLimitRetries = 0, int rateLimitBaseDelayMs = 4000, TimeSpan? retryWaitBudget = null, ProviderRequestGate? requestGate = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("API_KEY_MISSING", nameof(apiKey));
        if (rateLimitRetries < 0 || rateLimitBaseDelayMs < 0 || retryWaitBudget <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(rateLimitRetries));
        client = new HttpClient(handler, disposeHandler: false) { Timeout = timeout ?? TimeSpan.FromSeconds(300) };
        this.apiKey = apiKey; this.budget = budget;
        this.cancellationToken = cancellationToken;
        this.rateLimitRetries = rateLimitRetries; this.rateLimitBaseDelayMs = rateLimitBaseDelayMs;
        this.retryWaitBudget = retryWaitBudget ?? ProductionRetryWaitBudget;
        this.requestGate = requestGate ?? (rateLimitRetries > 0 ? ProviderRequestGate.ForAccount(apiKey) : null);
    }

    public int PromptTokens { get; private set; }
    public int CompletionTokens { get; private set; }
    public int ReasoningTokens { get; private set; }
    public int CachedTokens { get; private set; }
    public int CacheCreationTokens { get; private set; }

    readonly List<long> postDurationsMs = new();
    /// <summary>Wall time of each successful provider post in milliseconds, retries included. Read after the run for P50/P95.</summary>
    public IReadOnlyList<long> PostDurationsMs { get { lock (usageGate) return postDurationsMs.ToArray(); } }

    public static HttpClientHandler CreateHandler() => new() { AllowAutoRedirect = false, UseCookies = false };

    public ModelTurn Turn(string unitId, int turn, PreparedRequest request)
    {
        var started = Environment.TickCount64;
        var text = ReplyRaw(unitId, turn, request);
        lock (usageGate) postDurationsMs.Add(Environment.TickCount64 - started);
        var (reply, failure) = ReadResponse(text);
        if (reply is null) throw new ReplyFailure("CONTENT:" + failure);
        return reply;
    }

    static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>One audited call; returns the chat-completion view assembled from the stream.</summary>
    string ReplyRaw(string unitId, int turn, PreparedRequest request)
    {
        var waited = TimeSpan.Zero;
        for (var k = 0; ; k++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (budget.Claim(request, out var admitted, fresh: k > 0) is { } refusal) throw new ReplyFailure("BUDGET:" + refusal);
            var attempt = admitted!;
            var clock = Stopwatch.StartNew();
            var queueWait = TimeSpan.Zero;
            var requestSeconds = 0d;
            var retryDelay = TimeSpan.Zero;
            SendResult? sent = null;
            TimeSpan? retryHint = null;
            var outcome = "NOT_SENT";
            var dispatched = false;
            try
            {
                using (var lease = requestGate?.Enter(retryWaitBudget - waited, cancellationToken))
                {
                    queueWait = clock.Elapsed;
                    waited += queueWait;
                    cancellationToken.ThrowIfCancellationRequested();
                    dispatched = true;
                    outcome = "REQUEST_STARTED";
                    var sendClock = Stopwatch.StartNew();
                    try
                    {
                        sent = Send(request);
                        if (sent.Status == 429 || sent.ErrorCode == 429)
                        {
                            // Publish before releasing the permit, including when no retry remains for this turn.
                            retryHint = RetryDelay(k, sent.RetryAfter);
                            requestGate?.CoolDown(retryHint.Value);
                        }
                    }
                    finally { requestSeconds = sendClock.Elapsed.TotalSeconds; }
                }
                outcome = sent.Outcome;
                if (outcome != "HTTP_OK")
                {
                    var notSent = outcome is "BOUNDARY_SHA_MISMATCH" or "PREPARED_SHA_MISMATCH";
                    if (notSent) budget.Halt(outcome);
                    // Only an HTTP 429 is confirmed to have rejected generation. Broken SSE/5xx stay provisional.
                    budget.Settle(attempt.Id, notSent || sent.Status == 429 ? 0 : null);
                    throw new ReplyFailure("TRANSPORT:" + outcome);
                }
                return AuditReply(sent.Body ?? "", attempt);
            }
            catch (ProviderRequestGate.ReplyWaitLimit)
            {
                queueWait = clock.Elapsed;
                outcome = "PROVIDER_WAIT_LIMIT";
                budget.Settle(attempt.Id, 0);
                throw new ReplyFailure("TRANSPORT:" + outcome, transportRetriesHandled: true);
            }
            catch (ReplyFailure failure)
            {
                outcome = failure.Code.StartsWith("TRANSPORT:", StringComparison.Ordinal) ? failure.Code[10..] : failure.Code;
                var transient = outcome is "RATE_LIMITED_NO_RETRY" or "SERVER_ERROR_NO_RETRY" or "TRANSPORT_ERROR" or "STREAM_ERROR" or "STREAM_INCOMPLETE";
                retryDelay = retryHint ?? RetryDelay(k, sent?.RetryAfter);
                if (!transient || k >= rateLimitRetries || retryDelay >= retryWaitBudget - waited)
                {
                    retryDelay = TimeSpan.Zero;
                    throw new ReplyFailure(failure.Code, transportRetriesHandled:
                        (rateLimitRetries > 0 && outcome != "TIMEOUT_OR_CANCELLED" && failure.Code.StartsWith("TRANSPORT:", StringComparison.Ordinal)) || failure.TransportRetriesHandled);
                }
            }
            catch (OperationCanceledException)
            {
                outcome = "CANCELLED";
                if (!dispatched) { queueWait = clock.Elapsed; budget.Settle(attempt.Id, 0); }
                // HttpClient uses a linked token; Parallel.For recognizes cancellation only with its caller token.
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
            finally
            {
                budget.Settle(attempt.Id, null);
                budget.ObserveTransport(attempt.Id, new(unitId, turn, k, sent?.Status, outcome,
                    Math.Round(requestSeconds, 3), Math.Round(queueWait.TotalSeconds, 3), sent?.RetryAfter?.TotalSeconds,
                    0, sent?.ErrorCode));
            }
            // No provider concurrency permit is held here. Cancellation interrupts the wait.
            var delayClock = Stopwatch.StartNew();
            try { ProviderRequestGate.Wait(retryDelay, cancellationToken); }
            finally
            {
                waited += delayClock.Elapsed;
                budget.ObserveTransport(attempt.Id, attempt.Transport! with { RetryDelaySeconds = Math.Round(delayClock.Elapsed.TotalSeconds, 3) });
            }
        }
    }

    TimeSpan RetryDelay(int retry, TimeSpan? retryAfter)
    {
        if (retryAfter is { } requested) return requested;
        var ceiling = Math.Min(30000d, rateLimitBaseDelayMs * Math.Pow(2, Math.Min(retry, 20)));
        return TimeSpan.FromMilliseconds(ceiling * (0.5 + Random.Shared.NextDouble() * 0.5));
    }

    string AuditReply(string raw, AgentBudget.Attempt attempt)
    {
        try
        {
            if (raw.Contains(apiKey, StringComparison.Ordinal))
            {
                budget.Halt("API_KEY_ECHOED");
                throw new ReplyFailure("API_KEY_ECHOED");
            }
            var asm = Sse.Parse(raw);
            var broken = Sse.Guard(raw) ?? (asm.Error ? "STREAM_ERROR" : !asm.Done ? "STREAM_INCOMPLETE" : asm.MalformedEvents > 0 ? "STREAM_MALFORMED" : null);
            if (broken is not null) throw new ReplyFailure("TRANSPORT:" + broken);
            var text = asm.View();
            var audit = Usage.Check(text, attempt.Resources.WireBytes, attempt.Resources.CompletionTokens);
            budget.Settle(attempt.Id, audit.ProviderCostUsd, audit);
            lock (usageGate)
            {
                PromptTokens += audit.PromptTokens ?? 0; CompletionTokens += audit.CompletionTokens ?? 0;
                ReasoningTokens += audit.ReasoningTokens ?? 0; CachedTokens += audit.CachedTokens ?? 0;
                CacheCreationTokens += audit.CacheCreationTokens ?? 0;
            }
            if (!string.Equals(asm.Provider, "Alibaba", StringComparison.Ordinal) || asm.Model != ChatModel)
            {
                budget.Halt("ENDPOINT_OR_MODEL_UNEXPECTED");
                throw new ReplyFailure("ENDPOINT_OR_MODEL_UNEXPECTED");
            }
            if (!(audit.Codes.Count == 1 && audit.Codes[0] == "OK")) throw new ReplyFailure("AUDIT:" + string.Join(",", audit.Codes));
            return text;
        }
        finally { budget.Settle(attempt.Id, null); } // An unconfirmed generation remains charged provisionally.
    }

    sealed record SendResult(string Outcome, string? Body, int? Status = null, TimeSpan? RetryAfter = null, int? ErrorCode = null);

    internal static TimeSpan? RetryAfter(RetryConditionHeaderValue? header, DateTimeOffset now) =>
        header?.Delta is { } delta ? delta : header?.Date is { } date ? (date > now ? date - now : TimeSpan.Zero) : null;

    static int? ErrorCode(string? body)
    {
        // Never retain arbitrary provider messages, metadata or string error types in safe audit.
        if (body is null) return null;
        foreach (var line in body.Split('\n'))
        {
            var value = line.StartsWith("data:", StringComparison.Ordinal) ? line[5..].Trim() : line.Trim();
            if (!value.StartsWith('{')) continue;
            try
            {
                using var doc = JsonDocument.Parse(value);
                if (doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object &&
                    error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var number)) return number;
            }
            catch (JsonException) { }
        }
        return null;
    }

    SendResult Send(PreparedRequest request)
    {
        var bytes = Encoding.UTF8.GetBytes(request.Body);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != request.Sha256) return new("PREPARED_SHA_MISMATCH", null);
        using var message = new HttpRequestMessage(HttpMethod.Post, Url) { Content = new ByteArrayContent(bytes) };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(client.Timeout);
        HttpResponseMessage response;
        try { response = client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return new("TIMEOUT_OR_CANCELLED", null); }
        catch (HttpRequestException) { return new("TRANSPORT_ERROR", null); }
        using (response)
        {
            var status = (int)response.StatusCode;
            var retryAfter = RetryAfter(response.Headers.RetryAfter, DateTimeOffset.UtcNow);
            var httpOutcome = status switch
            {
                >= 200 and < 300 => "HTTP_OK",
                >= 300 and < 400 => "REDIRECT_REFUSED",
                429 => "RATE_LIMITED_NO_RETRY",
                402 => "PAYMENT_REQUIRED",
                >= 500 => "SERVER_ERROR_NO_RETRY",
                >= 400 => "CLIENT_ERROR",
                _ => "UNEXPECTED_STATUS",
            };
            byte[] bodyBytes;
            try { bodyBytes = response.Content.ReadAsByteArrayAsync(deadline.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) { return new(status >= 300 ? httpOutcome : "TIMEOUT_OR_CANCELLED", null, status, retryAfter); }
            catch (Exception e) when (e is HttpRequestException or IOException) { return new(status >= 300 ? httpOutcome : "TRANSPORT_ERROR", null, status, retryAfter); }
            string? body;
            try { body = StrictUtf8.GetString(bodyBytes); }
            catch (DecoderFallbackException) { body = null; }
            var outcome = httpOutcome;
            if (outcome == "HTTP_OK" && body is null) outcome = "RESPONSE_NOT_UTF8";
            // Preserve an actionable, allowlisted diagnostic without exposing provider bodies, source text or credentials.
            if (outcome == "CLIENT_ERROR" && status == 400 && body is not null &&
                body.Contains("must contain the word", StringComparison.OrdinalIgnoreCase) &&
                body.Contains("json", StringComparison.OrdinalIgnoreCase) && body.Contains("response_format", StringComparison.OrdinalIgnoreCase))
                outcome = "CLIENT_ERROR:HTTP_400:JSON_MODE_REQUIRES_JSON_MESSAGE";
            return new(outcome, body, status, retryAfter, ErrorCode(body));
        }
    }

    public void Dispose() => client.Dispose();
}
