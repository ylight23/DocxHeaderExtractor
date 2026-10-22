using MessagePack;
using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.DocumentProcessing.Source.Scan;

/// <summary>Where a piece of evidence comes from. A provenance class, not a label of what the text is.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EvidenceOrigin
{
    /// <summary>Text and formatting read directly from the DOCX or PDF.</summary>
    Native,
    /// <summary>Text and boxes recognised from an image by an OCR engine.</summary>
    OcrDerived,
    /// <summary>A normalised form of other text, with a map back to it.</summary>
    NormalizedView,
    /// <summary>A different reading of the same region that no second source has confirmed.</summary>
    CorrectionCandidate,
    /// <summary>A relation between boxes (line, block, column, reading order) inferred from geometry.</summary>
    LayoutInferred,
}

/// <summary>How far a reading is confirmed. There is no "true": two readers of the same image can share an error.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VerificationStatus
{
    Native,
    Unverified,
    /// <summary>Two or more independent readers agree.</summary>
    Corroborated,
    /// <summary>Readers disagree.</summary>
    Conflict,
}

/// <summary>A box in the pixels of the page's original image, origin top left.</summary>
[MessagePackObject]
public sealed record PixelBox(
    [property: JsonPropertyName("left"), Key(0)] int Left,
    [property: JsonPropertyName("top"), Key(1)] int Top,
    [property: JsonPropertyName("width"), Key(2)] int Width,
    [property: JsonPropertyName("height"), Key(3)] int Height)
{
    [JsonIgnore, IgnoreMember] public int Right => Left + Width;
    [JsonIgnore, IgnoreMember] public int Bottom => Top + Height;

    public static PixelBox Union(IEnumerable<PixelBox> boxes)
    {
        var all = boxes.ToArray();
        int l = all.Min(b => b.Left), t = all.Min(b => b.Top), r = all.Max(b => b.Right), bo = all.Max(b => b.Bottom);
        return new PixelBox(l, t, r - l, bo - t);
    }

    /// <summary>Intersection over union of two boxes, 0 when they do not meet.</summary>
    public double IoU(PixelBox other)
    {
        int w = Math.Min(Right, other.Right) - Math.Max(Left, other.Left), h = Math.Min(Bottom, other.Bottom) - Math.Max(Top, other.Top);
        if (w <= 0 || h <= 0) return 0;
        double inter = (double)w * h;
        return inter / ((double)Width * Height + (double)other.Width * other.Height - inter);
    }
}

/// <summary>One recognised word: its text as the engine wrote it, its box and the engine's confidence (0 to 100).</summary>
[MessagePackObject]
public sealed record OcrWord(
    [property: JsonPropertyName("text"), Key(0)] string Text,
    [property: JsonPropertyName("box"), Key(1)] PixelBox Box,
    [property: JsonPropertyName("confidence"), Key(2)] double Confidence);

/// <summary>
/// One recognised line of a page in one view. Text is the words joined by single spaces, exactly as the engine wrote them; TextNfc is set only
/// when Unicode NFC changes it. OverlapsOtherLine is a measurement: another line of the page shares part of its height and its horizontal range,
/// which is what a stamp or a speck above a line looks like and what makes a reader mix two lines.
/// </summary>
[MessagePackObject]
public sealed record OcrLine(
    [property: JsonPropertyName("id"), Key(0)] string Id,
    [property: JsonPropertyName("text"), Key(1)] string Text,
    [property: JsonPropertyName("box"), Key(2)] PixelBox Box,
    [property: JsonPropertyName("meanConfidence"), Key(3)] double MeanConfidence,
    [property: JsonPropertyName("minConfidence"), Key(4)] double MinConfidence,
    [property: JsonPropertyName("overlapsOtherLine"), Key(5)] bool OverlapsOtherLine,
    [property: JsonPropertyName("words"), Key(6)] IReadOnlyList<OcrWord> Words)
{
    [Key(7)]
    [JsonPropertyName("textNfc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TextNfc { get; init; }
}

/// <summary>One page of one view: its lines in the engine's reading order, and the hash of the engine's raw output for it.</summary>
[MessagePackObject]
public sealed record OcrPage(
    [property: JsonPropertyName("page"), Key(0)] int Page,
    [property: JsonPropertyName("imageSha256"), Key(1)] string ImageSha256,
    [property: JsonPropertyName("imageWidth"), Key(2)] int ImageWidth,
    [property: JsonPropertyName("imageHeight"), Key(3)] int ImageHeight,
    [property: JsonPropertyName("rawOutputSha256"), Key(4)] string RawOutputSha256,
    [property: JsonPropertyName("lines"), Key(5)] IReadOnlyList<OcrLine> Lines);

/// <summary>How one view was made: the preprocessing of the image, the engine and its parameters. Two views of one page differ only here.</summary>
[MessagePackObject]
public sealed record OcrViewSpec(
    [property: JsonPropertyName("viewId"), Key(0)] string ViewId,
    [property: JsonPropertyName("preprocessing"), Key(1)] string Preprocessing,
    [property: JsonPropertyName("scale"), Key(2)] double Scale,
    [property: JsonPropertyName("engine"), Key(3)] string Engine,
    [property: JsonPropertyName("engineVersion"), Key(4)] string EngineVersion,
    [property: JsonPropertyName("languages"), Key(5)] string Languages,
    [property: JsonPropertyName("pageSegmentationMode"), Key(6)] int PageSegmentationMode);

/// <summary>One reading of the whole document: its spec and its pages. Coordinates are those of the original page images whatever the scale.</summary>
[MessagePackObject]
public sealed record OcrView(
    [property: JsonPropertyName("spec"), Key(0)] OcrViewSpec Spec,
    [property: JsonPropertyName("origin"), Key(1)] EvidenceOrigin Origin,
    [property: JsonPropertyName("pages"), Key(2)] IReadOnlyList<OcrPage> Pages);

/// <summary>A view that could not be made, and why (a tool missing, a page that failed). Recorded, never silently left out.</summary>
[MessagePackObject]
public sealed record OcrViewGap(
    [property: JsonPropertyName("viewId"), Key(0)] string ViewId,
    [property: JsonPropertyName("reason"), Key(1)] string Reason);

/// <summary>
/// The OCR evidence of one scanned document: the source's identity and integrity, every view that was made and every view that could not be.
/// Immutable once written: a later run with the same source and parameters reads it back.
/// </summary>
[MessagePackObject]
public sealed record ScanOcrEvidence(
    [property: JsonPropertyName("version"), Key(0)] string Version,
    [property: JsonPropertyName("sourceSha256"), Key(1)] string SourceSha256,
    [property: JsonPropertyName("sourceKind"), Key(2)] string SourceKind,
    [property: JsonPropertyName("pageCount"), Key(3)] int PageCount,
    [property: JsonPropertyName("parametersSha256"), Key(4)] string ParametersSha256,
    [property: JsonPropertyName("views"), Key(5)] IReadOnlyList<OcrView> Views,
    [property: JsonPropertyName("gaps"), Key(6)] IReadOnlyList<OcrViewGap> Gaps)
{
    public const string CurrentVersion = "SCAN_OCR_EVIDENCE_V1";

    /// <summary>For PDF, the renderer/version/DPI of the immutable page rasters. Null on legacy DOCX evidence.</summary>
    [Key(7)]
    [JsonPropertyName("pageImageBasis")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PageImageBasis { get; init; }
}

/// <summary>Where two views read the same line differently: the words of each view between the parts they share. Never resolved here.</summary>
public sealed record TextDifference(
    [property: JsonPropertyName("original")] string Original,
    [property: JsonPropertyName("alternative")] string Alternative,
    [property: JsonPropertyName("verificationStatus")] VerificationStatus Status);

/// <summary>A line of one view and the line of another view that covers the same region, with the differences between their words.</summary>
public sealed record AlignedLine(
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("lineA")] string LineA,
    [property: JsonPropertyName("lineB")] string? LineB,
    [property: JsonPropertyName("iou")] double IoU,
    [property: JsonPropertyName("identical")] bool Identical,
    [property: JsonPropertyName("differences")] IReadOnlyList<TextDifference> Differences);
