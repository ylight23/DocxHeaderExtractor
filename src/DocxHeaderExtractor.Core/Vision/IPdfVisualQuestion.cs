namespace DocxHeaderExtractor.Core.Vision;

/// <summary>Minimal image-question contract used by PDF visual evidence stages.</summary>
public interface IPdfVisualQuestion : IDisposable
{
    Task<string> AskAsync(byte[] imageBytes, string question, int maxTokens = 300, CancellationToken ct = default);
}
