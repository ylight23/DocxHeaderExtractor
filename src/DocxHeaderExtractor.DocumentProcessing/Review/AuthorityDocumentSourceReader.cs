using DocxHeaderExtractor.DocumentProcessing.Review;
using DocxHeaderExtractor.DocumentProcessing.Features;
using DocxHeaderExtractor.DocumentProcessing.Policy;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.DocumentProcessing.Review;

/// <summary>
/// Explicit evaluation boundary. The compatibility projection is consumed here once to produce
/// source facts and the frozen candidate-index view; evaluator code never receives Slim types.
/// </summary>
public sealed class AuthorityDocumentSourceReader : IDocumentSourceReader
{
    private readonly PipelineOptions _pipelineOptions;
    private readonly ExtractionOptions _options;

    public AuthorityDocumentSourceReader(PipelineOptions? options = null)
    {
        _pipelineOptions = options ?? new PipelineOptions { DisableLlm = true };
        _options = _pipelineOptions.Extraction;
    }

    public AuthorityDocumentSourceReader(ExtractionOptions options)
    {
        _pipelineOptions = new PipelineOptions { DisableLlm = true, Extraction = options };
        _options = options;
    }

    public DocumentSourceSnapshot Read(string inputPath)
    {
        var conversion = LegacyDocConverter.EnsureDocx(inputPath);
        try
        {
            var source = new OpenXmlDocumentSource(_options).Read(conversion.Path);
            var features = NumberingStyleFeatures.FromSourceDocument(source);
            var derived = new DocumentFeatureDeriver().Derive(source);
            var policyState = DocxPolicyStateBuilder.Build(source, features, derived, _options);
            var candidates = _pipelineOptions.DisableLlm || !_pipelineOptions.ReviewAllParagraphs
                ? policyState.Paragraphs.Where(p => p.Role is ParagraphRole.StyledHeading or ParagraphRole.HeadingCandidate)
                    .Select(p => p.Index).ToArray()
                : policyState.Paragraphs
                    .Where(p => p.Role != ParagraphRole.Empty)
                    .Select(p => p.Index)
                    .ToArray();
            return new DocumentSourceSnapshot(source, candidates);
        }
        finally
        {
            LegacyDocConverter.Cleanup(conversion);
        }
    }
}
