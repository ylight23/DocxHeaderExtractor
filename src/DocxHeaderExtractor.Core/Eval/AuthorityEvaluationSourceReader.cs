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
            var extraction = new DocxSlimExtractor(_options).ExtractForAuthority(conversion.Path);
            var compatibility = extraction.Compatibility;
            var candidates = _pipelineOptions.DisableLlm || !_pipelineOptions.ReviewAllParagraphs
                ? compatibility.Paragraphs.Values.Where(p => p.Role is ParagraphRole.StyledHeading or ParagraphRole.HeadingCandidate)
                    .Select(p => p.Index).ToArray()
                : compatibility.Paragraphs.Values
                    .Where(p => p.Role != ParagraphRole.Empty)
                    .Select(p => p.Index)
                    .ToArray();
            return new EvaluationSourceSnapshot(extraction.Source, candidates);
        }
        finally
        {
            LegacyDocConverter.Cleanup(conversion);
        }
    }
}
