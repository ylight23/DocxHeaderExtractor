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

    /// <summary>Kích thước cửa sổ ngữ cảnh. 4096 đủ cho một khối XML tinh gọn và tiết kiệm RAM.</summary>
    public uint ContextSize { get; set; } = 4096;

    /// <summary>Số token tối đa cho mỗi khối XML gửi vào mô hình (phần còn lại dành cho prompt + đầu ra).</summary>
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

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ModelPath))
            throw new InvalidOperationException("Chưa cấu hình đường dẫn mô hình .gguf (--model hoặc appsettings.json).");
        if (!File.Exists(ModelPath))
            throw new FileNotFoundException($"Không tìm thấy file mô hình: {ModelPath}", ModelPath);
        if (ChunkTokenBudget + MaxOutputTokens + 800 > ContextSize)
            throw new InvalidOperationException(
                $"ContextSize ({ContextSize}) quá nhỏ so với ChunkTokenBudget ({ChunkTokenBudget}) + MaxOutputTokens ({MaxOutputTokens}).");
    }
}
