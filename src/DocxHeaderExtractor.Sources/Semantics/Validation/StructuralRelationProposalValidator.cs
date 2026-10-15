using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Semantics.Validation;

/// <summary>
/// Validates parent relations before they enter the heading graph. ParentId remains a projection.
/// </summary>
public static class StructuralRelationProposalValidator
{
    public static StructuralRelationValidation Validate(
        StructuralRelationProposal proposal,
        IReadOnlySet<string> knownStructuralElementIds)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(knownStructuralElementIds);
        var endpointsPresent = knownStructuralElementIds.Contains(proposal.FromId) &&
            knownStructuralElementIds.Contains(proposal.ToId);
        var distinctEndpoints = !string.Equals(proposal.FromId, proposal.ToId, StringComparison.Ordinal);
        var typeValid = Enum.IsDefined(proposal.Type);
        var reason = !endpointsPresent ? "relation-endpoint-not-grounded" :
            !distinctEndpoints ? "relation-self-reference" :
            !typeValid ? "relation-type-unsupported" : null;
        return new StructuralRelationValidation(endpointsPresent, distinctEndpoints, typeValid, reason);
    }

    public static IReadOnlyList<StructuralRelation> Materialize(
        IReadOnlySet<string> knownStructuralElementIds,
        IEnumerable<StructuralRelationProposal> proposals)
    {
        ArgumentNullException.ThrowIfNull(knownStructuralElementIds);
        ArgumentNullException.ThrowIfNull(proposals);
        var relations = new List<StructuralRelation>();
        var seen = new HashSet<(string FromId, string ToId, StructuralRelationType Type)>();
        var parentByChild = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var proposal in proposals)
        {
            var validation = Validate(proposal, knownStructuralElementIds);
            if (!validation.Accepted)
                throw new InvalidOperationException(validation.RejectionReason);

            if (parentByChild.TryGetValue(proposal.ToId, out var existingParent) &&
                !string.Equals(existingParent, proposal.FromId, StringComparison.Ordinal))
                throw new InvalidOperationException("multiple-parent-relations");

            parentByChild[proposal.ToId] = proposal.FromId;
            if (seen.Add((proposal.FromId, proposal.ToId, proposal.Type)))
                relations.Add(new StructuralRelation(proposal.FromId, proposal.ToId, proposal.Type));
        }
        return relations;
    }
}
