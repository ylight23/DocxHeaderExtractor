using DocxHeaderExtractor.Core.Application.Features;
using DocxHeaderExtractor.Core.Application.Policy;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Core.Eval;

/// <summary>
/// Explicit evaluation boundary. The compatibility projection is consumed here once to produce
/// source facts and the frozen candidate-index view; evaluator code never receives Slim types.
/// </summary>
public sealed class AuthorityEvaluationSourceReader : IEvaluationSourceReader
{
    private readonly PipelineOptions _pipelineOptions;
    private readonly ExtractionOptions _options;

    public AuthorityEvaluationSourceReader(PipelineOptions? options = null)
    {
        _pipelineOptions = options ?? new PipelineOptions { DisableLlm = true };
        _options = _pipelineOptions.Extraction;
    }

    public AuthorityEvaluationSourceReader(ExtractionOptions options)
    {
        _pipelineOptions = new PipelineOptions { DisableLlm = true, Extraction = options };
        _options = options;
    }

    public EvaluationSourceSnapshot Read(string inputPath)
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
            return new EvaluationSourceSnapshot(source, candidates);
        }
        finally
        {
            LegacyDocConverter.Cleanup(conversion);
        }
    }
}
