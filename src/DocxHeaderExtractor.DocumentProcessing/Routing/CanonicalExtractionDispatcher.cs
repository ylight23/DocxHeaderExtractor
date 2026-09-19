using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.DocumentProcessing.Routing;

/// <summary>Extracts a canonical document from one uploaded file of a format it owns.</summary>
public interface ICanonicalSourceExtractor
{
    SourceType Handles { get; }

    /// <summary>
    /// Extracts the canonical document, and with it the outline shape hosts consume.
    /// <para>
    /// Both come from one run on purpose. A host that needs an outline would otherwise have to
    /// take a second path, and a second path is a second answer about the same document.
    /// </para>
    /// <para>
    /// <paramref name="quarantinedIndexes"/> is the harness repair loop removing source occurrences
    /// a validator rejected, then re-running. It is not intent - it says nothing about what the
    /// caller wants out of the document - so it stays a parameter here rather than joining
    /// <see cref="AuthorityExtractionRequest"/>, which must carry the file and nothing else.
    /// </para>
    /// </summary>
    Task<Authority.AuthorityPipelineExecutionResult> ExtractAsync(
        UploadedFile file,
        IReadOnlySet<int>? quarantinedIndexes = null,
        CancellationToken ct = default);
}

/// <summary>
/// Sends the uploaded file to the extractor that owns its format, and nowhere else.
/// <para>
/// There is no discovery here. The dispatcher never looks beside the file, never consults the
/// working directory, and never lets one format's availability change how another is processed.
/// Each upload is its own job; two uploads are two jobs, and their canonical documents are merged
/// only if the user asks for that explicitly.
/// </para>
/// </summary>
public sealed class CanonicalExtractionDispatcher
{
    private readonly IReadOnlyDictionary<SourceType, ICanonicalSourceExtractor> _extractors;

    public CanonicalExtractionDispatcher(params ICanonicalSourceExtractor[] extractors)
    {
        ArgumentNullException.ThrowIfNull(extractors);
        _extractors = extractors.ToDictionary(item => item.Handles);
    }

    public Task<Authority.AuthorityPipelineExecutionResult> ExtractAsync(
        AuthorityExtractionRequest request,
        IReadOnlySet<int>? quarantinedIndexes = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Re-read rather than trusted: the record could have been built before the bytes were fully
        // written. The whole record is rebuilt, not just the type - routing on fresh bytes while
        // handing the extractor a stale hash would produce a document whose recorded identity is
        // not the identity of what was parsed, which is worse than either alone.
        var file = UploadedFile.FromLocalPath(request.File.LocalPath, request.File.OriginalFileName);
        return _extractors.TryGetValue(file.DetectedType, out var extractor)
            ? extractor.ExtractAsync(file, quarantinedIndexes, ct)
            : throw new UnsupportedSourceException(file);
    }
}
