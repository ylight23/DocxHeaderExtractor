namespace DocxHeaderExtractor.Contracts.Problems;

/// <summary>
/// The C# half of a problem: checks a proposal against the source and builds the output from what holds. It never calls the model and
/// never adds what the agent did not propose; what it refuses belongs in the output with its reason, not silently dropped.
/// </summary>
public interface IChecker<in TProposal, out TOutput> where TProposal : class where TOutput : class
{
    TOutput Check(ProblemSource source, TProposal proposal);
}
