using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Semantics.Validation;

/// <summary>
/// Validates relation proposals before they enter the structural authority graph. This contract is
/// intentionally open to additional relation types without making ParentId a second authority.
/// </summary>
public static class StructuralRelationProposalValidator
{
    public static StructuralRelationValidation Validate(
        StructuralRelationProposal proposal,
        IReadOnlySet<string> knownStructuralElementIds,
        IReadOnlyDictionary<string, StructuralElementType>? structuralElementTypes = null)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(knownStructuralElementIds);
        var endpointsPresent = knownStructuralElementIds.Contains(proposal.FromId) &&
            knownStructuralElementIds.Contains(proposal.ToId);
        var distinctEndpoints = !string.Equals(proposal.FromId, proposal.ToId, StringComparison.Ordinal);
        var typeValid = Enum.IsDefined(proposal.Type);
        var semanticValid = structuralElementTypes is null || !endpointsPresent ||
            IsSemanticallyCompatible(proposal, structuralElementTypes);
        var reason = !endpointsPresent ? "relation-endpoint-not-grounded" :
            !distinctEndpoints ? "relation-self-reference" :
            !typeValid ? "relation-type-unsupported" :
            !semanticValid ? "relation-type-incompatible" : null;
        return new StructuralRelationValidation(endpointsPresent, distinctEndpoints, typeValid, reason);
    }

    public static IReadOnlyList<StructuralRelation> Materialize(
        IReadOnlySet<string> knownStructuralElementIds,
        IEnumerable<StructuralRelationProposal> proposals,
        IReadOnlyDictionary<string, StructuralElementType>? structuralElementTypes = null)
    {
        ArgumentNullException.ThrowIfNull(knownStructuralElementIds);
        ArgumentNullException.ThrowIfNull(proposals);
        var relations = new List<StructuralRelation>();
        var seen = new HashSet<(string FromId, string ToId, StructuralRelationType Type)>();
        var parentByChild = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var proposal in proposals)
        {
            var validation = Validate(proposal, knownStructuralElementIds, structuralElementTypes);
            if (!validation.Accepted)
                throw new InvalidOperationException(validation.RejectionReason);

            if (proposal.Type == StructuralRelationType.ParentChild &&
                parentByChild.TryGetValue(proposal.ToId, out var existingParent) &&
                !string.Equals(existingParent, proposal.FromId, StringComparison.Ordinal))
                throw new InvalidOperationException("multiple-parent-relations");

            parentByChild[proposal.ToId] = proposal.FromId;
            if (seen.Add((proposal.FromId, proposal.ToId, proposal.Type)))
                relations.Add(new StructuralRelation(proposal.FromId, proposal.ToId, proposal.Type));
        }
        return relations;
    }

    private static bool IsSemanticallyCompatible(
        StructuralRelationProposal proposal,
        IReadOnlyDictionary<string, StructuralElementType> structuralElementTypes)
    {
        if (!structuralElementTypes.TryGetValue(proposal.FromId, out var from) ||
            !structuralElementTypes.TryGetValue(proposal.ToId, out var to))
            return false;

        return proposal.Type switch
        {
            StructuralRelationType.ParentChild => true,
            StructuralRelationType.CaptionOf => from == StructuralElementType.Caption &&
                to == StructuralElementType.Figure,
            StructuralRelationType.Labels =>
                (from == StructuralElementType.FigureTitle && to == StructuralElementType.Figure) ||
                (from == StructuralElementType.TableTitle && to == StructuralElementType.Table),
            _ => false,
        };
    }
}
