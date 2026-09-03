using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;

namespace DocxHeaderExtractor.DocumentProcessing.Review;

public sealed record DocumentSourceSnapshot(
    SourceDocument Document,
    IReadOnlyList<int> CandidateIndexes);

public interface IDocumentSourceReader
{
    DocumentSourceSnapshot Read(string inputPath);
}
