using System.Text.Json.Serialization;

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
}
