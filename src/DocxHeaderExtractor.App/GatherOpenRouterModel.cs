using System.Text.Json.Nodes;
using System.Text.Json;
using DocxHeaderExtractor.Agents;
using DocxHeaderExtractor.Agents.Budget;
using DocxHeaderExtractor.Agents.Transport;
using DocxHeaderExtractor.DocumentProcessing.Gather;

namespace DocxHeaderExtractor.App;

/// <summary>Audited OpenRouter/Alibaba transport for an experimental gather run. One instance owns one shared budget.</summary>
public sealed class GatherOpenRouterModel : IGatherModel, IDisposable
{
    readonly string apiKey;
    readonly HttpMessageHandler handler;
    readonly AgentBudget budget;
    readonly int maxTokens;
    readonly string? reasoningEffort;
    readonly string? diagnosticsPath;
    readonly object diagnosticsGate = new();
    readonly HashSet<string> loggedAttempts = [];
    int calls;

    public GatherOpenRouterModel(string apiKey, int maxCalls, double ceilingUsd, int maxTokens = 32768,
        string? reasoningEffort = null, HttpMessageHandler? handler = null, string? diagnosticsPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        if (maxTokens <= 0 || maxTokens > OpenRouterWire.MaxTokens) throw new ArgumentOutOfRangeException(nameof(maxTokens));
        this.apiKey = apiKey;
        this.handler = handler ?? AgentModel.CreateHandler();
        budget = new AgentBudget(maxCalls, ceilingUsd);
        this.maxTokens = maxTokens;
        this.reasoningEffort = reasoningEffort;
        this.diagnosticsPath = diagnosticsPath is null ? null : Path.GetFullPath(diagnosticsPath);
    }

    /// <summary>Identity to include in checkpoint keys. Increment the version when the wire or model settings change.</summary>
    public string CheckpointIdentity => CheckpointIdentityFor(maxTokens, reasoningEffort);

    public static string CheckpointIdentityFor(int maxTokens = 32768, string? reasoningEffort = null) =>
        $"openrouter:{OpenRouterWire.ChatModel}:{OpenRouterWire.ProviderTag}:wire-v1:max={maxTokens}:reasoning={reasoningEffort ?? "enabled"}";

    public IReadOnlyList<GatherProviderAttempt> Attempts => budget.Attempts.Select(a => new GatherProviderAttempt(
        a.RequestSha256, a.Resources.WireBytes, a.Resources.CompletionTokens, a.Status, a.Transport?.Outcome,
        a.Transport?.RequestSeconds, a.Usage?.PromptTokens, a.Usage?.CompletionTokens, a.Usage?.ReasoningTokens,
        a.Usage?.ProviderCostUsd, a.ReserveUsd)).ToList();

    public Task<string> CompleteAsync(string system, string user, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        var request = OpenRouterWire.Build(system, user, new JsonArray(), null, null,
            new WireOptions(MaxTokens: maxTokens, ReasoningEffort: reasoningEffort));
        var call = Interlocked.Increment(ref calls);
        Log(new { kind = "call_start", utc = DateTimeOffset.UtcNow, call, request.Sha256, request.Bytes, maxTokens });
        using var model = new AgentModel(handler, apiKey, budget, cancellationToken: ct,
            rateLimitRetries: AgentModel.ProductionRateLimitRetries);
        try
        {
            var reply = model.Turn($"gather-{call:000}", 1, request);
            Log(new { kind = "call_end", utc = DateTimeOffset.UtcNow, call, outcome = "OK" });
            return reply.Content ?? throw new GatherModelFailureException("CONTENT_MISSING", false);
        }
        catch (ReplyFailure ex)
        {
            Log(new { kind = "call_end", utc = DateTimeOffset.UtcNow, call, outcome = ex.Code });
            var codes = ex.Code.StartsWith("AUDIT:", StringComparison.Ordinal) ? ex.Code[6..].Split(',') : [];
            if (codes.Contains("FINISH_LENGTH") && codes.All(c => c is "FINISH_LENGTH" or "REASONING_EXHAUSTED"))
                throw new GatherCompletionTruncatedException();
            // A transport that already applied its retry policy and a budget refusal must never restart as a whole call.
            var retryable = !ex.TransportRetriesHandled && !ex.Code.StartsWith("BUDGET:", StringComparison.Ordinal)
                && !ex.Code.StartsWith("TRANSPORT:", StringComparison.Ordinal);
            throw new GatherModelFailureException(ex.Code, retryable);
        }
        catch (OperationCanceledException)
        {
            Log(new { kind = "call_end", utc = DateTimeOffset.UtcNow, call, outcome = "CANCELLED" });
            throw;
        }
        finally
        {
            lock (diagnosticsGate)
                foreach (var a in budget.Attempts.Where(a => loggedAttempts.Add(a.Id.ToString())))
                    Log(new { kind = "attempt", utc = DateTimeOffset.UtcNow, a.Id, a.RequestSha256, a.Status,
                        a.ReserveUsd, a.CostUsd, a.Usage, a.Transport });
        }
    }, ct);

    void Log(object value)
    {
        if (diagnosticsPath is null) return;
        lock (diagnosticsGate) File.AppendAllText(diagnosticsPath, JsonSerializer.Serialize(value) + Environment.NewLine);
    }

    public void Dispose() => handler.Dispose();
}

/// <summary>Safe provider telemetry; null cost means billing is unknown and the reserve remains provisional.</summary>
public sealed record GatherProviderAttempt(string RequestSha256, int RequestBytes, int CompletionCap, string Status,
    string? Outcome, double? RequestSeconds, int? PromptTokens, int? CompletionTokens, int? ReasoningTokens,
    double? ProviderCostUsd, double ReservedUsd);
