using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.DocumentProcessing.Routing;

/// <summary>Extracts a canonical document from one uploaded file of a format it owns.</summary>
public interface ICanonicalSourceExtractor
{
    SourceType Handles { get; }

    Task<DocumentExtractionResult> ExtractAsync(UploadedFile file, CancellationToken ct = default);
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

    public Task<DocumentExtractionResult> ExtractAsync(
        AuthorityExtractionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Re-detected rather than trusted: the record could have been built before the bytes were
        // fully written, and the type is the one thing that must match what is about to be parsed.
        var type = UploadedSourceDetector.Detect(request.File.LocalPath);
        return _extractors.TryGetValue(type, out var extractor)
            ? extractor.ExtractAsync(request.File with { }, ct)
            : throw new UnsupportedSourceException(request.File);
    }
}
