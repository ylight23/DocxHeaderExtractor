using DocxHeaderExtractor.Core.OpenXmlLayer;

namespace DocxHeaderExtractor.Core.Application.Policy;

/// <summary>
/// Application owner for initial candidate classification. The legacy heuristic implementation is
/// deliberately delegated unchanged during ARCH-4E1; demotion/post-processing remain separate.
/// </summary>
public sealed class HeadingCandidatePolicy : IHeadingCandidatePolicy
{
    public CandidateDecision Apply(CandidatePolicyInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Paragraph);
        ArgumentNullException.ThrowIfNull(input.DocumentFeatures);
        ArgumentNullException.ThrowIfNull(input.Options);

        HeadingHeuristics.Classify(input.Paragraph, input.Options, input.TrustStyleSelection);
        return new CandidateDecision(
            input.Paragraph.IsCandidate,
            input.Paragraph.Score,
            input.Paragraph.Role,
            input.Paragraph.GuessedLevel);
    }
}
