using DocxHeaderExtractor.DocumentProcessing.Review;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.DocumentProcessing.Review;

public sealed record DocumentSourceSnapshot(
    SourceDocument Document,
    IReadOnlyList<int> SourceIndexes);

/// <summary>
/// Explicit evaluation boundary. The outline projection is consumed here once to produce
/// source facts and the frozen source-index view; evaluator code never receives Slim types.
/// </summary>
public sealed class AuthorityDocumentSourceReader
{
    private readonly ExtractionOptions _options;

    public AuthorityDocumentSourceReader(PipelineOptions? options = null)
        : this((options ?? new PipelineOptions()).Extraction)
    {
    }

    public AuthorityDocumentSourceReader(ExtractionOptions options)
    {
        _options = options;
    }

    public DocumentSourceSnapshot Read(string inputPath)
    {
        var conversion = OfficeDocumentConverter.EnsureDocx(inputPath);
        try
        {
            var source = new OpenXmlDocumentSource(_options).Read(conversion.Path);
            var sourceIndexes = source.Paragraphs
                .Where(p => !string.IsNullOrWhiteSpace(p.Text))
                .Select(p => p.SourceOrdinal)
                .ToArray();
            return new DocumentSourceSnapshot(source, sourceIndexes);
        }
        finally
        {
            OfficeDocumentConverter.Cleanup(conversion);
        }
    }
}
