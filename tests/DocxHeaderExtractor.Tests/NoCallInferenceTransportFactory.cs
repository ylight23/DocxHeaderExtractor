using DocxHeaderExtractor.DocumentProcessing.Inference;

namespace DocxHeaderExtractor.Tests;

/// <summary>Deterministic tests must never construct or call a provider, including PDF.</summary>
internal sealed class NoCallInferenceTransportFactory : IInferenceTransportFactory
{
    public Task<IInferenceTransport> CreateAsync(CancellationToken ct = default) =>
        throw new InvalidOperationException("Unexpected inference factory call in a no-LLM test.");

    public Task<IInferenceTransport> CreatePdfProductionAsync(CancellationToken ct = default) =>
        throw new InvalidOperationException("Unexpected PDF inference factory call in a no-LLM test.");
}
