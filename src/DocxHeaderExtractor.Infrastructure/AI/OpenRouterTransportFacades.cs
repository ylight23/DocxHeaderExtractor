using DocxHeaderExtractor.DocumentProcessing.Inference;

namespace DocxHeaderExtractor.Infrastructure.AI;

/// <summary>
/// Production OpenRouter surface: ordinary boundary calls and frozen requests only. Raw observation,
/// unconstrained-response, and tool-call transport live in the qualification assembly, which reaches
/// <see cref="OpenRouterTransportEngine"/> through InternalsVisibleTo.
/// </summary>
public sealed class OpenRouterInferenceTransport : IFrozenInferenceTransport, IDisposable
{
    private readonly OpenRouterTransportEngine _engine;

    public OpenRouterInferenceTransport(HttpClient http, RemoteInferenceOptions options) =>
        _engine = new OpenRouterTransportEngine(http, options);

    private OpenRouterInferenceTransport(OpenRouterTransportEngine engine) => _engine = engine;

    public static OpenRouterInferenceTransport CreateOwned(RemoteInferenceOptions options) =>
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

    public Task<FrozenInferenceResponse> ExecuteFrozenRequestAsync(byte[] providerBody, int maxTokens,
        string systemPrompt, string userMessage, CancellationToken cancellationToken = default) =>
        _engine.ExecuteFrozenRequestAsync(providerBody, maxTokens, systemPrompt, userMessage, cancellationToken);

    internal static int BoundaryOutputBudgetFor(string userMessage, int expectedItemCount, int maxOutputTokens) =>
        OpenRouterTransportEngine.BoundaryOutputBudgetFor(userMessage, expectedItemCount, maxOutputTokens);

    public void Dispose() => _engine.Dispose();
}
