using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Captures what production would send, without sending it.
/// <para>
/// The request is recorded at the provider boundary, fully formed, before any response exists. It
/// is therefore the production request, not a reconstruction of one. The empty reply this returns
/// affects only what happens after the boundary, and each segment's packet is built from the source
/// evidence and its own ownership window rather than from a previous reply, so no captured request
/// depends on the fake.
/// </para>
/// <para>
/// One request is deliberately not captured: the placement pass fires only when headings remain
/// unresolved, and an empty reply produces no headings at all. A preflight therefore covers the
/// discovery requests and says so, rather than silently reporting a request count production would
/// not match.
/// </para>
/// </summary>
internal sealed class RequestCapturingClassifier : IHeaderClassifier
{
    public List<CapturedRequest> Requests { get; } = [];

    public string ModelName => "request-capture";
    public int ContextSize => 1 << 20;
    public string RuntimeDescription => "preflight capture, no provider call";
    public int SharedPrefixTokens => 0;

    public Task<string> BoundaryCutAsync(
        string systemPrompt, string userMessage, CancellationToken ct = default, int expectedItemCount = 0)
    {
        Requests.Add(new CapturedRequest(systemPrompt, userMessage, expectedItemCount));
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

internal sealed record CapturedRequest(string SystemPrompt, string UserMessage, int ExpectedItemCount);
