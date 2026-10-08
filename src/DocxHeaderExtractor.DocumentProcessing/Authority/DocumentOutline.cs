using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.DocumentProcessing.Authority;

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
/// Những gì lượt chạy ĐÃ LÀM, đối lại với capability metadata đã hứa trước khi chạy.
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

    [JsonPropertyName("sourceCount")]
    public int SourceCount { get; init; }

    [JsonPropertyName("headings")]
    public required IReadOnlyList<HeadingRecord> Headings { get; init; }

    [JsonPropertyName("elapsedMs")]
    public long ElapsedMs { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    /// <summary>Đường dựng outline tất định đã dùng, nếu mode đủ rõ để không cần LLM.</summary>
    [JsonPropertyName("deterministicRoute")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeterministicRoute { get; init; }

    /// <summary>
    /// Route-specific evidence summary. This is deliberately separate from document diagnostics so
    /// a bounded PDF/LLM route can disclose its source coverage and grounding losses.
    /// </summary>
    [JsonPropertyName("routeAudit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PipelineExecutionAudit? RouteAudit { get; init; }

    /// <summary>Bản ghi các lượt hỏi mô hình đã chạy thật; null khi chạy <c>--no-llm</c>.</summary>
    [JsonPropertyName("provenance")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OutlineRunProvenance? Provenance { get; set; }

    /// <summary>
    /// M9 authority for a route materialized through <c>CanonicalFinalStructureProjection</c> -
    /// <see cref="HeadingRecord"/> above is a structural COPY of this, not the other way around.
    /// Carried on the outline so a later writeback step acts on the exact same
    /// <c>DocumentProductOutput</c> the pipeline computed, never a reconstruction through
    /// <see cref="HeadingRecord"/>. Internal transport only; never part of the JSON contract.
    /// </summary>
    [JsonIgnore]
    public Projection.DocumentProductOutput? ProductOutput { get; init; }

    /// <summary>
    /// Số đoạn đáng ngờ cần trọng tài xem lại: hai lượt quét bất đồng, hoặc hậu kiểm đánh số
    /// thấy cấp lệch khỏi các mục cùng dạng ký hiệu.
    /// </summary>
    [JsonPropertyName("disputedCount")]
    public int DisputedCount => Headings.Count(h => h.Disputed);
}
