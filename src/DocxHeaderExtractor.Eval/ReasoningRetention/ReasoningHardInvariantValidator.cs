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
            else if (!string.IsNullOrEmpty(proposal.Text) && !string.Equals(
                text[proposal.HeadingSpan.Start..proposal.HeadingSpan.End],
                proposal.Text,
                StringComparison.Ordinal))
                issues.Add("claimed-text-does-not-match-source");
        }
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
    public const string Included = "INCLUDED";
    public const string Excluded = "EXCLUDED";

    public static IReadOnlyList<ReasoningProjectionDecision> Project(ValidatedStructure structure)
    {
        ArgumentNullException.ThrowIfNull(structure);
        return structure.Elements
            .OrderBy(element => element.Sources.FirstOrDefault()?.SourceOrdinal ?? int.MaxValue)
            .ThenBy(element => element.Sources.FirstOrDefault()?.Span.Start ?? int.MaxValue)
            .ThenBy(element => element.Id, StringComparer.Ordinal)
            .Select(element =>
            {
                var reason = ExclusionReason(element);
                return new ReasoningProjectionDecision(
                    element.Id,
                    reason is null ? Included : Excluded,
                    reason);
            })
            .ToArray();
    }

    public static IReadOnlyList<ValidatedStructuralElement> ProjectContentHeadings(
        ValidatedStructure structure) =>
        Project(structure)
            .Where(item => item.Status == Included)
            .Join(structure.Elements, item => item.ProposalId, element => element.Id, (_, element) => element)
            .ToArray();

    private static string? ExclusionReason(ValidatedStructuralElement element)
    {
        if (element.Type is not (StructuralElementType.Title or StructuralElementType.Heading))
            return element.Role switch
            {
                ProposedRole.LocalSubheading => "NAVIGATION_ONLY",
                ProposedRole.Metadata => "FRONT_MATTER",
                _ => "TASK_ROLE_EXCLUDED",
            };
        return element.Role switch
        {
            ProposedRole.LocalSubheading => "NAVIGATION_ONLY",
            ProposedRole.Metadata => "FRONT_MATTER",
            _ => null,
        };
    }
}

public sealed record ReasoningProjectionDecision(
    [property: System.Text.Json.Serialization.JsonPropertyName("proposalId")] string ProposalId,
    [property: System.Text.Json.Serialization.JsonPropertyName("projectionStatus")] string Status,
    [property: System.Text.Json.Serialization.JsonPropertyName("projectionReason")] string? Reason);
