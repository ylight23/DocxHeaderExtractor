using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>Storage boundary for deterministic search-index documents.</summary>
public interface ISearchIndexSink
{
    Task UpsertDocumentAsync(SearchIndexDocument document, CancellationToken cancellationToken = default);

    Task DeleteDocumentAsync(string documentId, CancellationToken cancellationToken = default);

    Task ReplaceDocumentAsync(
        string documentId,
        IReadOnlyList<SearchIndexDocument> documents,
        CancellationToken cancellationToken = default);
}

/// <summary>Retrieval boundary independent of any search or vector database SDK.</summary>
public interface ISearchIndexRetriever
{
    Task<IReadOnlyList<RetrievalHit>> SearchAsync(
        RetrievalQuery query,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Deterministic in-memory sink/retriever used by contract tests and local execution. The key is
/// document plus chunk identity, so replacement cannot accumulate stale chunks.
/// </summary>
public sealed class InMemorySearchIndex : ISearchIndexSink, ISearchIndexRetriever
{
    private readonly object _gate = new();
    private readonly Dictionary<(string DocumentId, string ChunkId), SearchIndexDocument> _documents = new();

    public Task UpsertDocumentAsync(
        SearchIndexDocument document,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateDocument(document);
        lock (_gate)
            _documents[(document.DocumentId, document.ChunkId)] = document;
        return Task.CompletedTask;
    }

    public Task DeleteDocumentAsync(
        string documentId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(documentId))
            throw new ArgumentException("Document ID is required.", nameof(documentId));

        lock (_gate)
        {
            foreach (var key in _documents.Keys.Where(key => key.DocumentId == documentId).ToArray())
                _documents.Remove(key);
        }

        return Task.CompletedTask;
    }

    public Task ReplaceDocumentAsync(
        string documentId,
        IReadOnlyList<SearchIndexDocument> documents,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(documentId))
            throw new ArgumentException("Document ID is required.", nameof(documentId));
        ArgumentNullException.ThrowIfNull(documents);

        var replacement = documents.ToArray();
        foreach (var document in replacement)
        {
            ValidateDocument(document);
            if (!string.Equals(document.DocumentId, documentId, StringComparison.Ordinal))
                throw new InvalidOperationException("search-index-document-id-mismatch");
        }

        if (replacement.Select(document => document.ChunkId).Distinct(StringComparer.Ordinal).Count() != replacement.Length)
            throw new InvalidOperationException("duplicate-search-index-chunk-id");

        lock (_gate)
        {
            foreach (var key in _documents.Keys.Where(key => key.DocumentId == documentId).ToArray())
                _documents.Remove(key);
            foreach (var document in replacement)
                _documents[(document.DocumentId, document.ChunkId)] = document;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RetrievalHit>> SearchAsync(
        RetrievalQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        SearchIndexDocument[] snapshot;
        lock (_gate)
            snapshot = _documents.Values.ToArray();

        var documentFilter = query.DocumentIds?.ToHashSet(StringComparer.Ordinal);
        var sectionFilter = query.SectionIds?.ToHashSet(StringComparer.Ordinal);
        var typeFilter = query.StructuralTypes?.Select(type => type.ToString()).ToHashSet(StringComparer.Ordinal);
        var terms = Tokenize(query.QueryText);

        var hits = snapshot
            .Where(document => documentFilter is null || documentFilter.Contains(document.DocumentId))
            .Where(document => sectionFilter is null || sectionFilter.Contains(document.SectionId))
            .Where(document => typeFilter is null || document.StructuralTypes.Any(type => typeFilter.Contains(type)))
            .Select(document => (Document: document, Score: Score(document.Text, terms)))
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Document.DocumentId, StringComparer.Ordinal)
            .ThenBy(item => item.Document.ChunkId, StringComparer.Ordinal)
            .Take(query.TopK)
            .Select(item => new RetrievalHit(
                item.Document.DocumentId,
                item.Document.ChunkId,
                item.Score,
                item.Document.Text,
                item.Document.SourceIds,
                item.Document.SectionPath,
                item.Document.StructuralContext,
                item.Document.Relations))
            .ToArray();

        return Task.FromResult<IReadOnlyList<RetrievalHit>>(hits);
    }

    public IReadOnlyList<SearchIndexDocument> Snapshot()
    {
        lock (_gate)
            return _documents.Values
                .OrderBy(document => document.DocumentId, StringComparer.Ordinal)
                .ThenBy(document => document.ChunkId, StringComparer.Ordinal)
                .ToArray();
    }

    private static void ValidateDocument(SearchIndexDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (string.IsNullOrWhiteSpace(document.DocumentId))
            throw new InvalidOperationException("search-index-empty-document-id");
        if (string.IsNullOrWhiteSpace(document.ChunkId))
            throw new InvalidOperationException("search-index-empty-chunk-id");
        if (string.IsNullOrEmpty(document.Text))
            throw new InvalidOperationException("search-index-empty-text");
    }

    private static string[] Tokenize(string value) => value
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(term => term.ToLowerInvariant())
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    private static double Score(string text, IReadOnlyList<string> terms)
    {
        var haystack = Tokenize(text).ToHashSet(StringComparer.Ordinal);
        return terms.Count == 0 ? 0 : (double)terms.Count(term => haystack.Contains(term)) / terms.Count;
    }
}

/// <summary>Lifecycle facade that atomically replaces one document's indexed chunks.</summary>
public sealed class SearchIndexRuntime
{
    private readonly ISearchIndexSink _sink;

    public SearchIndexRuntime(ISearchIndexSink sink)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
    }

    public Task ReplaceAsync(
        DocumentExtractionResult extraction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(extraction);
        var documents = SearchIndexProjection.Project(extraction);
        return _sink.ReplaceDocumentAsync(
            extraction.DocumentIdentity.DocumentId,
            documents,
            cancellationToken);
    }

    public Task DeleteAsync(
        string documentId,
        CancellationToken cancellationToken = default) =>
        _sink.DeleteDocumentAsync(documentId, cancellationToken);
}
