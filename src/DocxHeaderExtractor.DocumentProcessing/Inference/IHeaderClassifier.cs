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

    /// <summary>
    /// Creates the separately-authorized production transport for the PDF authority route.
    /// DOCX continues to use <see cref="CreateAsync"/> and may use any configured provider.
    /// A factory that has not explicitly opted into the qualified PDF route fails closed rather
    /// than silently falling back to its default (usually local) classifier.
    /// </summary>
    Task<IHeaderClassifier> CreatePdfProductionAsync(PipelineOptions options, CancellationToken ct = default) =>
        throw new InvalidOperationException("PDF_PRODUCTION_PROVIDER_FACTORY_REQUIRED");
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

/// <summary>
/// Marker issued only by a composition root that has authorized the qualified PDF route. It is
/// deliberately distinct from experimental qualification transport: ordinary production must
/// not borrow an experiment manifest merely to use its approved provider transport.
/// </summary>
public interface IPdfProductionAuthorizedFrozenRequestClassifier : IFrozenRequestHeaderClassifier
{
    string PdfProductionProvider { get; }
    string PdfProductionModel { get; }
}
