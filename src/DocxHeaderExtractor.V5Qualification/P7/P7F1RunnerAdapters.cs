using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.V5Qualification.P7;

/// <summary>Qualification transport only. Not reachable from the dry-run CLI. Key acquisition is lazy,
/// after runner reservation. Private options enforce one attempt; redirects are disabled.</summary>
internal sealed class P7OpenRouterF1RunnerTransport : IP7F1RunnerTransport, IDisposable
{
    private readonly Func<string> readApprovedDedicatedKey;
    private HttpClient? http;
    private OpenRouterTransportEngine? observed;
    public P7RunnerTransportPolicy Policy { get; } = new(P7FinancialQualification.ChatEndpoint, "qwen/qwen3.7-flash", "alibaba", 0, false, 300, false);
    public P7OpenRouterF1RunnerTransport(Func<string> readApprovedDedicatedKey) => this.readApprovedDedicatedKey = readApprovedDedicatedKey;
    public async Task<P7RunnerRawObservation> SendOnceAsync(byte[] body, CancellationToken ct)
    {
        P7F1ExecutionReadiness.ValidateCarrier(body);
        ct.ThrowIfCancellationRequested();
        if (observed is null) {
            var key = readApprovedDedicatedKey();
            P7FinancialQualification.Need(!string.IsNullOrWhiteSpace(key), "APPROVED_DEDICATED_KEY_MISSING");
            http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
            observed = new OpenRouterTransportEngine(http, new RemoteInferenceOptions {
                Endpoint = new(P7FinancialQualification.ChatEndpoint), Model = Policy.Model, ApiKey = key,
                ProviderTransportTimeoutSeconds = 300, TransientRequestRetries = 0, MaxOutputTokens = 32768,
                OpenRouterProviderRoute = "alibaba", RequireJsonObjectResponse = true });
        }
        using var parsed = JsonDocument.Parse(body); var messages = parsed.RootElement.GetProperty("messages");
        var result = await observed.ExecuteObservedAsync(body, 32768, messages[0].GetProperty("content").GetString()!,
            messages[1].GetProperty("content").GetString()!, ct).ConfigureAwait(false);
        return new(result.Content, result.FinishReason, result.Usage?.Clone(), result.RawSse, result.SseEventCount, result.RetryCount);
    }
    public void Dispose() { observed?.Dispose(); http?.Dispose(); }
}

/// <summary>Durable create-new journal. An existing directory cannot be resumed or replayed.
/// Capture directory is private: request bytes may contain source excerpts.</summary>
internal sealed class P7F1FileJournal : IP7F1RunnerJournal
{
    private readonly string root;
    private readonly IReadOnlyDictionary<string, int> sequence;
    private readonly bool fixtureOnly;
    public P7F1FileJournal(string root, P7F1ExecutionPlan plan, bool fixtureOnly)
    {
        this.root = Path.GetFullPath(root); this.fixtureOnly = fixtureOnly;
        P7FinancialQualification.Need(!Directory.Exists(this.root) && !File.Exists(this.root), "JOURNAL_ALREADY_EXISTS_NO_REPLAY");
        sequence = plan.Calls.ToDictionary(c => c.CallHandle, c => c.Sequence, StringComparer.Ordinal);
        Directory.CreateDirectory(this.root);
    }
    public Task BeginAsync(byte[] identity, CancellationToken ct) { ct.ThrowIfCancellationRequested(); Write(Path.Combine(root, "run-claim.json"), identity); return Task.CompletedTask; }
    public Task ReservationAsync(string handle, byte[] reservation, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); Write(Path.Combine(CallDirectory(handle), "reservation.json"), reservation); return Task.CompletedTask; }
    public Task<P7RunnerRawReceipt> FreezeRawAsync(string handle, byte[] body, P7RunnerRawObservation observation)
    {
        var dir = CallDirectory(handle);
        var response = Encoding.UTF8.GetBytes(observation.Content); var sse = Encoding.UTF8.GetBytes(observation.RawSse);
        // Observation uses the existing capture casing. Origin is carried by run/raw markers, not invented provider evidence.
        var obs = JsonSerializer.SerializeToUtf8Bytes(new { observation.Content, observation.FinishReason, observation.Usage,
            observation.RawSse, observation.SseEventCount, observation.RetryCount });
        Write(Path.Combine(dir, "provider-body.json"), body); Write(Path.Combine(dir, "response.txt"), response);
        Write(Path.Combine(dir, "response.sse"), sse); Write(Path.Combine(dir, "observation.json"), obs);
        var hashes = new[] { ("provider-body.json", body), ("response.txt", response), ("response.sse", sse) }
            .Select(p => new { file = p.Item1, sha256 = SpatialCanonical.Hash(p.Item2) }).ToArray();
        var marker = SpatialCanonical.Bytes(new { status = "RAW_FROZEN_BEFORE_PARSE", origin = fixtureOnly ? "SYNTHETIC_FINANCIAL_DRY_RUN" : "PROVIDER_RAW",
            hashes, observationSha256 = SpatialCanonical.Hash(obs), protocolValidated = false, semanticValidated = false });
        Write(Path.Combine(dir, "raw-freeze.json"), marker);
        return Task.FromResult(new P7RunnerRawReceipt(SpatialCanonical.Hash(obs), SpatialCanonical.Hash(response), SpatialCanonical.Hash(sse), SpatialCanonical.Hash(marker)));
    }
    public Task FinancialAsync(string handle, byte[] receipt) { Write(Path.Combine(CallDirectory(handle), "financial-receipt.json"), receipt); return Task.CompletedTask; }
    public Task CompleteAsync(byte[] result) { Write(Path.Combine(root, "execution-result.json"), result); return Task.CompletedTask; }
    private string CallDirectory(string handle) { var dir = Path.Combine(root, sequence[handle].ToString("D2", System.Globalization.CultureInfo.InvariantCulture)); Directory.CreateDirectory(dir); return dir; }
    private static void Write(string path, byte[] bytes) { using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); file.Write(bytes); file.Flush(true); }
}

/// <summary>Sealed fake, not provider execution or semantic inference. Used only by dry-run CLI.</summary>
internal sealed class P7F1DryRunTransport : IP7F1RunnerTransport
{
    public P7RunnerTransportPolicy Policy { get; } = new(P7FinancialQualification.ChatEndpoint, "qwen/qwen3.7-flash", "alibaba", 0, false, 300, true);
    public List<string> BodyHashes { get; } = [];
    public Task<P7RunnerRawObservation> SendOnceAsync(byte[] body, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); BodyHashes.Add(SpatialCanonical.Hash(body));
        using var b = JsonDocument.Parse(body);
        using var user = JsonDocument.Parse(b.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
        var input = user.RootElement.TryGetProperty("stageInput", out var stage) ? stage : user.RootElement;
        // Not used to measure accuracy or drive downstream. Do not substitute these decisions for real F1 outputs.
        var decisions = input.GetProperty("occurrences").EnumerateArray().Select(o => new {
            occurrence = o.GetProperty("id").GetString(), function = "OTHER" }).ToArray();
        var content = JsonSerializer.Serialize(new { decisions });
        var usage = SpatialCanonical.Element(new { prompt_tokens = 200, completion_tokens = 40, cost = 0.01m,
            completion_tokens_details = new { reasoning_tokens = 30 } });
        return Task.FromResult(new P7RunnerRawObservation(content, "stop", usage, "data: SYNTHETIC_FINANCIAL_FIXTURE\n\ndata: [DONE]\n", 2, 0));
    }
}
