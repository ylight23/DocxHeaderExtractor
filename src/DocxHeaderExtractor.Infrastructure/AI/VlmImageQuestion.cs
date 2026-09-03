using LLama;
using LLama.Common;
using LLama.Native;

using DocxHeaderExtractor.DocumentProcessing.Vision;

namespace DocxHeaderExtractor.Infrastructure.AI;

/// <summary>
/// Bọc suy luận đa phương thức (VLM) qua LLamaSharp: nạp một model .gguf + mmproj, trả lời MỘT câu hỏi
/// về MỘT ảnh mỗi lượt gọi. Không giữ hội thoại nhiều lượt — mỗi câu hỏi độc lập, đúng mẫu cost thấp
/// đã đo (handoff §172: ~7,5s nạp mtmd + suy luận, không cần batch).
/// <para>
/// API thật của LLamaSharp 0.27 dùng namespace <c>Mtmd</c> (không phải <c>Clip</c>/<c>Llava</c> như một
/// khảo sát trước đó ghi nhầm — đã đính chính ở §172). Lớp này giấu chi tiết đó sau một hàm duy nhất.
/// </para>
/// <para>
/// KHÔNG dùng cho pipeline trích xuất chính — chỉ dùng cho các cổng chẩn đoán có điều kiện kích hoạt cụ
/// thể (vd xác nhận paragraph hỏng thật hay lỗi parser, xem <see cref="Repair.CorruptParagraphVisualVerifier"/>).
/// Nạp model tốn vài giây và vài trăm MB-GB RAM; gọi lại nhiều lần nên tái dùng cùng một instance.
/// </para>
/// </summary>
public sealed class VlmImageQuestion : IPdfVisualQuestion
{
    private readonly LLamaWeights _weights;
    private readonly MtmdWeights _mtmd;
    private readonly LLamaContext _context;

    private VlmImageQuestion(LLamaWeights weights, MtmdWeights mtmd, LLamaContext context)
    {
        _weights = weights;
        _mtmd = mtmd;
        _context = context;
    }

    public static async Task<VlmImageQuestion> LoadAsync(
        string modelPath,
        string mmprojPath,
        int contextSize = 4096,
        int gpuLayerCount = 0,
        CancellationToken ct = default)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"Không tìm thấy model VLM: {modelPath}", modelPath);
        if (!File.Exists(mmprojPath))
            throw new FileNotFoundException($"Không tìm thấy mmproj: {mmprojPath}", mmprojPath);

        var modelParams = new ModelParams(modelPath) { ContextSize = (uint)contextSize, GpuLayerCount = gpuLayerCount };
        var weights = await LLamaWeights.LoadFromFileAsync(modelParams, ct);

        var mtmdParams = MtmdContextParams.Default();
        mtmdParams.UseGpu = gpuLayerCount > 0;
        var mtmd = await MtmdWeights.LoadFromFileAsync(mmprojPath, weights, mtmdParams, ct);
        if (!mtmd.SupportsVision)
        {
            mtmd.Dispose();
            weights.Dispose();
            throw new InvalidOperationException(
                $"mmproj '{mmprojPath}' nạp được nhưng model không báo hỗ trợ thị giác (SupportsVision=false).");
        }

        var context = new LLamaContext(weights, modelParams);
        return new VlmImageQuestion(weights, mtmd, context);
    }

    /// <summary>
    /// Hỏi một câu về một ảnh PNG/JPEG (bytes). Trả về nguyên văn câu trả lời của model — KHÔNG parse
    /// JSON ở đây; nơi gọi tự parse verdict/evidence theo hợp đồng riêng của từng vai trò (§143).
    /// </summary>
    public async Task<string> AskAsync(
        byte[] imageBytes, string question, int maxTokens = 300, CancellationToken ct = default)
    {
        // Mỗi câu hỏi độc lập, không phải hội thoại tiếp nối — nhưng tạo InteractiveExecutor mới mỗi
        // lượt KHÔNG xoá được KV cache của _context (dùng chung, tái nạp cho rẻ). Không xoá thì lượt
        // gọi thứ hai trở đi lỗi native "inconsistent sequence positions" (M-RoPE đòi X < Y) — bắt được
        // qua chạy thật lượt 2/4 trên 053 (handoff §174), không thấy được nếu chỉ test một câu hỏi.
        _context.NativeHandle.MemoryClear(true);

        _mtmd.ClearMedia();
        var embed = _mtmd.LoadMedia(imageBytes);

        var executor = new InteractiveExecutor(_context, _mtmd);
        executor.Embeds.Add(embed);

        var prompt = $"<__media__>\n{question}";
        var inferenceParams = new InferenceParams { MaxTokens = maxTokens, AntiPrompts = ["\n\n"] };

        var sb = new System.Text.StringBuilder();
        await foreach (var token in executor.InferAsync(prompt, inferenceParams, ct))
            sb.Append(token);
        return sb.ToString().Trim();
    }

    public void Dispose()
    {
        _context.Dispose();
        _mtmd.Dispose();
        _weights.Dispose();
    }
}
