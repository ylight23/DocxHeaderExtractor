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

    /// <summary>
    /// Context ban đầu trước khi áp model profile. Model đã biết như Qwen2.5/Llama 3.2 được nâng
    /// lên 8192; model 4K không nhận diện sẽ được co ngân sách chunk cho vừa.
    /// </summary>
    public uint ContextSize { get; set; } = 4096;

    /// <summary>Số token tối đa cho mỗi document-view chunk (phần còn lại dành cho prompt + đầu ra).</summary>
    public int ChunkTokenBudget { get; set; } = 2200;

    /// <summary>
    /// Profile chunk cho backend chạy qua RPC (OpenRouter, LM Studio). Mặc định 4096/2200 là của
    /// bản local bị giới hạn VRAM; backend RPC không có ràng buộc đó nên dùng bộ 8K/5K đã đo cho
    /// Qwen. Không dùng chung mà để mặc định 2200 thì tài liệu bị xé vụn: đo được trên tài liệu
    /// thật là 13 ứng viên → 27 khối, tức 27 lượt RPC cho thứ nhét vừa vài lượt, và mỗi lượt lại
    /// gửi lại toàn bộ prompt hệ thống.
    /// </summary>
    public void UseRemoteChunkProfile()
    {
        ContextSize = 8192;
        ChunkTokenBudget = 5000;
    }

    /// <summary>Số token tối đa mô hình được sinh ra cho mỗi khối.</summary>
    public int MaxOutputTokens { get; set; } = 900;

    /// <summary>Số luồng CPU. Null = số lõi vật lý - 1.</summary>
    public int? Threads { get; set; }

    public int? BatchThreads { get; set; }

    public uint BatchSize { get; set; } = 512;

    /// <summary>0 = chạy hoàn toàn trên CPU (backend LLamaSharp.Backend.Cpu).</summary>
    public int GpuLayerCount { get; set; }

    /// <summary>0 = greedy, cho kết quả ổn định và lặp lại được.</summary>
    /// <summary>
    /// Seed dùng chung cho mọi backend. Backend RPC phải khai báo đúng seed này thì so sánh
    /// local-vs-LM Studio mới là so hai backend, không phải so hai cấu hình sampler.
    /// </summary>
    public const uint SharedSamplerSeed = 1234;

    public float Temperature { get; set; }

    public uint Seed { get; set; } = SharedSamplerSeed;

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
    // Batch vừa đủ lớn cho Qwen 7B: giảm số request nhưng tránh nhầm ID/cấp khi
    // mỗi ứng viên mang theo các đoạn lân cận.
    public int MaxCandidatesPerChunk { get; set; } = 6;

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
    /// Profile thực dụng cho Qwen2.5-7B: 8K context nhưng chỉ dành khoảng 5K cho document view, phần còn
    /// lại cho chat template, system prompt, JSON và đệm. Chỉ thay các giá trị mặc định, nên
    /// cấu hình người dùng đặt khác vẫn được giữ nguyên.
    /// </summary>
    public void ApplyRecommendedModelProfile()
    {
        var fileName = Path.GetFileName(ModelPath);
        var qwen = fileName.Contains("qwen", StringComparison.OrdinalIgnoreCase);
        var knownLongContext = qwen ||
            fileName.Contains("llama-3.2", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("llama3.2", StringComparison.OrdinalIgnoreCase);

        // Qwen 7B đã được đo với ngân sách 5K. Chỉ thay giá trị mặc định, không ghi đè cấu hình
        // chunk do người dùng chủ động đặt.
        if (qwen && ChunkTokenBudget == 2200) ChunkTokenBudget = 5000;

        var required = RequiredContextSize(ChunkTokenBudget, MaxOutputTokens);
        if (ContextSize >= required) return;

        if (knownLongContext && ContextSize == 4096)
        {
            // Qwen2.5 và Llama 3.2 đều hỗ trợ hơn 8K; 8192 là mức nhỏ nhất dạng power-of-two
            // chứa đủ document view + output + system prompt hiện tại.
            ContextSize = NextPowerOfTwo(required);
            return;
        }

        // Dưới mức tối thiểu cho prompt + output + một chunk hữu dụng thì không có cách co tiếp;
        // nâng lên power-of-two nhỏ nhất (thực tế là 4096).
        var minimumUsable = RequiredContextSize(400, MaxOutputTokens);
        if (ContextSize < minimumUsable) ContextSize = NextPowerOfTwo(minimumUsable);

        // Model không nhận diện được có thể thật sự chỉ hỗ trợ 4K. Không tự nâng lên 8K khi chưa
        // biết khả năng của model; thu nhỏ document chunk để cấu hình vẫn chạy được.
        ChunkTokenBudget = Math.Max(400, (int)ContextSize - MaxOutputTokens - FixedPromptTokens);
    }

    public static uint SuggestedContextForModel(string modelPath)
    {
        var options = new LlamaOptions { ModelPath = modelPath };
        options.ApplyRecommendedModelProfile();
        return options.ContextSize;
    }

    public static uint RequiredContextSize(int chunkTokenBudget, int maxOutputTokens) =>
        checked((uint)(chunkTokenBudget + maxOutputTokens + FixedPromptTokens));

    private static uint NextPowerOfTwo(uint value)
    {
        var result = 1024u;
        while (result < value && result < 1u << 30) result <<= 1;
        return result;
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
