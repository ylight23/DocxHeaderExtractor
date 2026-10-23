using DocxHeaderExtractor.Contracts.Models;

namespace DocxHeaderExtractor.DocumentProcessing.Problems;

/// <summary>What every problem's run reports, whatever it produced: the document, the problem and the agent contract, the model it used
/// (null when the model was disabled), whether the document left this machine, how long the run took, and what of the source could not
/// be read (see ProblemSource.Gaps).</summary>
public sealed record RunEnvelope(
    DocumentIdentity Identity,
    string Problem,
    string Contract,
    string? Model,
    bool SentDataExternally,
    long ElapsedMs)
{
    public IReadOnlyList<string> SourceGaps { get; init; } = [];
}
