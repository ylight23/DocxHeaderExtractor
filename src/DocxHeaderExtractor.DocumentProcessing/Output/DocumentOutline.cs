using System.Text.Json.Serialization;
using DocxHeaderExtractor.DocumentProcessing.Audit;
using DocxHeaderExtractor.DocumentProcessing.Output.Product;

namespace DocxHeaderExtractor.DocumentProcessing.Output;

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
    public RunAudit? RouteAudit { get; init; }

    /// <summary>Bản ghi các lượt hỏi mô hình đã chạy thật; null khi chạy <c>--no-llm</c>.</summary>
    [JsonPropertyName("provenance")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OutlineRunProvenance? Provenance { get; set; }

    /// <summary>
    /// M9 authority for a route materialized through <c>FinalProjection</c> -
    /// <see cref="HeadingRecord"/> above is a structural COPY of this, not the other way around.
    /// Carried on the outline so a later writeback step acts on the exact same
    /// <c>DocumentProductOutput</c> the pipeline computed, never a reconstruction through
    /// <see cref="HeadingRecord"/>. Internal transport only; never part of the JSON contract.
    /// </summary>
    [JsonIgnore]
    public DocumentProductOutput? ProductOutput { get; init; }

    /// <summary>
    /// Số đoạn đáng ngờ cần trọng tài xem lại: hai lượt quét bất đồng, hoặc hậu kiểm đánh số
    /// thấy cấp lệch khỏi các mục cùng dạng ký hiệu.
    /// </summary>
    [JsonPropertyName("disputedCount")]
    public int DisputedCount => Headings.Count(h => h.Disputed);
}
