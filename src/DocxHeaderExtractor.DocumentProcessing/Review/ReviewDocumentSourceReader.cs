using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.DocumentProcessing.Review;

public sealed record ReviewDocumentSourceSnapshot(
    SourceDocument Document,
    IReadOnlyList<int> SourceIndexes);

/// <summary>
/// Reads source-native DOCX facts and the nonblank source-index view for review callers.
/// Extraction and writeback must use the same extraction policy to preserve ordinal mapping.
/// </summary>
public sealed class ReviewDocumentSourceReader
{
    private readonly ExtractionOptions _options;

    public ReviewDocumentSourceReader(PipelineOptions? options = null)
        : this((options ?? new PipelineOptions()).Extraction)
    {
    }

    public ReviewDocumentSourceReader(ExtractionOptions options)
    {
        _options = options;
    }

    public ReviewDocumentSourceSnapshot Read(string inputPath)
    {
        var conversion = OfficeDocumentConverter.EnsureDocx(inputPath);
        try
        {
            var source = new OpenXmlDocumentSource(_options).Read(conversion.Path);
            var sourceIndexes = source.Paragraphs
                .Where(p => !string.IsNullOrWhiteSpace(p.Text))
                .Select(p => p.SourceOrdinal)
                .ToArray();
            return new ReviewDocumentSourceSnapshot(source, sourceIndexes);
        }
        finally
        {
            OfficeDocumentConverter.Cleanup(conversion);
        }
    }
}
