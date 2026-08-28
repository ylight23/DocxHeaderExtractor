using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Application.Policy;

/// <summary>Pure policy remainder extracted from the former Slim PostProcess method.</summary>
public sealed class PostClassificationPolicy : IPostClassificationPolicy
{
    public PostClassificationDecision Decide(PostClassificationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Source);
        ArgumentNullException.ThrowIfNull(input.Candidate);
        ArgumentNullException.ThrowIfNull(input.TocFeatures);

        var role = input.Candidate.Role;
        var score = input.Candidate.Score;

        if (input.TocFeatures.PrecedesTableOfContents(input.Source.SourceId) &&
            role is ParagraphRole.Normal or ParagraphRole.HeadingCandidate)
        {
            role = ParagraphRole.HeadingCandidate;
            score = Math.Max(score, 0.80);
        }

        if (role != ParagraphRole.HeadingCandidate)
            return new PostClassificationDecision(role, score, input.Candidate.GuessedLevel);

        if (input.NextNonEmptyText is { Length: > 200 })
            score = Math.Min(1, score + 0.10);

        if (input.PreviousNonEmptyRole is ParagraphRole.StyledHeading)
            score = Math.Min(1, score + 0.05);

        return new PostClassificationDecision(role, score, input.Candidate.GuessedLevel);
    }
}
