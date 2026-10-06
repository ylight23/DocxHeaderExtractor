using System.Text.Json;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Inference;

namespace DocxHeaderExtractor.Infrastructure.AI;

/// <summary>Production OpenRouter surface: ordinary boundary calls and frozen requests only.</summary>
public sealed class OpenRouterHeaderExtractor : IFrozenRequestHeaderClassifier, IDisposable
{
    private readonly OpenRouterTransportEngine _engine;

    public OpenRouterHeaderExtractor(HttpClient http, RemoteInferenceOptions options) =>
        _engine = new OpenRouterTransportEngine(http, options);

    private OpenRouterHeaderExtractor(OpenRouterTransportEngine engine) => _engine = engine;

    public static OpenRouterHeaderExtractor CreateOwned(RemoteInferenceOptions options) =>
        new(OpenRouterTransportEngine.CreateOwned(options));

    public string ModelName => _engine.ModelName;
    public int ContextSize => _engine.ContextSize;
    public string RuntimeDescription => _engine.RuntimeDescription;
    public int SharedPrefixTokens => _engine.SharedPrefixTokens;

    internal Func<TimeSpan, CancellationToken, Task> RetryWait
    {
        get => _engine.RetryWait;
        set => _engine.RetryWait = value;
    }

    public Task<string> BoundaryCutAsync(string systemPrompt, string userMessage, CancellationToken ct = default,
        int expectedItemCount = 0) => _engine.BoundaryCutAsync(systemPrompt, userMessage, ct, expectedItemCount);

    public Task<(string Content, string? FinishReason)> ExecuteAsync(byte[] payloadBytes, int maxTokens,
        string systemPrompt, string userMessage, CancellationToken ct = default) =>
        _engine.ExecuteAsync(payloadBytes, maxTokens, systemPrompt, userMessage, ct);

    public Task<FrozenHeaderExecutionResult> ExecuteFrozenRequestAsync(byte[] providerBody, int maxTokens,
        string systemPrompt, string userMessage, CancellationToken cancellationToken = default) =>
        _engine.ExecuteFrozenRequestAsync(providerBody, maxTokens, systemPrompt, userMessage, cancellationToken);

    internal static int BoundaryOutputBudgetFor(string userMessage, int expectedItemCount, int maxOutputTokens) =>
        OpenRouterTransportEngine.BoundaryOutputBudgetFor(userMessage, expectedItemCount, maxOutputTokens);

    public void Dispose() => _engine.Dispose();
}

/// <summary>Qualification-only raw observation, unconstrained-response, and tool-call transport surface.</summary>
public sealed class OpenRouterQualificationTransport : IFrozenRequestHeaderClassifier, IDisposable
{
    private readonly OpenRouterTransportEngine _engine;

    public OpenRouterQualificationTransport(HttpClient http, RemoteInferenceOptions options) =>
        _engine = new OpenRouterTransportEngine(http, options);

    private OpenRouterQualificationTransport(OpenRouterTransportEngine engine) => _engine = engine;

    public static OpenRouterQualificationTransport CreateOwned(RemoteInferenceOptions options) =>
        new(OpenRouterTransportEngine.CreateOwned(options));

    public string ModelName => _engine.ModelName;
    public int ContextSize => _engine.ContextSize;
    public string RuntimeDescription => _engine.RuntimeDescription;
    public int SharedPrefixTokens => _engine.SharedPrefixTokens;

    public Task<string> BoundaryCutAsync(string systemPrompt, string userMessage, CancellationToken ct = default,
        int expectedItemCount = 0) => _engine.BoundaryCutAsync(systemPrompt, userMessage, ct, expectedItemCount);

    public Task<(string Content, string? FinishReason)> ExecuteAsync(byte[] payloadBytes, int maxTokens,
        string systemPrompt, string userMessage, CancellationToken ct = default) =>
        _engine.ExecuteAsync(payloadBytes, maxTokens, systemPrompt, userMessage, ct);

    public Task<FrozenHeaderExecutionResult> ExecuteFrozenRequestAsync(byte[] providerBody, int maxTokens,
        string systemPrompt, string userMessage, CancellationToken cancellationToken = default) =>
        _engine.ExecuteFrozenRequestAsync(providerBody, maxTokens, systemPrompt, userMessage, cancellationToken);

    public Task<OpenRouterExecutionObservation> ExecuteObservedAsync(byte[] payloadBytes, int maxTokens,
        string systemPrompt, string userMessage, CancellationToken ct = default) =>
        _engine.ExecuteObservedAsync(payloadBytes, maxTokens, systemPrompt, userMessage, ct);

    public Task<OpenRouterExecutionObservation> ExecuteObservedUnconstrainedAsync(byte[] payloadBytes, int maxTokens,
        string systemPrompt, string userMessage, CancellationToken ct = default) =>
        _engine.ExecuteObservedUnconstrainedAsync(payloadBytes, maxTokens, systemPrompt, userMessage, ct);

    public Task<(string Content, string? FinishReason, IReadOnlyList<V5ToolCallDeltaFragment> ToolCallFragments, JsonElement? Usage)>
        ExecuteToolCallAsync(byte[] payloadBytes, int maxTokens, string systemPrompt, string userMessage, CancellationToken ct = default) =>
        _engine.ExecuteToolCallAsync(payloadBytes, maxTokens, systemPrompt, userMessage, ct);

    public void Dispose() => _engine.Dispose();
}

public sealed record OpenRouterExecutionObservation(
    string Content,
    string? FinishReason,
    JsonElement? Usage,
    string RawSse,
    int SseEventCount,
    int RetryCount);
