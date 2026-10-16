using DocxHeaderExtractor.DocumentProcessing.Source.Scan;

namespace DocxHeaderExtractor.DocumentProcessing.Routing;

/// <summary>Prepares one immutable OCR/VLM line universe and its heading decisions for the shared pipeline.
/// A null key requests local OCR only: no transcription, selection or provider calls.</summary>
public interface IScanHeadingSelector
{
    string SelectorId { get; }
    Task<ScanDocumentSelection> PrepareAsync(string path, string? apiKey, CancellationToken ct);
}

public sealed record ScanDocumentSelection(string SourceSha256, IReadOnlyList<ScanSourceLine> Lines,
    HeadingSelection Selection, ScanRunDiagnostics Diagnostics);

/// <summary>The complete reading used by the binder, beside its original OCR and image coordinates.</summary>
public sealed record ScanSourceLine(string Alias, string LineId, int Page, PixelBox Box, int ImageWidth, int ImageHeight,
    string Text, string PrimaryReading, string ReadingViewId, IReadOnlyDictionary<string, string> Readings,
    string? TranscriptionStatus, string FactsJson, string? TranscriptionText = null);

public sealed record ScanRunDiagnostics(int PreflightCalls, double PreflightCostUsd, int ConversationCalls, double ConversationCostUsd,
    int TranscribedLines, int TranscriptionsAccepted, IReadOnlyDictionary<string, int> TranscriptionStatuses,
    IReadOnlyList<string> TranscriptionFailures, IReadOnlyList<string> VisionFailures, IReadOnlyList<OcrViewGap> OcrGaps,
    IReadOnlyDictionary<string, double> StageSeconds)
{
    /// <summary>Bounded shared placement attempts. Its transport does not expose billing here; the other costs exclude placement.</summary>
    public int PlacementCalls { get; init; }
}
