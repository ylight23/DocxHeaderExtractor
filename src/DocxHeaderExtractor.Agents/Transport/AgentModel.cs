using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
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

    /// <summary>How many times a production call waits and sends again when the provider answers 429 (too many requests).</summary>
    public const int ProductionRateLimitRetries = 5;

    /// <param name="rateLimitRetries">On 429 the same bytes are sent again after a wait that doubles each time from
    /// <paramref name="rateLimitBaseDelayMs"/>, with jitter so parallel calls do not come back together. 0 (the default) fails at once, as before.</param>
    public AgentModel(HttpMessageHandler handler, string apiKey, AgentBudget budget, TimeSpan? timeout = null, CancellationToken cancellationToken = default,
        int rateLimitRetries = 0, int rateLimitBaseDelayMs = 4000)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("API_KEY_MISSING", nameof(apiKey));
        client = new HttpClient(handler, disposeHandler: false) { Timeout = timeout ?? TimeSpan.FromSeconds(300) };
        this.apiKey = apiKey; this.budget = budget;
        this.cancellationToken = cancellationToken;
        this.rateLimitRetries = rateLimitRetries; this.rateLimitBaseDelayMs = rateLimitBaseDelayMs;
    }

    /// <summary>How many 429 answers were waited out and sent again.</summary>
    public int RateLimitWaits => rateLimitWaits;
    int rateLimitWaits;

    public int PromptTokens { get; private set; }
    public int CompletionTokens { get; private set; }
    public int ReasoningTokens { get; private set; }
    public int CachedTokens { get; private set; }

    public static HttpClientHandler CreateHandler() => new() { AllowAutoRedirect = false, UseCookies = false };

    public ModelTurn Turn(string unitId, int turn, PreparedRequest request)
    {
        var text = ReplyRaw(request);
        var (reply, failure) = ReadResponse(text);
        if (reply is null) throw new ReplyFailure("CONTENT:" + failure);
        return reply;
    }

    static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>One audited call; returns the chat-completion view assembled from the stream.</summary>
    string ReplyRaw(PreparedRequest request)
    {
        var reserve = AgentBudget.AttemptUsd(request.Bytes);
        var (outcome, body) = Send(request);
        for (var k = 0; outcome == "RATE_LIMITED_NO_RETRY" && k < rateLimitRetries; k++)
        {
            // The provider said too many requests: a 429 costs nothing, so the same bytes go again after a doubling wait with jitter.
            var wait = rateLimitBaseDelayMs * (1 << k);
            wait = wait / 2 + Random.Shared.Next(wait / 2 + 1);
            if (cancellationToken.WaitHandle.WaitOne(wait)) cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref rateLimitWaits);
            (outcome, body) = Send(request);
        }
        if (outcome != "HTTP_OK")
        {
            if (outcome is "BOUNDARY_SHA_MISMATCH" or "PREPARED_SHA_MISMATCH") budget.Halt(outcome);
            budget.RecordActual(null, reserve);
            throw new ReplyFailure("TRANSPORT:" + outcome);
        }
        var raw = body ?? "";
        if (raw.Contains(apiKey, StringComparison.Ordinal))
        {
            budget.Halt("API_KEY_ECHOED");
            throw new ReplyFailure("API_KEY_ECHOED");
        }
        var asm = Sse.Parse(raw);
        var broken = Sse.Guard(raw) ?? (asm.Error ? "STREAM_ERROR" : !asm.Done ? "STREAM_INCOMPLETE" : asm.MalformedEvents > 0 ? "STREAM_MALFORMED" : null);
        if (broken is not null)
        {
            budget.RecordActual(asm.Cost, reserve);
            throw new ReplyFailure("TRANSPORT:" + broken);
        }
        var text = asm.View();
        var audit = Usage.Check(text, request.Bytes);
        budget.RecordActual(audit.ProviderCostUsd, reserve);
        lock (usageGate)
        {
            PromptTokens += audit.PromptTokens ?? 0; CompletionTokens += audit.CompletionTokens ?? 0;
            ReasoningTokens += audit.ReasoningTokens ?? 0; CachedTokens += audit.CachedTokens ?? 0;
        }
        if (!string.Equals(asm.Provider, "Alibaba", StringComparison.Ordinal) || asm.Model != ChatModel)
        {
            budget.Halt("ENDPOINT_OR_MODEL_UNEXPECTED");
            throw new ReplyFailure("ENDPOINT_OR_MODEL_UNEXPECTED");
        }
        if (!(audit.Codes.Count == 1 && audit.Codes[0] == "OK")) throw new ReplyFailure("AUDIT:" + string.Join(",", audit.Codes));
        return text;
    }

    (string Outcome, string? Body) Send(PreparedRequest request)
    {
        var bytes = Encoding.UTF8.GetBytes(request.Body);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != request.Sha256) return ("PREPARED_SHA_MISMATCH", null);
        using var message = new HttpRequestMessage(HttpMethod.Post, Url) { Content = new ByteArrayContent(bytes) };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        HttpResponseMessage response;
        try { response = client.SendAsync(message, cancellationToken).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return ("TIMEOUT_OR_CANCELLED", null); }
        catch (HttpRequestException) { return ("TRANSPORT_ERROR", null); }
        using (response)
        {
            var status = (int)response.StatusCode;
            byte[] bodyBytes;
            try { bodyBytes = response.Content.ReadAsByteArrayAsync(cancellationToken).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException) { return ("TRANSPORT_ERROR", null); }
            string? body;
            try { body = StrictUtf8.GetString(bodyBytes); }
            catch (DecoderFallbackException) { body = null; }
            var outcome = status switch
            {
                >= 200 and < 300 => "HTTP_OK",
                >= 300 and < 400 => "REDIRECT_REFUSED",
                429 => "RATE_LIMITED_NO_RETRY",
                // The provider account has no credit left: not retried, and named apart so the failure says what to fix.
                402 => "PAYMENT_REQUIRED",
                >= 500 => "SERVER_ERROR_NO_RETRY",
                >= 400 => "CLIENT_ERROR",
                _ => "UNEXPECTED_STATUS",
            };
            if (outcome == "HTTP_OK" && body is null) outcome = "RESPONSE_NOT_UTF8";
            // Preserve an actionable, allowlisted diagnostic without exposing provider bodies, source text or credentials.
            if (outcome == "CLIENT_ERROR" && status == 400 && body is not null &&
                body.Contains("must contain the word", StringComparison.OrdinalIgnoreCase) &&
                body.Contains("json", StringComparison.OrdinalIgnoreCase) && body.Contains("response_format", StringComparison.OrdinalIgnoreCase))
                outcome = "CLIENT_ERROR:HTTP_400:JSON_MODE_REQUIRES_JSON_MESSAGE";
            return (outcome, body);
        }
    }

    public void Dispose() => client.Dispose();
}
