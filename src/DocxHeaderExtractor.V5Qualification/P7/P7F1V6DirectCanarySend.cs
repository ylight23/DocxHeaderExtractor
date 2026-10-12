using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DocxHeaderExtractor.V5Qualification.P7;

/// <summary>
/// Canary T1 send entrypoint (D4.2). It sends exactly the pinned bytes of the body file: the file is read once, its SHA and length are checked
/// against the pin, and the same bytes go to the transport. The body is never parsed back into an object and serialized again. Preflight runs
/// before any connection; if it fails, no request is made. One attempt only: the transport never retries, never follows a redirect, and this
/// entrypoint never falls back to another route. The API key is passed in, used only in the Authorization header, and never written to a file.
/// The response is stored as received. Missing reasoning usage is recorded as UNKNOWN; this code draws no conclusion about thinking being on or off.
/// </summary>
internal static class P7F1V6DirectCanarySend
{
    public const string PinnedSha256 = "681ecdd320d346ddc6d11ef9ecfa22b8f4b599ee4ce64e256f9da9473f8aa645";
    public const int PinnedBytes = 1804;
    public const string BodyFile = "canary-t1-request.body.json";
    public const string ResultFile = "canary-t1-result.v1.json";
    public const string ResponseFile = "canary-t1-response.raw.json";
    public const int MaxPosts = 1;
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(180);

    public sealed record Preflight(bool Allowed, IReadOnlyList<string> Failures, P7F1V6Protocol.PreparedRequest? Request, double ReserveUsd);

    public sealed record SendRecord(string Outcome, int? Status, int Attempts, string? BoundarySha256, long? BoundaryBytes, bool Sent);

    /// <summary>Every check that must hold before a connection is opened. Reads the body file once and works on those bytes.</summary>
    public static Preflight Check(string artifactDir, string confirmSha, string? apiKey, int postsRequested)
    {
        var failures = new List<string>();
        if (confirmSha != PinnedSha256) failures.Add("CONFIRM_SHA_NOT_PINNED");
        if (postsRequested != MaxPosts) failures.Add("POST_COUNT_NOT_ONE");
        if (string.IsNullOrWhiteSpace(apiKey)) failures.Add("API_KEY_MISSING");

        var path = Path.Combine(artifactDir, BodyFile);
        if (!File.Exists(path)) { failures.Add("BODY_FILE_MISSING"); return new(false, failures, null, 0); }
        var bytes = File.ReadAllBytes(path);
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (sha != PinnedSha256) failures.Add("BODY_SHA_MISMATCH");
        if (bytes.Length != PinnedBytes) failures.Add("BODY_LENGTH_MISMATCH");

        // The body must be valid UTF-8 that round-trips to the same bytes, so the string handed to the transport is the file.
        var body = Encoding.UTF8.GetString(bytes);
        if (!Encoding.UTF8.GetBytes(body).AsSpan().SequenceEqual(bytes)) failures.Add("BODY_NOT_UTF8_ROUND_TRIP");
        if (!string.IsNullOrEmpty(apiKey) && body.Contains(apiKey, StringComparison.Ordinal)) failures.Add("API_KEY_IN_BODY");

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.GetProperty("model").GetString() != P7F1V6Protocol.ChatModel) failures.Add("MODEL_NOT_APPROVED");
            var maxTokens = root.TryGetProperty("max_tokens", out var mt) && mt.TryGetInt32(out var m) ? m : -1;
            if (maxTokens < 1 || maxTokens > P7F1V6DirectCanary.MaxTokensCap) failures.Add("MAX_TOKENS_OUT_OF_RANGE");
            if (root.TryGetProperty("reasoning", out _) || body.Contains("\"reasoning\"", StringComparison.Ordinal)) failures.Add("REASONING_DECLARED");
            if (!root.TryGetProperty("usage", out var usage) || !usage.GetProperty("include").GetBoolean()) failures.Add("USAGE_INCLUDE_MISSING");
            var route = root.GetProperty("provider");
            var only = route.GetProperty("only").EnumerateArray().Select(x => x.GetString()).ToArray();
            var ignore = route.GetProperty("ignore").EnumerateArray().Select(x => x.GetString()).ToArray();
            if (!only.SequenceEqual(new[] { P7F1V6Protocol.ProviderTag })) failures.Add("ROUTE_ONLY_NOT_ALIBABA");
            if (!ignore.Contains("alibaba/us-east-1")) failures.Add("ROUTE_IGNORE_MISSING_US_EAST_1");
            if (route.GetProperty("allow_fallbacks").GetBoolean()) failures.Add("FALLBACKS_ENABLED");
            if (!route.GetProperty("require_parameters").GetBoolean()) failures.Add("REQUIRE_PARAMETERS_OFF");
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            failures.Add("BODY_STRUCTURE_INVALID");
        }

        var reserve = P7F1V6Direct.AttemptUsd(bytes.Length);
        if (reserve >= P7F1V6DirectCanary.CeilingUsd) failures.Add("RESERVE_NOT_BELOW_CEILING");

        var allowed = failures.Count == 0;
        return new(allowed, failures, allowed ? new P7F1V6Protocol.PreparedRequest(body, bytes.Length, sha) : null, reserve);
    }

    /// <summary>Runs preflight and, only if it passes, sends the one request through the transport and writes the record. The handler is the
    /// network boundary: production passes P7F1V6OpenRouterTransport.CreateProductionHandler(); tests pass a fake.</summary>
    public static SendRecord Run(string artifactDir, string confirmSha, string? apiKey, int postsRequested, HttpMessageHandler handler)
    {
        Directory.CreateDirectory(artifactDir);
        var pre = Check(artifactDir, confirmSha, apiKey, postsRequested);
        if (!pre.Allowed)
        {
            WriteResult(artifactDir, new { status = "PREFLIGHT_FAILED", sent = false, failures = pre.Failures, attempts = 0 });
            return new SendRecord("PREFLIGHT_FAILED", null, 0, null, null, false);
        }

        var transport = new P7F1V6OpenRouterTransport(handler, RequestTimeout);
        var r = transport.Send(pre.Request!, apiKey!);
        var record = new SendRecord(r.Outcome, r.Status, r.Attempts, r.BoundarySha256, r.BoundaryBytes, true);

        // The response is kept as it arrived, unless it echoes the key; then nothing is written, and that is recorded.
        var keyEchoed = r.Body is not null && r.Body.Contains(apiKey!, StringComparison.Ordinal);
        if (r.Body is not null && !keyEchoed)
            File.WriteAllText(Path.Combine(artifactDir, ResponseFile), r.Body, new UTF8Encoding(false));

        var audit = r.Body is null || keyEchoed ? null : P7F1V6Usage.Check(r.Body, pre.Request!.Bytes, P7F1V6Protocol.MaxTokens);
        using var responseDoc = r.Body is null || keyEchoed ? null : TryParse(r.Body);
        var root = responseDoc?.RootElement;
        WriteResult(artifactDir, new
        {
            status = r.Outcome,
            sent = true,
            attempts = r.Attempts,
            httpStatus = r.Status,
            boundarySha256 = r.BoundarySha256,
            boundaryBytes = r.BoundaryBytes,
            boundaryMatchesPinned = r.BoundarySha256 == PinnedSha256 && r.BoundaryBytes == PinnedBytes,
            responseSha256 = r.Body is null ? null : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(r.Body))),
            responseFile = r.Body is null || keyEchoed ? null : ResponseFile,
            apiKeyEchoedInResponse = keyEchoed,
            providerReportedModel = root is not null && root.Value.TryGetProperty("model", out var mo) && mo.ValueKind == JsonValueKind.String ? mo.GetString() : "UNKNOWN",
            providerReportedEndpoint = root is not null && root.Value.TryGetProperty("provider", out var pr) && pr.ValueKind == JsonValueKind.String ? pr.GetString() : "UNKNOWN",
            finishReason = audit?.FinishReason ?? "UNKNOWN",
            usage = audit is null ? null : new
            {
                promptTokens = (object?)audit.PromptTokens ?? "UNKNOWN",
                completionTokens = (object?)audit.CompletionTokens ?? "UNKNOWN",
                reasoningTokens = (object?)audit.ReasoningTokens ?? "UNKNOWN",
                visibleTokens = (object?)audit.VisibleTokens ?? "UNKNOWN",
                providerCostUsd = (object?)audit.ProviderCostUsd ?? "UNKNOWN",
                computedCostUsd = audit.ComputedCostUsd,
            },
            auditCodes = audit?.Codes ?? new[] { "NO_RESPONSE_BODY" },
            reasoningNote = "UNKNOWN means the response did not report reasoning tokens. It does not establish whether thinking was on or off.",
            worstCaseReserveUsd = pre.ReserveUsd,
            ceilingUsd = P7F1V6DirectCanary.CeilingUsd,
        });
        return record;
    }

    static JsonDocument? TryParse(string body)
    {
        try { return JsonDocument.Parse(body); }
        catch (JsonException) { return null; }
    }

    static void WriteResult(string artifactDir, object result) =>
        File.WriteAllText(Path.Combine(artifactDir, ResultFile), JsonSerializer.Serialize(result,
            new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n", new UTF8Encoding(false));
}
