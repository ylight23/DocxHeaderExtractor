namespace DocxHeaderExtractor.DocumentProcessing.Policy;

public interface IHeadingCandidatePolicy
{
    CandidateDecision Apply(CandidatePolicyInput input);
}
