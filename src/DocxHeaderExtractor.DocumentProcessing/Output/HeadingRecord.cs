using System.Text.Json.Serialization;
using DocxHeaderExtractor.Contracts.Models;

namespace DocxHeaderExtractor.DocumentProcessing.Output;

/// <summary>Nguồn gốc của một heading trong kết quả cuối cùng.</summary>
public enum HeadingSource
{
    /// <summary>Claim của mô hình đã bind vào nguồn và qua validator.</summary>
    Model,

    /// <summary>Người dùng đã sửa đúng paragraph của đúng tài liệu; áp dụng cục bộ sau suy luận.</summary>
    HumanCorrection,
}

public enum HeadingDecisionStatus
{
    RequiresReview,
    HumanVerified,
}

/// <summary>Document-level result of the common evidence-first workflow.</summary>
public sealed class HeadingRecord
{
    /// <summary>Same-execution structural identity; not part of the compatibility wire contract.</summary>
    [JsonIgnore]
    public string? StructuralElementId { get; init; }

    /// <summary>Ordered, validated source selections with parser-owned text. Never model-authored.</summary>
    [JsonIgnore]
    public IReadOnlyList<HeadingSourcePart>? SourceParts { get; init; }

    [JsonIgnore]
    public int ValidatedSourcePartCount { get; init; }

    [JsonPropertyName("scanSources")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ScanHeadingSourcePart>? ScanSources { get; init; }

    /// <summary>Chỉ số đoạn trong tài liệu gốc.</summary>
    [JsonPropertyName("index")]
    public required int Index { get; init; }

    [JsonPropertyName("stableId")]
    public string? StableId { get; init; }

    /// <summary>Immutable PDF/DOCX source-fact identity when the authority route produced this heading.</summary>
    [JsonPropertyName("sourceId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceId { get; init; }

    /// <summary>
    /// Cấp tiêu đề 1..9, hoặc null khi route tạo ra heading này không đủ bằng chứng để khẳng định
    /// cấp (M9 hierarchy authority: <c>CanonicalFinalHeading.Level</c> abstain thay vì đoán). `required`
    /// buộc caller phải khai báo rõ trạng thái, kể cả khi trạng thái đó là "chưa biết" — null không
    /// phải giá trị bị bỏ quên.
    /// </summary>
    [JsonPropertyName("level")]
    public required int? Level { get; set; }

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

    /// <summary>
    /// Why <see cref="Level"/> is what it is. Distinguishes a heading the model placed outside the
    /// section tree (a title, a running header) from one it could not place at all: both carry a
    /// null level, but only the second needs a human.
    /// </summary>
    [JsonPropertyName("hierarchyResolution")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? HierarchyResolution { get; set; }

    [JsonPropertyName("styleId")]
    public string? StyleId { get; init; }

    [JsonPropertyName("source")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public HeadingSource Source { get; set; }

    [JsonPropertyName("decisionStatus")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public HeadingDecisionStatus DecisionStatus { get; set; } = HeadingDecisionStatus.RequiresReview;

    [JsonPropertyName("confidenceBasis")]
    public string ConfidenceBasis { get; set; } = "evidence_not_calibrated";

    /// <summary>
    /// Đoạn đáng ngờ: hai lượt quét cho kết quả khác nhau (mô hình không ổn định tại đây), hoặc
    /// hậu kiểm đánh số thấy cấp lệch khỏi các mục cùng dạng ký hiệu.
    /// Đây là những dòng đáng để người/mô hình mạnh hơn xem lại, thay vì đọc lại toàn bộ.
    /// </summary>
    [JsonPropertyName("disputed")]
    public bool Disputed { get; set; }
}

/// <summary>Runtime grounding evidence for one exact source selection, keyed by identity, not ordinal.</summary>
public sealed record HeadingSourcePart(string SourceId, int SourceOrdinal, TextOffsetSpan Span, string OriginalText);

public sealed record ScanHeadingSourcePart(string SourceId, int Page, TextOffsetSpan Span, DocxHeaderExtractor.Contracts.Models.ScanLineAnchor Evidence);
