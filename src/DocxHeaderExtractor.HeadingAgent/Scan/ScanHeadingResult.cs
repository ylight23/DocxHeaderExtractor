using DocxHeaderExtractor.Contracts.Routing;
using DocxHeaderExtractor.Contracts.Scan;

namespace DocxHeaderExtractor.HeadingAgent.Scan;

/// <summary>One line of a heading of a scan: where it stands (page, OCR line, box in the page image's pixels), the reading the agent took
/// (view and origin), the heading text in it with the extent, and the primary view's reading of the same line, kept beside it.</summary>
public sealed record ScanHeadingPart(string Alias, int Page, string LineId, PixelBox Box, string ViewId, string ReadingOrigin, string Text,
    int ExtentStart, int ExtentEnd, string PrimaryReading, IReadOnlyList<string> ReadingsThatAgree, string Verification);

/// <summary>
/// A heading of a scanned document, anchored to page images and OCR evidence, never to an OOXML paragraph that does not exist. TextStatus says
/// which reading the text comes from (VLM_TRANSCRIBED for the vision model's blind transcription, OCR_PRIMARY_VIEW, OCR_OTHER_VIEW, or
/// CORRECTED_UNVERIFIED for a vision proposal); Verification is the weakest of its parts: CORROBORATED when two readers read the same (an OCR
/// view and the transcription, or an OCR view and the vision model's choice), CONFLICT when the vision model chose another reading than the one
/// taken, UNVERIFIED otherwise. The agent decided that it is a heading, where it ends and which reading to take.
/// </summary>
public sealed record ScanHeading(int Index, IReadOnlyList<ScanHeadingPart> Parts, string Text, string PrimaryReading, string TextStatus, string Verification);

/// <summary>The headings of a scan with the audit of the run: units, failures, the model's answers, calls and cost, the vision pages, and the
/// OCR views that could not be made. Hierarchy (levels and parents) is not part of this result yet.</summary>
public sealed record ScanHeadingResult(string SourceSha256, string SelectorId, IReadOnlyList<ScanHeading> Headings, int UnitsComplete, int UnitsTotal,
    IReadOnlyList<string> UnitFailures, IReadOnlyList<string> ModelAnswers, int ProviderCalls, double ProviderCostUsd,
    int VisionPagesObserved, IReadOnlyList<string> VisionFailures, IReadOnlyList<OcrViewGap> OcrGaps, int PartsRejected)
{
    public GroupingObservation? Grouping { get; init; }
    public ToolUseStats? ToolUse { get; init; }
    /// <summary>Lines transcribed by the vision model, and how many passed the checks and are shown.</summary>
    public int TranscribedLines { get; init; }
    public int TranscriptionsAccepted { get; init; }
    public IReadOnlyDictionary<string, int> TranscriptionStatuses { get; init; } = new Dictionary<string, int>();
    public IReadOnlyList<string> TranscriptionFailures { get; init; } = [];
    /// <summary>Every transcription the checks refused, with the OCR reading it was checked against: the program never shows these.</summary>
    public IReadOnlyList<TranscriptionRejection> TranscriptionRejections { get; init; } = [];

    /// <summary>The vision observation and transcription calls sent before the conversation, and their cost; not in ProviderCalls.</summary>
    public int PreflightCalls { get; init; }
    public double PreflightCostUsd { get; init; }

    /// <summary>Wall-clock seconds of each stage: ocr, vision and transcription (which run side by side, so their sum is not the time spent),
    /// preflight (ocr to the end of both), conversation. A stage read back from the store takes almost none.</summary>
    public IReadOnlyDictionary<string, double> StageSeconds { get; init; } = new Dictionary<string, double>();

    /// <summary>The provider calls of each stage (vision, transcription, conversation): count, median, 90th percentile, slowest, sum.</summary>
    public IReadOnlyDictionary<string, CallStats> CallSeconds { get; init; } = new Dictionary<string, CallStats>();
    /// <summary>Seconds the transcription spent making its crops, before any call.</summary>
    public double TranscriptionCropSeconds { get; init; }
    /// <summary>The conversation's units whose calls took longest in all (a unit's calls run one after another).</summary>
    public IReadOnlyList<UnitTime> SlowestUnits { get; init; } = [];
}

/// <summary>A transcription the checks refused: the line, why, what the vision model wrote and the primary OCR reading of the same box.</summary>
public sealed record TranscriptionRejection(string Alias, string LineId, string Status, string? Transcription, string OcrReading);
