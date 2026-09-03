using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

internal sealed record BoundaryCutPrompt(string SystemPrompt, string UserPrefix, string LabelWord);

/// <summary>
/// Tầng cắt ranh giới title/body bằng LLM few-shot CỐ ĐỊNH theo domain — bảng cứng đã đo 85,7% /
/// 95,0% / 85,7% trên ba domain (pháp quy VN, RFC, biên bản họp không marker) và THẮNG retrieval
/// động khi so đầu đối đầu (xem <c>docs/llm-boundary-few-shot-retrieval.md</c> §3/§4 và
/// <c>handoff.md</c> mục "thí nghiệm retrieval ... KHÔNG thắng bảng cứng"). Ba prompt dưới đây
/// chuyển NGUYÊN VĂN từ ba harness đã đo (<c>.verify-build/llm-boundary-test*/harness/Program.cs</c>)
/// — không diễn giải lại, vì chính prompt đó đã được đo, không phải một bản "tương đương".
/// <para>
/// Chỉ áp dụng cho ĐÚNG ba domain đã đo. Domain khác trả <see langword="null"/> — không suy diễn
/// số cho domain chưa đo, đúng kỷ luật đo-trước-khi-xây của dự án.
/// </para>
/// </summary>
public static class LlmBoundaryCutter
{
    private const string LegalSystem =
        "Bạn nhận một đoạn văn bản pháp quy tiếng Việt. Đoạn văn bắt đầu bằng một TIÊU ĐỀ MỤC " +
        "(dạng \"Điều N. ...\") rồi nối liền ngay vào nội dung điều khoản, không có dấu ngắt dòng. " +
        "Nhiệm vụ DUY NHẤT: trả về CHÍNH XÁC phần TIÊU ĐỀ — dừng lại đúng ngay trước khi câu văn " +
        "nội dung bắt đầu. Giữ nguyên số hiệu \"Điều N.\" ở đầu. Không thêm chữ nào ngoài tiêu đề, " +
        "không giải thích, không markdown, không dấu ngoặc kép.\n\n" +
        "Ví dụ 1 (ngắn, không có dấu hai chấm):\n" +
        "Văn bản:\nĐiều 44. Hiệu lực thi hành Luật này có hiệu lực thi hành từ ngày 01 tháng 01 năm 2019.\n" +
        "Tiêu đề: Điều 44. Hiệu lực thi hành\n\n" +
        "Ví dụ 2 (dài, có dấu hai chấm):\n" +
        "Văn bản:\nĐiều 15. Bảo vệ hệ thống thông tin quan trọng về an ninh quốc gia trong lĩnh vực tài chính, ngân hàng, năng lượng, giao thông vận tải: Chính phủ quy định chi tiết về danh mục, tiêu chí xác định và biện pháp bảo vệ.\n" +
        "Tiêu đề: Điều 15. Bảo vệ hệ thống thông tin quan trọng về an ninh quốc gia trong lĩnh vực tài chính, ngân hàng, năng lượng, giao thông vận tải";

    private const string RfcSystem =
        "You receive a text block from an English technical specification (RFC-style). The block " +
        "starts with a SECTION HEADING (a numeric marker like \"5.2.1.\" or \"3.\" followed by a " +
        "title) and then runs directly into the body paragraph, with no line break. " +
        "Your ONLY task: return EXACTLY the heading portion — stop right before the body sentence " +
        "begins. Keep the numeric marker at the start. Do not add any word beyond the heading, no " +
        "explanation, no markdown, no quotes.\n\n" +
        "Example 1:\n" +
        "Text:\n3.2. Updating Stored Header Fields Caches are required to update a stored response's header fields from another response in several situations.\n" +
        "Heading: 3.2. Updating Stored Header Fields\n\n" +
        "Example 2:\n" +
        "Text:\n2. Overview This section defines terminology used throughout the document.\n" +
        "Heading: 2. Overview";

    private const string MinutesSystem =
        "You receive a text block from the minutes of a formal committee meeting. The block starts " +
        "with a SECTION LABEL (a short topic phrase, sometimes ending in a colon, sometimes with no " +
        "punctuation at all) and runs directly into the body sentence, with no line break and often " +
        "no punctuation marking the boundary. Your ONLY task: return EXACTLY the label portion — stop " +
        "right before the body sentence begins. Do not add any word beyond the label, no explanation, " +
        "no markdown, no quotes.\n\n" +
        "Example 1 (ends with a colon):\n" +
        "Text:\nClosing: The meeting was adjourned at 5:30 pm by the Chair.\n" +
        "Label: Closing:\n\n" +
        "Example 2 (no punctuation at the boundary at all):\n" +
        "Text:\nFinancial update on trust fund operations The Secretariat presented the quarterly report on fund disbursements and outstanding commitments.\n" +
        "Label: Financial update on trust fund operations";

    private static readonly IReadOnlyDictionary<DocumentMode, BoundaryCutPrompt> Table =
        new Dictionary<DocumentMode, BoundaryCutPrompt>
        {
            [DocumentMode.VietnameseLegal] = new(LegalSystem, "Văn bản:", "Tiêu đề"),
            [DocumentMode.TypedNumbering] = new(RfcSystem, "Text:", "Heading"),
            [DocumentMode.FormatDriven] = new(MinutesSystem, "Text:", "Label"),
        };

    /// <summary>Domain đã có bảng cứng đo được — dùng để quyết định có gọi <see cref="TryCutAsync"/> hay không mà không tốn một lượt gọi model.</summary>
    public static bool IsSupported(DocumentMode mode) => Table.ContainsKey(mode);

    /// <summary>
    /// Gọi model cắt ranh giới cho <paramref name="text"/>. Trả về độ dài phần title (== điểm kết
    /// thúc <c>HeadingSpan</c>, tính từ đầu <paramref name="text"/>) nếu model trả một chuỗi là
    /// PREFIX HỢP LỆ của input; <see langword="null"/> nếu domain chưa có bảng, backend lỗi, hoặc
    /// câu trả lời không grounding được vào input.
    /// <para>
    /// Grounding là bắt buộc, không phải tuỳ chọn: model có thể sửa chính tả hoặc bịa thêm chữ so
    /// với nguyên văn. Cùng nguyên tắc <c>OutlineGroundingValidator</c> đã áp cho các tầng khác
    /// trong pipeline — heading.Text lệch <c>OriginalText[Start..End]</c> dù chỉ một ký tự sẽ bị
    /// cách ly âm thầm ở bước duyệt sau, nên phải chặn ở đây, không phải phát hiện ở đó.
    /// </para>
    /// </summary>
    public static async Task<int?> TryCutAsync(
        IHeaderClassifier classifier, DocumentMode mode, string text, CancellationToken ct = default)
    {
        if (!Table.TryGetValue(mode, out var prompt)) return null;
        if (string.IsNullOrWhiteSpace(text)) return null;

        var user = $"{prompt.UserPrefix}\n{text}\n{prompt.LabelWord}:";
        string raw;
        try
        {
            raw = await classifier.BoundaryCutAsync(prompt.SystemPrompt, user, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Lỗi backend không phải lỗi tất định của luật — bỏ qua, ứng viên giữ nguyên trạng thái
            // "chưa cắt được" cho đường xử lý khác, không ném lỗi làm hỏng cả lượt trích xuất.
            return null;
        }

        var label = raw.Trim().Trim('"', '\u201c', '\u201d');
        var prefixWord = $"{prompt.LabelWord}:";
        if (label.StartsWith(prefixWord, StringComparison.Ordinal))
            label = label[prefixWord.Length..].Trim();
        if (label.Length == 0) return null;

        return text.StartsWith(label, StringComparison.Ordinal) ? label.Length : null;
    }
}
