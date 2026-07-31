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
}

public sealed class HeadingRecord
{
    /// <summary>Chỉ số đoạn trong tài liệu gốc.</summary>
    [JsonPropertyName("index")]
    public required int Index { get; init; }

    /// <summary>Cấp tiêu đề 1..9.</summary>
    [JsonPropertyName("level")]
    public required int Level { get; set; }

    /// <summary>Văn bản LẤY TỪ OpenXML (không lấy từ LLM để tránh bịa).</summary>
    [JsonPropertyName("text")]
    public required string Text { get; init; }

    [JsonPropertyName("styleId")]
    public string? StyleId { get; init; }

    [JsonPropertyName("source")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public HeadingSource Source { get; set; }

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    /// <summary>
    /// Hai lượt quét cho kết quả khác nhau ở đoạn này — mô hình không ổn định tại đây.
    /// Đây là những dòng đáng để người/mô hình mạnh hơn xem lại, thay vì đọc lại toàn bộ.
    /// </summary>
    [JsonPropertyName("disputed")]
    public bool Disputed { get; set; }
}

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

    /// <summary>Số đoạn hai lượt quét bất đồng — cần trọng tài xem lại.</summary>
    [JsonPropertyName("disputedCount")]
    public int DisputedCount => Headings.Count(h => h.Disputed);
}
