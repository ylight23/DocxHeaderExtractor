using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using System.Text.Json;

namespace DocxHeaderExtractor.DocumentProcessing.Inference;

/// <summary>Provider-neutral classifier contract consumed by document processing.</summary>
public interface IHeaderClassifier : IDisposable
{
    string ModelName { get; }
    int ContextSize { get; }
    string RuntimeDescription { get; }
    /// <summary>Số token prefix đã cache; backend không hỗ trợ trả 0.</summary>
    int SharedPrefixTokens { get; }

    /// <summary>
    /// The one model call the canonical lanes make: a system prompt and a user message, and the
    /// completion text back (trimmed). Callers validate the reply against their own contract;
    /// the backend guarantees nothing about its shape.
    /// </summary>
    /// <param name="expectedItemCount">
    /// How many result items the caller is asking about, when it knows. Backends that must size an
    /// output budget use it instead of guessing from the payload's shape. 0 means "unknown, infer".
    /// Guessing was a real defect: the budget was derived by counting a field name, so renaming a
    /// field in the request silently collapsed the budget to its floor and truncated the reply.
    /// </param>
    Task<string> BoundaryCutAsync(
        string systemPrompt,
        string userMessage,
        CancellationToken ct = default,
        int expectedItemCount = 0);
}

/// <summary>
/// Composition-root seam for inference providers. Core consumes the neutral classifier contract;
/// provider construction belongs to Infrastructure.
/// </summary>
public interface IHeaderClassifierFactory
{
    bool SendsDataExternally => false;

    Task<IHeaderClassifier> CreateAsync(PipelineOptions options, CancellationToken ct = default);
}

/// <summary>Raw completion returned by executing an already frozen provider request body.</summary>
public sealed record FrozenHeaderExecutionResult(
    string Content, string? FinishReason, JsonElement? Usage, string RawSse, int SseEventCount, int RetryCount);

/// <summary>
/// Optional exact-body surface for production candidates whose body composer is part of their
/// semantic contract. Implementations must send the supplied bytes verbatim and retain their normal
/// transport-only retry policy; they must not rebuild or edit the request.
/// </summary>
public interface IFrozenRequestHeaderClassifier : IHeaderClassifier
{
    Task<FrozenHeaderExecutionResult> ExecuteFrozenRequestAsync(
        byte[] providerBody, int maxTokens, string systemPrompt, string userMessage,
        CancellationToken cancellationToken = default);
}
