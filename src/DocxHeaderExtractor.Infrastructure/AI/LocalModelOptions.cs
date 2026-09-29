namespace DocxHeaderExtractor.Infrastructure.AI;

/// <summary>
/// Cấu hình nạp mô hình GGUF đã lượng tử hoá cho backend cục bộ.
/// </summary>
public sealed class LocalModelOptions
{
    /// <summary>Đường dẫn tới file .gguf.</summary>
    public string ModelPath { get; set; } = "";

    /// <summary>
    /// Context tối thiểu. Khi <see cref="AutoContextSize"/> bật (mặc định), context thật được đọc
    /// từ chính GGUF sau khi nạp weights và chỉ được NÂNG lên, tới <see cref="MaxAutoContextSize"/>.
    /// </summary>
    public uint ContextSize { get; set; } = 4096;

    /// <summary>
    /// Đọc <c>{arch}.context_length</c> từ metadata GGUF và nâng context lên theo model, thay vì
    /// giữ một con số cứng. Đo trên chính model đang dùng: <c>qwen35.context_length = 262144</c>.
    /// Truyền <c>--ctx</c> tường minh thì cờ này tự tắt: lựa chọn của người dùng luôn thắng.
    /// </summary>
    public bool AutoContextSize { get; set; } = true;

    /// <summary>
    /// Trần cho <see cref="AutoContextSize"/>. KHÔNG lấy thẳng con số GGUF khai báo: 262.144 token
    /// KV-cache của một model 9B vượt xa VRAM của mọi máy đang dùng, và nạp thất bại thì tệ hơn
    /// context nhỏ. 32768 là cấu hình ĐÃ ĐO của dự án.
    /// </summary>
    public const uint MaxAutoContextSize = 32768;

    /// <summary>Số luồng CPU. Null = số lõi vật lý - 1.</summary>
    public int? Threads { get; set; }

    public int? BatchThreads { get; set; }

    public uint BatchSize { get; set; } = 512;

    /// <summary>0 = chạy hoàn toàn trên CPU (backend LLamaSharp.Backend.Cpu).</summary>
    public int GpuLayerCount { get; set; }

    /// <summary>
    /// Seed dùng chung cho mọi backend. Backend RPC phải khai báo đúng seed này thì so sánh
    /// local-vs-LM Studio mới là so hai backend, không phải so hai cấu hình sampler.
    /// </summary>
    public const uint SharedSamplerSeed = 1234;

    public uint Seed { get; set; } = SharedSamplerSeed;

    /// <summary>In log gốc của llama.cpp.</summary>
    public bool VerboseNativeLog { get; set; }

    /// <summary>
    /// Bản sao nông để bước nạp chốt context mà không ghi ngược lên cấu hình của người gọi. Mọi
    /// field đều là kiểu giá trị hoặc chuỗi bất biến nên sao nông là đủ.
    /// </summary>
    public LocalModelOptions Clone() => (LocalModelOptions)MemberwiseClone();

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ModelPath))
            throw new InvalidOperationException("Chưa cấu hình đường dẫn mô hình .gguf (--model hoặc appsettings.json).");
        if (!File.Exists(ModelPath))
            throw new FileNotFoundException($"Không tìm thấy file mô hình: {ModelPath}", ModelPath);
    }
}
