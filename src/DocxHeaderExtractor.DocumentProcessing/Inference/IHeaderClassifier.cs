using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.DocumentProcessing.Inference;

/// <summary>Backend suy luận dùng chung cho GGUF local, LM Studio local RPC và OpenRouter.</summary>
public interface IHeaderClassifier : IDisposable
{
    string ModelName { get; }
    int ContextSize { get; }
    string RuntimeDescription { get; }
    /// <summary>Số token prefix đã cache; backend không hỗ trợ trả 0.</summary>
    int SharedPrefixTokens { get; }

    Task<ChunkResult> ClassifyAsync(
        string chunkXml,
        IReadOnlyList<int> allowedIndexes,
        CancellationToken ct = default);

    /// <summary>
    /// Lượt phản biện độc lập cho các heading model-only có bằng chứng yếu. Mục tiêu là tìm
    /// phản ví dụ ngữ nghĩa, không phải tăng điểm cho quyết định ban đầu.
    /// </summary>
    Task<ChunkResult> CritiqueAsync(
        string chunkXml,
        IReadOnlyList<int> allowedIndexes,
        CancellationToken ct = default);

    Task<ChunkResult> ClassifyHierarchyAsync(
        IReadOnlyList<HierarchyItem> context,
        IReadOnlyList<HierarchyItem> headings,
        CancellationToken ct = default);

    /// <summary>
    /// Cắt ranh giới title/body cho MỘT đoạn văn bản đã biết là heading dính liền thân bài (câu hỏi
    /// "có phải heading không" đã được tầng khác trả lời — nhiệm vụ ở đây hẹp hơn nhiều so với
    /// <see cref="ClassifyAsync"/>: không JSON schema, không multi-index, chỉ system+user rồi trả
    /// nguyên văn completion (đã trim). Người gọi (<c>LlmBoundaryCutter</c>) tự kiểm câu trả lời có
    /// phải PREFIX hợp lệ của input hay không trước khi dùng làm ranh giới — backend không tự bảo
    /// đảm điều đó.
    /// </summary>
    Task<string> BoundaryCutAsync(string systemPrompt, string userMessage, CancellationToken ct = default);
}

/// <summary>
/// Composition-root seam for inference providers. Core consumes the neutral classifier contract;
/// provider construction belongs to Infrastructure.
/// </summary>
public interface IHeaderClassifierFactory
{
    Task<IHeaderClassifier> CreateAsync(PipelineOptions options, CancellationToken ct = default);
}
