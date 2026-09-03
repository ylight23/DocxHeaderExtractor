namespace DocxHeaderExtractor.Infrastructure.AI;

public enum ProviderTransport
{
    Local,
    Http,
}

public sealed record ModelProviderDescriptor(
    string ProviderId,
    string ModelId,
    ProviderTransport Transport,
    bool SendsDataExternally,
    int? ContextTokens,
    bool SupportsStructuredOutput,
    bool SupportsVision);

public sealed record ChatMessage(string Role, string Content);

public sealed record ProviderRequest(
    IReadOnlyList<ChatMessage> Messages,
    int MaxOutputTokens,
    string? ResponseFormat = null);

public sealed record ProviderResponse(
    string Content,
    int? InputTokens,
    int? OutputTokens,
    string? ProviderRequestId);

/// <summary>
/// Infrastructure port for provider adapters. Concrete OpenRouter/LM Studio/SGLang/LLama
/// implementations belong here; Application and Core consume only a port and metadata.
/// </summary>
public interface IChatModelProvider : IDisposable
{
    ModelProviderDescriptor Descriptor { get; }

    Task<ProviderResponse> CompleteAsync(
        ProviderRequest request,
        CancellationToken cancellationToken = default);
}
