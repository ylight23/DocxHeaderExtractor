namespace DocxHeaderExtractor.Core.Application.Policy;

public interface IHeadingCandidatePolicy
{
    CandidateDecision Apply(CandidatePolicyInput input);
}
