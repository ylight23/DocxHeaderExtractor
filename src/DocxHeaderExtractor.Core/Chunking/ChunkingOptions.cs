namespace DocxHeaderExtractor.Core.Chunking;

/// <summary>
/// Cách cắt document view thành khối để hỏi mô hình. Đây là việc của PIPELINE, không của backend:
/// cùng một tài liệu, cùng cách cắt, dù câu hỏi đi tới GGUF cục bộ, LM Studio hay OpenRouter.
/// <para>
/// Trước đây ba giá trị này nằm trong <c>LlamaOptions</c> — lớp mang tên backend GGUF, chỉ vì
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
    /// Trần số ứng viên mỗi khối. Ở grammar liệt kê, mô hình sinh một quyết định cho mỗi ứng viên
    /// trong CÙNG một chuỗi tự hồi quy, nên khối càng dài thì lỗi càng bám theo vị trí.
    /// </summary>
    public int MaxCandidatesPerChunk { get; set; } = 6;

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
    /// Ngân sách suy từ context mà CHÍNH backend khai báo, trừ đi chỗ dành cho prompt hệ thống và
    /// phần đầu ra.
    /// <para>
    /// Lý do cần: hằng 5000 ở <see cref="UseRemoteProfile"/> là con số đo cho Qwen 7B chạy cục bộ
    /// với context 8192, rồi bị đem dùng cho mọi backend RPC. LM Studio khai 16384 qua
    /// <c>IHeaderClassifier.ContextSize</c> nhưng pipeline vẫn cắt theo 5000 — tài liệu bị chia
    /// nhỏ hơn mức cần, mà mỗi khối lại là một lượt RPC.
    /// </para>
    /// </summary>
    public static int DeriveTokenBudget(int contextSize, int maxOutputTokens, int promptReserve) =>
        Math.Max(400, contextSize - maxOutputTokens - promptReserve);
}
