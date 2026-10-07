using System.Text.Json;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.V5Qualification;

/// <summary>
/// Qualification-only OpenRouter transport: raw observation, unconstrained responses, and tool calls.
/// Lives outside the production Infrastructure assembly and drives its internal transport engine.
/// </summary>
public sealed class OpenRouterQualificationTransport : IFrozenInferenceTransport, IDisposable
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
    public IFrozenInferenceRequestComposer RequestComposer { get; } = new OpenRouterQwen37InferenceRequestComposer();

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

    public Task<FrozenInferenceResult> ExecuteFrozenRequestAsync(byte[] providerBody, int maxTokens,
        string systemPrompt, string userMessage, CancellationToken cancellationToken = default) =>
        _engine.ExecuteFrozenRequestAsync(providerBody, maxTokens, systemPrompt, userMessage, cancellationToken);

    public async Task<OpenRouterExecutionObservation> ExecuteObservedAsync(byte[] payloadBytes, int maxTokens,
        string systemPrompt, string userMessage, CancellationToken ct = default) =>
        Map(await _engine.ExecuteObservedAsync(payloadBytes, maxTokens, systemPrompt, userMessage, ct).ConfigureAwait(false));

    public async Task<OpenRouterExecutionObservation> ExecuteObservedUnconstrainedAsync(byte[] payloadBytes, int maxTokens,
        string systemPrompt, string userMessage, CancellationToken ct = default) =>
        Map(await _engine.ExecuteObservedUnconstrainedAsync(payloadBytes, maxTokens, systemPrompt, userMessage, ct).ConfigureAwait(false));

    public async Task<(string Content, string? FinishReason, IReadOnlyList<V5ToolCallDeltaFragment> ToolCallFragments, JsonElement? Usage)>
        ExecuteToolCallAsync(byte[] payloadBytes, int maxTokens, string systemPrompt, string userMessage, CancellationToken ct = default)
    {
        var (content, finishReason, fragments, usage) = await _engine
            .ExecuteToolCallAsync(payloadBytes, maxTokens, systemPrompt, userMessage, ct).ConfigureAwait(false);
        return (content, finishReason, Map(fragments), usage);
    }

    public void Dispose() => _engine.Dispose();

    private static OpenRouterExecutionObservation Map(OpenRouterTransportObservation value) =>
        new(value.Content, value.FinishReason, value.Usage, value.RawSse, value.SseEventCount, value.RetryCount);

    internal static IReadOnlyList<V5ToolCallDeltaFragment> Map(IReadOnlyList<OpenRouterToolCallDelta> fragments) =>
        fragments.Select(value => new V5ToolCallDeltaFragment(value.Index, value.Id, value.FunctionName, value.ArgumentsChunk)).ToArray();
}

public sealed record OpenRouterExecutionObservation(
    string Content,
    string? FinishReason,
    JsonElement? Usage,
    string RawSse,
    int SseEventCount,
    int RetryCount);
