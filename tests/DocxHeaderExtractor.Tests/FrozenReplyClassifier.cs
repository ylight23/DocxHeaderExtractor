using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Replays provider replies captured from one real run, in call order, so the harness can be
/// changed and re-measured without asking the model again.
/// <para>
/// This is the only way to attribute a metric change to a harness change. A fresh call to a
/// stochastic model answers a different question — it measures the model and the harness at once,
/// and cannot tell you which one moved.
/// </para>
/// <para>
/// Calls past the end of the recording return an empty heading set rather than replaying an
/// unrelated answer. <see cref="CallsBeyondRecording"/> reports how often that happened, so a
/// replay can never quietly claim coverage of a pass that was never recorded.
/// </para>
/// </summary>
internal sealed class FrozenReplyClassifier(IReadOnlyList<string> replies) : IHeaderClassifier
{
    private int _next;

    public int CallsBeyondRecording { get; private set; }

    public string ModelName => "frozen-replay";
    public int ContextSize => 1 << 20;
    public string RuntimeDescription => "offline replay of frozen provider replies";
    public int SharedPrefixTokens => 0;

    public Task<string> BoundaryCutAsync(
        string systemPrompt, string userMessage, CancellationToken ct = default, int expectedItemCount = 0)
    {
        if (_next < replies.Count) return Task.FromResult(replies[_next++]);
        CallsBeyondRecording++;
        return Task.FromResult("{\"headings\":[]}");
    }

    public Task<ChunkResult> ClassifyAsync(string chunkXml, IReadOnlyList<int> allowedIndexes, CancellationToken ct = default) =>
        throw new NotSupportedException("The canonical route does not use ClassifyAsync.");

    public Task<ChunkResult> CritiqueAsync(string chunkXml, IReadOnlyList<int> allowedIndexes, CancellationToken ct = default) =>
        throw new NotSupportedException("The canonical route does not use CritiqueAsync.");

    public Task<ChunkResult> ClassifyHierarchyAsync(
        IReadOnlyList<HierarchyItem> context, IReadOnlyList<HierarchyItem> headings, CancellationToken ct = default) =>
        throw new NotSupportedException("The canonical route does not use ClassifyHierarchyAsync.");

    public void Dispose() { }
}
