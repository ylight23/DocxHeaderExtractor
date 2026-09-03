namespace DocxHeaderExtractor.Core.Vision;

/// <summary>Minimal image-question contract used by PDF visual evidence stages.</summary>
public interface IPdfVisualQuestion : IDisposable
{
    Task<string> AskAsync(byte[] imageBytes, string question, int maxTokens = 300, CancellationToken ct = default);
}

public interface IPdfVisualAttemptAuditable
{
    IReadOnlyList<PdfVisualAttemptOutcome> LastAttemptOutcomes { get; }
}

public sealed record PdfVisualAttemptOutcome(int Attempt, string Status, int? HttpStatus, long ElapsedMs, string? Error);
