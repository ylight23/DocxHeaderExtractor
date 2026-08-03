namespace DocxHeaderExtractor.Core.Llm;

public enum GrammarMode
{
    /// <summary>Không ràng buộc – mô hình tự do trả lời, dựa vào bộ đọc JSON chịu lỗi.</summary>
    None,

    /// <summary>Ràng buộc lược đồ nhưng mô hình tự chọn liệt kê bao nhiêu mục.</summary>
    Free,

    /// <summary>
    /// Grammar sinh riêng cho từng khối, cố định sẵn danh sách chỉ số: mô hình buộc phải
    /// trả lời đúng một cấp cho mỗi ứng viên, đúng thứ tự. Tin cậy nhất với mô hình nhỏ.
    /// </summary>
    Enumerated,
}

/// <summary>
/// Cấu hình nạp mô hình GGUF đã lượng tử hoá (mặc định nhắm Llama-3.2-3B-Instruct-Q4_K_M).
/// Toàn bộ chạy trên CPU: GpuLayerCount = 0.
/// </summary>
public sealed class LlamaOptions
{
    /// <summary>Đường dẫn tới file .gguf.</summary>
    public string ModelPath { get; set; } = "";

    /// <summary>Kích thước cửa sổ ngữ cảnh. 4096 đủ cho một document-view chunk nhỏ và tiết kiệm RAM.</summary>
    public uint ContextSize { get; set; } = 4096;

    /// <summary>Số token tối đa cho mỗi document-view chunk (phần còn lại dành cho prompt + đầu ra).</summary>
    public int ChunkTokenBudget { get; set; } = 2200;

    /// <summary>Số token tối đa mô hình được sinh ra cho mỗi khối.</summary>
    public int MaxOutputTokens { get; set; } = 900;

    /// <summary>Số luồng CPU. Null = số lõi vật lý - 1.</summary>
    public int? Threads { get; set; }

    public int? BatchThreads { get; set; }

    public uint BatchSize { get; set; } = 512;

    /// <summary>0 = chạy hoàn toàn trên CPU (backend LLamaSharp.Backend.Cpu).</summary>
    public int GpuLayerCount { get; set; }

    /// <summary>0 = greedy, cho kết quả ổn định và lặp lại được.</summary>
    public float Temperature { get; set; }

    public uint Seed { get; set; } = 1234;

    /// <summary>Cách ràng buộc đầu ra bằng GBNF.</summary>
    public GrammarMode GrammarMode { get; set; } = GrammarMode.Enumerated;

    /// <summary>Số ứng viên chồng lấn giữa hai khối liên tiếp.</summary>
    public int ChunkOverlap { get; set; } = 2;

    /// <summary>
    /// Trần số ứng viên mỗi khối. Ở grammar liệt kê, mô hình sinh một chữ số cho mỗi ứng viên
    /// trong MỘT chuỗi tự hồi quy, nên khối càng dài thì một dãy 0 càng dễ kéo theo các 0 sai.
    /// Đo trên Qwen2.5-7B: 40 ứng viên/khối cho 7/40, cùng tài liệu ở 13 ứng viên/khối cho 6/13
    /// với các tiêu đề then chốt đều đúng. Nhỏ hơn thì chính xác hơn nhưng tốn prefill nhiều lần.
    /// </summary>
    public int MaxCandidatesPerChunk { get; set; } = 12;

    /// <summary>In log gốc của llama.cpp.</summary>
    public bool VerboseNativeLog { get; set; }

    /// <summary>
    /// Nạp phần prompt dùng chung (system + ví dụ one-shot, ~900 token) MỘT LẦN rồi tái dùng
    /// cho mọi khối, thay vì nạp lại từ đầu ở từng khối.
    /// <para>
    /// Cơ sở đo được: thêm 3 khối làm tổng thời gian tăng 164 s (~55 s/khối), trong khi cắt 90%
    /// số token phải SINH lại tiết kiệm 0 giây. Tức là thời gian nằm ở khâu nạp prompt, và phần
    /// lớn prompt mỗi khối là đoạn giống hệt nhau.
    /// </para>
    /// <para>
    /// Kết quả: phần khối nhanh hơn 58% trên bộ test, 38% trên tài liệu 898 đoạn. Chỉ số đo được
    /// KHÔNG đổi trên cả 8 tài liệu (P 100%, R 97,2%, đúng cấp 100%).
    /// </para>
    /// <para>
    /// LƯU Ý: không bảo đảm cho ra đúng từng bit. BatchedExecutor gộp batch khác StatelessExecutor
    /// nên thứ tự cộng dồn dấu phẩy động khác, đủ lật vài quyết định sát ranh giới — quan sát được
    /// ở khối 3 và 4 của tài liệu thật. Kết quả cuối vẫn trùng nhờ hai lưới an toàn hấp thụ:
    /// cấp đọc từ outlineLvl và TrustStyles khôi phục đoạn bị bỏ. Tắt bằng --no-reuse-prefix
    /// nếu cần tái lập chính xác từng bước.
    /// </para>
    /// </summary>
    public bool ReusePromptPrefix { get; set; } = true;

    /// <summary>
    /// Profile thực dụng cho Qwen2.5-7B: 8K context nhưng chỉ dành khoảng 5K cho XML, phần còn
    /// lại cho chat template, system prompt, JSON và đệm. Chỉ thay các giá trị mặc định, nên
    /// cấu hình người dùng đặt khác vẫn được giữ nguyên.
    /// </summary>
    public void ApplyRecommendedModelProfile()
    {
        if (!Path.GetFileName(ModelPath).Contains("qwen", StringComparison.OrdinalIgnoreCase)) return;
        if (ContextSize == 4096) ContextSize = 8192;
        if (ChunkTokenBudget == 2200) ChunkTokenBudget = 5000;
    }

    /// <summary>
    /// Phần prompt không đổi giữa các khối: system prompt + ví dụ one-shot + GBNF.
    /// ĐO ĐƯỢC 1.212 token bằng tokenizer Qwen2.5-7B (3.757 ký tự, 3.10 ký tự/token vì gần như
    /// toàn tiếng Anh và markup); làm tròn lên cho chat template và lề.
    /// </summary>
    public const int FixedPromptTokens = 1400;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ModelPath))
            throw new InvalidOperationException("Chưa cấu hình đường dẫn mô hình .gguf (--model hoặc appsettings.json).");
        if (!File.Exists(ModelPath))
            throw new FileNotFoundException($"Không tìm thấy file mô hình: {ModelPath}", ModelPath);
        // 1400 = prompt cố định (system + one-shot + GBNF) ĐO ĐƯỢC 1.212 token bằng tokenizer
        // Qwen2.5-7B, cộng lề cho chat template. Hằng số cũ 800 nhỏ hơn phần cố định thật.
        if (ChunkTokenBudget + MaxOutputTokens + FixedPromptTokens > ContextSize)
            throw new InvalidOperationException(
                $"ContextSize ({ContextSize}) quá nhỏ: ChunkTokenBudget ({ChunkTokenBudget}) + " +
                $"MaxOutputTokens ({MaxOutputTokens}) + prompt cố định ({FixedPromptTokens}) vượt quá.");
    }
}
