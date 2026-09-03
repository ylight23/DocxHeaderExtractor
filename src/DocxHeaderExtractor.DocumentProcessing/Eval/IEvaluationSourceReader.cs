using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Eval;

public sealed record EvaluationSourceSnapshot(
    SourceDocument Document,
    IReadOnlyList<int> CandidateIndexes);

public interface IEvaluationSourceReader
{
    EvaluationSourceSnapshot Read(string inputPath);
}
