using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

public static class ReasoningHardInvariantValidator
{
    public static IReadOnlyList<string> Validate(
        ReasoningHeadingProposal proposal,
        IReadOnlyDictionary<string, string> sourceTextById)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(sourceTextById);
        var issues = new List<string>();
        if (!sourceTextById.TryGetValue(proposal.SourceId, out var text))
            issues.Add("source-id-not-found");
        else
        {
            if (!proposal.HeadingSpan.IsValidFor(text))
                issues.Add("source-span-invalid");
            else if (!string.Equals(
                text[proposal.HeadingSpan.Start..proposal.HeadingSpan.End],
                proposal.Text,
                StringComparison.Ordinal))
                issues.Add("claimed-text-does-not-match-source");
        }
        if (proposal.ProposedLevel is < 1 or > 9)
            issues.Add("level-out-of-range");
        return issues;
    }

    public static IReadOnlyList<string> ValidateStructure(ValidatedStructure structure)
    {
        ArgumentNullException.ThrowIfNull(structure);
        var issues = new List<string>();
        var ids = structure.Elements.Select(element => element.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var relation in structure.Relations)
        {
            if (!ids.Contains(relation.FromId) || !ids.Contains(relation.ToId))
                issues.Add("relation-endpoint-missing");
            if (string.Equals(relation.FromId, relation.ToId, StringComparison.Ordinal))
                issues.Add("relation-self-reference");
        }

        var parentByChild = structure.Relations
            .Where(relation => relation.Type == StructuralRelationType.ParentChild)
            .ToDictionary(relation => relation.ToId, relation => relation.FromId, StringComparer.Ordinal);
        foreach (var id in ids)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var current = id;
            while (parentByChild.TryGetValue(current, out var parent))
            {
                if (!seen.Add(current))
                {
                    issues.Add("parent-cycle");
                    break;
                }
                current = parent;
            }
        }
        return issues.Distinct(StringComparer.Ordinal).ToArray();
    }
}

public static class ReasoningTaskProjection
{
    public static IReadOnlyList<ValidatedStructuralElement> ProjectContentHeadings(
        ValidatedStructure structure) =>
        structure.Elements
            .Where(element => element.Type is StructuralElementType.Title or StructuralElementType.Heading)
            .Where(element => element.Role is not ProposedRole.LocalSubheading)
            .ToArray();
}
