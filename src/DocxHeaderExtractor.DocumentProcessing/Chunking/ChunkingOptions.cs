namespace DocxHeaderExtractor.DocumentProcessing.Chunking;

/// <summary>
/// Size of the output section chunks (<c>SectionChunkProjection</c>). A product setting, the same for
/// every host and backend: model requests are sized by their own packing policy, not by this.
/// </summary>
public sealed class ChunkingOptions
{
    /// <summary>Token budget of one output chunk.</summary>
    public int TokenBudget { get; set; } = 2200;
}
