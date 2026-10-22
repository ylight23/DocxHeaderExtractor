namespace DocxHeaderExtractor.Contracts.Models;

public sealed record ScanLineAnchor(DocxHeaderExtractor.Contracts.Scan.PixelBox Box, int ImageWidth, int ImageHeight,
    string ReadingViewId, string PrimaryReading, IReadOnlyDictionary<string, string> Readings, string? TranscriptionStatus, string? TranscriptionText = null)
{
    public string ReadingOrigin => ReadingViewId switch { "vlm-transcribed" => "VLM_TRANSCRIBED", "vlm-proposed" => "CORRECTION_CANDIDATE", _ => "OCR_DERIVED" };
}
