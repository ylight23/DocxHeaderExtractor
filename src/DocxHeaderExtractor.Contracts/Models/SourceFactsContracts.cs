namespace DocxHeaderExtractor.Contracts.Models;

/// <summary>
/// Immutable parser/render facts for one source occurrence. Model calls may read these facts but must not
/// create or alter them; downstream output is built only after a separate validation pass.
/// </summary>
public sealed record SourceFacts
{
    public required string SourceId { get; init; }
    public required string RawText { get; init; }
    public required SourceAnchor Source { get; init; }
    public required SourceTextSpan RawSpan { get; init; }

    /// <summary>
    /// Parser-owned UTF-16 boundaries that a proposal may point to. An empty value means the
    /// deterministic boundary map derived from <see cref="RawText"/> is used.
    /// </summary>
    public IReadOnlyList<int> ParserBoundaries { get; init; } = [];
}

public sealed record SourceTextSpan(int Start, int End);

/// <summary>Stable source/writeback coordinates. PDF render coordinates are optional for DOCX-only input.</summary>
public sealed record SourceAnchor
{
    public required string SourceType { get; init; }
    public string? ParagraphId { get; init; }
    public int? ParagraphIndex { get; init; }
    public IReadOnlyList<SourceSegment> SourceSegments { get; init; } = [];
    public int? Page { get; init; }
    public string? RenderBlockId { get; init; }
    public IReadOnlyList<string> RenderLineIds { get; init; } = [];
    public PdfBoundingBox? BoundingBox { get; init; }
    /// <summary>Scan coordinates and independent readings. These are pixels from the top left, not PDF points.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ScanLineAnchor? Scan { get; init; }
}

public sealed record PdfBoundingBox(double Left, double Bottom, double Right, double Top);

public sealed record ScanLineAnchor(DocxHeaderExtractor.Contracts.Scan.PixelBox Box, int ImageWidth, int ImageHeight,
    string ReadingViewId, string PrimaryReading, IReadOnlyDictionary<string, string> Readings, string? TranscriptionStatus, string? TranscriptionText = null)
{
    public string ReadingOrigin => ReadingViewId switch { "vlm-transcribed" => "VLM_TRANSCRIBED", "vlm-proposed" => "CORRECTION_CANDIDATE", _ => "OCR_DERIVED" };
}
