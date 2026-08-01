namespace DocxHeaderExtractor.Core.Llm;

/// <summary>Backend suy luận dùng chung cho GGUF local và RPC OpenRouter.</summary>
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
}
