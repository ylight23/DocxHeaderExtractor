namespace DocxHeaderExtractor.DocumentProcessing.Chunking;

/// <summary>
/// Cách cắt document view thành khối để hỏi mô hình. Đây là việc của PIPELINE, không của backend:
/// cùng một tài liệu, cùng cách cắt, dù câu hỏi đi tới GGUF cục bộ, LM Studio hay OpenRouter.
/// <para>
/// Trước đây ba giá trị này nằm trong <c>LocalModelOptions</c> — lớp mang tên backend GGUF, chỉ vì
/// backend đó ra đời trước. Bốn hậu quả đã đo được:
/// </para>
/// <list type="number">
/// <item>Nhánh LM Studio quên đặt profile chunk nên thừa hưởng mặc định 2200 của bản local bị giới
/// hạn VRAM: 13 ứng viên bị xé thành 27 khối thay vì ~7, mỗi khối một lượt RPC.</item>
/// <item>Luật nâng ngân sách lên 5000 bám vào TÊN FILE .gguf (<c>Path.GetFileName(ModelPath)</c>
/// chứa "qwen"). Chạy đúng bộ trọng số đó qua LM Studio thì luật không bao giờ kích hoạt vì không
/// có đường dẫn file nào.</item>
/// <item>Backend RPC phải ghi giá trị giả vào <c>Llama.ContextSize</c> — ô mô tả context của
/// llama.cpp cục bộ — chỉ để phép chia khối ra đúng, trong khi context thật của LM Studio nằm ở
/// trường khác.</item>
/// <item>Chữ ký calibration nhúng <c>grammar</c>/<c>temperature</c> của backend cục bộ vào cả
/// những lượt chạy không dùng backend đó.</item>
/// </list>
/// </summary>
public sealed class ChunkingOptions
{
    /// <summary>Số token tối đa cho phần document view của mỗi khối.</summary>
    public int TokenBudget { get; set; } = 2200;

    /// <summary>
    /// Trần số ứng viên mỗi khối; <c>0</c> = không chặn, để ngân sách token quyết định một mình.
    /// <para>
    /// MẶC ĐỊNH 0. Trần cứng 6 trước đây sinh từ một phép đo trên Qwen 7B với context 8192
    /// (40 ứng viên/khối cho đúng 7/40, 13 ứng viên/khối cho 6/13) rồi được áp cho mọi model, mọi
    /// context, mọi tài liệu. Nó cũng vô hiệu hoá việc suy ngân sách từ context server khai: đo
    /// được là ngân sách nhảy 5000 → 14216 mà số khối không đổi, vì trần 6 mới là thứ ràng buộc.
    /// </para>
    /// <para>
    /// ĐÁNH ĐỔI CÓ THẬT: khối dài hơn nghĩa là mô hình phải giữ nhiều quyết định hơn trong cùng
    /// một chuỗi tự hồi quy, và phép đo 40 → 7/40 ở trên là cảnh báo trực tiếp. Đặt lại một con số
    /// khác 0 nếu đo được rằng tài liệu của bạn cần chặn.
    /// </para>
    /// </summary>
    public int MaxCandidatesPerChunk { get; set; }

    /// <summary>Số ứng viên chồng lấn giữa hai khối liên tiếp.</summary>
    public int Overlap { get; set; } = 2;

    /// <summary>Người dùng đã tự đặt ngân sách; đừng suy lại từ context của backend.</summary>
    public bool TokenBudgetExplicit { get; private set; }

    /// <summary>Đặt ngân sách theo yêu cầu tường minh của người dùng (cờ CLI, form, tham số MCP).</summary>
    public void SetExplicitTokenBudget(int tokens)
    {
        TokenBudget = tokens;
        TokenBudgetExplicit = true;
    }

    /// <summary>
    /// Profile cho backend chạy qua RPC (LM Studio, OpenRouter). Mặc định 2200 là của bản local bị
    /// giới hạn VRAM; backend RPC không có ràng buộc đó nên dùng bộ 5K đã đo cho Qwen.
    /// </summary>
    public void UseRemoteProfile() => TokenBudget = 5000;

    /// <summary>
    /// Phần context dành cho document view, suy từ profile DUY NHẤT đã đo: Qwen 7B dùng ngân sách
    /// 5000 trên context 8192 — tức khoảng 61%, phần còn lại cho prompt hệ thống, chat template,
    /// JSON đầu ra và đệm.
    /// </summary>
    public const double MeasuredContextShare = 5000d / 8192d;

    /// <summary>
    /// Ngân sách ước lượng theo context mà CHÍNH backend khai báo.
    /// <para>
    /// Hai thái cực đều đã đo và đều sai. Hằng 5000 cứng: đúng cho Qwen 7B ở context 8192 nhưng bị
    /// đem áp cho mọi server, kể cả LM Studio khai 16384. Dùng kịch context (14216): khối phình ra
    /// và <b>chậm hơn ~60%</b> — cùng tài liệu, 2 khối mất 143 s còn 1 khối mất 231 s, vì attention
    /// là bậc hai theo độ dài prompt nên gộp khối không hề rẻ đi. Chưa kể phép đo cũ trong repo:
    /// 40 ứng viên/khối cho đúng 7/40.
    /// </para>
    /// <para>
    /// Nên lấy tỉ lệ đã đo mà nhân lên, rồi chặn bằng ràng buộc cứng của cửa sổ ngữ cảnh. Ở 8192
    /// nó tái tạo đúng con số 5000 đã đo; ở 16384 cho ~10000 thay vì 14216; ở 4096 thì ràng buộc
    /// cứng thắng và ngân sách co lại cho vừa.
    /// </para>
    /// </summary>
    public static int DeriveTokenBudget(int contextSize, int maxOutputTokens, int promptReserve)
    {
        var hardLimit = contextSize - maxOutputTokens - promptReserve;
        var measured = (int)(contextSize * MeasuredContextShare);
        return Math.Max(400, Math.Min(hardLimit, measured));
    }
}
