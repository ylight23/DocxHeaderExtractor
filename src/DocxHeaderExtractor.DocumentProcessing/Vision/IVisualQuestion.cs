namespace DocxHeaderExtractor.DocumentProcessing.Vision;

/// <summary>Single-image visual question contract shared by local and OpenAI-compatible VLMs.</summary>
public interface IVisualQuestion : IDisposable
{
    Task<string> AskAsync(byte[] imageBytes, string question, int maxTokens = 300, CancellationToken ct = default);
}

/// <summary>
/// Optional OpenAI-compatible capability for one question over several independent image crops.
/// The order of returned decisions must be grounded by the caller; images never create candidates.
/// </summary>
public interface IMultiImageVisualQuestion : IVisualQuestion
{
    /// <summary>Gateway-advertised image limit for one request; one preserves single-crop review.</summary>
    int MaximumImagesPerRequest { get; }

    Task<string> AskManyAsync(
        IReadOnlyList<byte[]> imageBytes,
        string question,
        int maxTokens = 300,
        CancellationToken ct = default);
}
