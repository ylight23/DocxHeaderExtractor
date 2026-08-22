using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.OpenXmlLayer;

namespace DocxHeaderExtractor.Core.Models;

/// <summary>Nguồn gốc của một heading trong kết quả cuối cùng.</summary>
public enum HeadingSource
{
    /// <summary>Style Word khẳng định (không cần LLM).</summary>
    Style,

    /// <summary>Do mô hình LLM xác nhận từ tập ứng viên.</summary>
    Model,

    /// <summary>Heuristic giữ lại khi chạy chế độ --no-llm.</summary>
    Heuristic,

    /// <summary>
    /// Mô hình đã loại, nhưng đánh số của tài liệu khẳng định nó là em kế tiếp của một heading
    /// đã nhận (3.1 → 3.2). Luôn kèm <see cref="HeadingRecord.Disputed"/> — đây là suy luận cấu
    /// trúc, không phải khẳng định.
    /// </summary>
    Structure,

    /// <summary>Người dùng đã sửa đúng paragraph của đúng tài liệu; áp dụng cục bộ sau suy luận.</summary>
    HumanCorrection,
}

public enum HeadingDecisionStatus
{
    RequiresReview,
    AutoAcceptedEvidence,
    AutoAcceptedCalibrated,
    HumanVerified,
}

/// <summary>Document-level result of the common evidence-first workflow.</summary>
public enum OutlineDisposition
{
    Accepted,
    RequiresReview,
    Abstained,
}

public sealed record OutlineOutcome(
    [property: JsonPropertyName("disposition")]
    [property: JsonConverter(typeof(JsonStringEnumConverter))] OutlineDisposition Disposition,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("evidenceRoute")] string? EvidenceRoute);

public sealed class HeadingRecord
{
    /// <summary>Chỉ số đoạn trong tài liệu gốc.</summary>
    [JsonPropertyName("index")]
    public required int Index { get; init; }

    [JsonPropertyName("stableId")]
    public string? StableId { get; init; }

    /// <summary>Cấp tiêu đề 1..9.</summary>
    [JsonPropertyName("level")]
    public required int Level { get; set; }

    /// <summary>Văn bản LẤY TỪ OpenXML (không lấy từ LLM để tránh bịa).</summary>
    [JsonPropertyName("text")]
    public required string Text { get; set; }

    /// <summary>Chỉ có khi một paragraph chứa cả heading và nội dung cùng dòng.</summary>
    [JsonPropertyName("originalText")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OriginalText { get; set; }

    [JsonPropertyName("headingSpan")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TextOffsetSpan? HeadingSpan { get; set; }

    [JsonPropertyName("inlineBody")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InlineBody { get; set; }

    [JsonPropertyName("inlineBodySpan")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TextOffsetSpan? InlineBodySpan { get; set; }

    [JsonPropertyName("boundarySource")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BoundarySource { get; set; }

    [JsonPropertyName("styleId")]
    public string? StyleId { get; init; }

    [JsonPropertyName("source")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public HeadingSource Source { get; set; }

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    /// <summary>Model đã xác nhận lại một heading do Structure phục hồi.</summary>
    [JsonPropertyName("modelConfirmed")]
    public bool ModelConfirmed { get; set; }

    /// <summary>Lượt phản biện độc lập vẫn xác nhận heading model-only có evidence yếu.</summary>
    [JsonPropertyName("criticConfirmed")]
    public bool CriticConfirmed { get; set; }

    [JsonPropertyName("decisionStatus")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public HeadingDecisionStatus DecisionStatus { get; set; } = HeadingDecisionStatus.RequiresReview;

    [JsonPropertyName("confidenceBasis")]
    public string ConfidenceBasis { get; set; } = "evidence_not_calibrated";

    [JsonPropertyName("acceptanceSignature")]
    public string? AcceptanceSignature { get; set; }

    [JsonPropertyName("calibrationSamples")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int CalibrationSamples { get; set; }

    [JsonPropertyName("evidence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HeadingEvidence? Evidence { get; set; }

    /// <summary>
    /// Đoạn đáng ngờ: hai lượt quét cho kết quả khác nhau (mô hình không ổn định tại đây), hoặc
    /// hậu kiểm đánh số thấy cấp lệch khỏi các mục cùng dạng ký hiệu.
    /// Đây là những dòng đáng để người/mô hình mạnh hơn xem lại, thay vì đọc lại toàn bộ.
    /// </summary>
    [JsonPropertyName("disputed")]
    public bool Disputed { get; set; }
}

public sealed record HeadingEvidence(
    [property: JsonPropertyName("numberingValid")] bool NumberingValid,
    [property: JsonPropertyName("siblingSequenceValid")] bool SiblingSequenceValid,
    [property: JsonPropertyName("formattingConsistent")] bool FormattingConsistent,
    [property: JsonPropertyName("modelConfirmed")] bool ModelConfirmed,
    [property: JsonPropertyName("treeValid")] bool TreeValid,
    [property: JsonPropertyName("status")] string Status);

public sealed record TextOffsetSpan(
    [property: JsonPropertyName("start")] int Start,
    [property: JsonPropertyName("end")] int End);

/// <summary>Một lượt hỏi mô hình đã thực sự chạy trong lượt trích xuất này.</summary>
public sealed record OutlinePass(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("chunks")] int Chunks,
    [property: JsonPropertyName("requestedParagraphs")] int RequestedParagraphs,
    [property: JsonPropertyName("sentDataExternally")] bool SentDataExternally);

/// <summary>
/// Những gì lượt chạy ĐÃ LÀM, đối lại với những gì <c>AgentToolDescriptor</c> hứa trước khi chạy.
/// <para>
/// Lý do tồn tại: harness nhìn cả pipeline là MỘT tool và chốt <c>SendsDataExternally</c> đúng một
/// lần lúc dựng tool, trong khi bên trong có tới năm lượt hỏi mô hình, mỗi lượt gửi một tập nội
/// dung khác nhau. Không có bản ghi này thì lời hứa "run chỉ xử lý cục bộ" không kiểm lại được —
/// nó chỉ là một cờ do code khác tính, không phải một quan sát.
/// </para>
/// </summary>
public sealed record OutlineRunProvenance(
    [property: JsonPropertyName("backend")] string Backend,
    [property: JsonPropertyName("sentDataExternally")] bool SentDataExternally,
    [property: JsonPropertyName("passes")] IReadOnlyList<OutlinePass> Passes);

public sealed class DocumentOutline
{
    [JsonPropertyName("file")]
    public required string File { get; init; }

    [JsonPropertyName("paragraphCount")]
    public int ParagraphCount { get; init; }

    [JsonPropertyName("candidateCount")]
    public int CandidateCount { get; init; }

    [JsonPropertyName("headings")]
    public required IReadOnlyList<HeadingRecord> Headings { get; init; }

    [JsonPropertyName("elapsedMs")]
    public long ElapsedMs { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    /// <summary>Mode tài liệu đo từ chính OpenXML/text, dùng để giải thích đường deterministic đã chọn.</summary>
    [JsonPropertyName("documentMode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DocumentModeReport? DocumentMode { get; init; }

    /// <summary>Đường dựng outline tất định đã dùng, nếu mode đủ rõ để không cần LLM.</summary>
    [JsonPropertyName("deterministicRoute")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeterministicRoute { get; init; }

    /// <summary>
    /// Route-specific evidence summary. This is deliberately separate from document diagnostics so
    /// a bounded PDF/LLM route can disclose its candidate coverage and grounding losses.
    /// </summary>
    [JsonPropertyName("routeAudit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RouteExecutionAudit? RouteAudit { get; init; }

    /// <summary>
    /// Audit tự động của tầng code-first: đo tín hiệu tài liệu, chạy candidate deterministic trong
    /// sandbox và ghi lý do pass/fail. LLM chỉ nên phân tích report này, không tự chọn output.
    /// </summary>
    [JsonPropertyName("diagnostics")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DocumentDiagnosticReport? Diagnostics { get; init; }

    /// <summary>Bản ghi các lượt hỏi mô hình đã chạy thật; null khi chạy <c>--no-llm</c>.</summary>
    [JsonPropertyName("provenance")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OutlineRunProvenance? Provenance { get; set; }

    /// <summary>
    /// Số đoạn đáng ngờ cần trọng tài xem lại: hai lượt quét bất đồng, hoặc hậu kiểm đánh số
    /// thấy cấp lệch khỏi các mục cùng dạng ký hiệu.
    /// </summary>
    [JsonPropertyName("disputedCount")]
    public int DisputedCount => Headings.Count(h => h.Disputed);

    [JsonPropertyName("autoAcceptedCount")]
    public int AutoAcceptedCount => Headings.Count(h => h.DecisionStatus is
        HeadingDecisionStatus.AutoAcceptedEvidence or HeadingDecisionStatus.AutoAcceptedCalibrated or
        HeadingDecisionStatus.HumanVerified);

    /// <summary>
    /// Tách lý do auto-accept để không đọc nhầm route deterministic declared thành bucket precision
    /// đã được holdout chứng minh.
    /// </summary>
    [JsonPropertyName("decisionAudit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PrecisionDecisionAudit? DecisionAudit { get; init; }

    /// <summary>Terminal disposition; a non-empty heading list alone is never a promotion signal.</summary>
    [JsonPropertyName("outcome")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OutlineOutcome? Outcome { get; init; }
}

public sealed record PrecisionDecisionAudit(
    [property: JsonPropertyName("autoAcceptedTotal")] int AutoAcceptedTotal,
    [property: JsonPropertyName("autoAcceptedCalibrated")] int AutoAcceptedCalibrated,
    [property: JsonPropertyName("autoAcceptedDeterministic")] int AutoAcceptedDeterministic,
    [property: JsonPropertyName("autoAcceptedUncalibratedEvidence")] int AutoAcceptedUncalibratedEvidence,
    [property: JsonPropertyName("humanVerified")] int HumanVerified,
    [property: JsonPropertyName("requiresReview")] int RequiresReview,
    [property: JsonPropertyName("byConfidenceBasis")] IReadOnlyDictionary<string, int> ByConfidenceBasis);

public sealed record DocumentDiagnosticReport(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("style")] StyleSignalDiagnostic Style,
    [property: JsonPropertyName("layout")] LayoutSignalDiagnostic Layout,
    [property: JsonPropertyName("candidates")] IReadOnlyList<OutlineCandidateDiagnostic> Candidates);

public sealed record StyleSignalDiagnostic(
    [property: JsonPropertyName("styledCount")] int StyledCount,
    [property: JsonPropertyName("suspectRatio")] double SuspectRatio,
    [property: JsonPropertyName("density")] double Density,
    [property: JsonPropertyName("distinctLevels")] int DistinctLevels,
    [property: JsonPropertyName("numberedDisagreeRatio")] double NumberedDisagreeRatio,
    [property: JsonPropertyName("selectionTrusted")] bool SelectionTrusted,
    [property: JsonPropertyName("levelTrusted")] bool LevelTrusted,
    [property: JsonPropertyName("mixed")] bool Mixed);

public sealed record LayoutSignalDiagnostic(
    [property: JsonPropertyName("mergedParagraphs")] int MergedParagraphs,
    [property: JsonPropertyName("mergedMarkers")] int MergedMarkers,
    [property: JsonPropertyName("tableOfContentsParagraphs")] int TableOfContentsParagraphs,
    [property: JsonPropertyName("typedNumberSegments")] int TypedNumberSegments);

public sealed record OutlineCandidateDiagnostic(
    [property: JsonPropertyName("route")] string Route,
    [property: JsonPropertyName("accepted")] bool Accepted,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("headingCount")] int HeadingCount,
    [property: JsonPropertyName("duplicateRate")] double DuplicateRate,
    [property: JsonPropertyName("titlePollutionRate")] double TitlePollutionRate,
    [property: JsonPropertyName("levelJumpRate")] double LevelJumpRate,
    [property: JsonPropertyName("bodyAnchorRatio")] double? BodyAnchorRatio = null,
    [property: JsonPropertyName("tocCoverage")] double? TocCoverage = null);
