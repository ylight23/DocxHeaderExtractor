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
public sealed record PixelBox(
    [property: JsonPropertyName("left")] int Left,
    [property: JsonPropertyName("top")] int Top,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height)
{
    [JsonIgnore] public int Right => Left + Width;
    [JsonIgnore] public int Bottom => Top + Height;

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
public sealed record OcrWord(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("box")] PixelBox Box,
    [property: JsonPropertyName("confidence")] double Confidence);

/// <summary>
/// One recognised line of a page in one view. Text is the words joined by single spaces, exactly as the engine wrote them; TextNfc is set only
/// when Unicode NFC changes it. OverlapsOtherLine is a measurement: another line of the page shares part of its height and its horizontal range,
/// which is what a stamp or a speck above a line looks like and what makes a reader mix two lines.
/// </summary>
public sealed record OcrLine(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("box")] PixelBox Box,
    [property: JsonPropertyName("meanConfidence")] double MeanConfidence,
    [property: JsonPropertyName("minConfidence")] double MinConfidence,
    [property: JsonPropertyName("overlapsOtherLine")] bool OverlapsOtherLine,
    [property: JsonPropertyName("words")] IReadOnlyList<OcrWord> Words)
{
    [JsonPropertyName("textNfc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TextNfc { get; init; }
}

/// <summary>One page of one view: its lines in the engine's reading order, and the hash of the engine's raw output for it.</summary>
public sealed record OcrPage(
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("imageSha256")] string ImageSha256,
    [property: JsonPropertyName("imageWidth")] int ImageWidth,
    [property: JsonPropertyName("imageHeight")] int ImageHeight,
    [property: JsonPropertyName("rawOutputSha256")] string RawOutputSha256,
    [property: JsonPropertyName("lines")] IReadOnlyList<OcrLine> Lines);

/// <summary>How one view was made: the preprocessing of the image, the engine and its parameters. Two views of one page differ only here.</summary>
public sealed record OcrViewSpec(
    [property: JsonPropertyName("viewId")] string ViewId,
    [property: JsonPropertyName("preprocessing")] string Preprocessing,
    [property: JsonPropertyName("scale")] double Scale,
    [property: JsonPropertyName("engine")] string Engine,
    [property: JsonPropertyName("engineVersion")] string EngineVersion,
    [property: JsonPropertyName("languages")] string Languages,
    [property: JsonPropertyName("pageSegmentationMode")] int PageSegmentationMode);

/// <summary>One reading of the whole document: its spec and its pages. Coordinates are those of the original page images whatever the scale.</summary>
public sealed record OcrView(
    [property: JsonPropertyName("spec")] OcrViewSpec Spec,
    [property: JsonPropertyName("origin")] EvidenceOrigin Origin,
    [property: JsonPropertyName("pages")] IReadOnlyList<OcrPage> Pages);

/// <summary>A view that could not be made, and why (a tool missing, a page that failed). Recorded, never silently left out.</summary>
public sealed record OcrViewGap(
    [property: JsonPropertyName("viewId")] string ViewId,
    [property: JsonPropertyName("reason")] string Reason);

/// <summary>
/// The OCR evidence of one scanned document: the source's identity and integrity, every view that was made and every view that could not be.
/// Immutable once written: a later run with the same source and parameters reads it back.
/// </summary>
public sealed record ScanOcrEvidence(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("sourceSha256")] string SourceSha256,
    [property: JsonPropertyName("sourceKind")] string SourceKind,
    [property: JsonPropertyName("pageCount")] int PageCount,
    [property: JsonPropertyName("parametersSha256")] string ParametersSha256,
    [property: JsonPropertyName("views")] IReadOnlyList<OcrView> Views,
    [property: JsonPropertyName("gaps")] IReadOnlyList<OcrViewGap> Gaps)
{
    public const string CurrentVersion = "SCAN_OCR_EVIDENCE_V1";
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
