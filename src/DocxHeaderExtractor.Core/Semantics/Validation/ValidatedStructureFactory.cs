using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Semantics.Validation;

/// <summary>
/// Admits heading elements and explicit relation proposals to the authority graph.
/// Incoming element ParentId values are never authority; they are replaced by the graph view.
/// </summary>
public static class ValidatedStructureFactory
{
    public static ValidatedStructure Create(
        IEnumerable<ValidatedStructuralElement> elements,
        IEnumerable<StructuralRelationProposal>? relationProposals = null)
    {
        ArgumentNullException.ThrowIfNull(elements);
        var materialized = elements.ToArray();
        foreach (var element in materialized)
        {
            if (element.Type != StructuralElementType.Heading)
                throw new InvalidOperationException("unsupported-structural-type");
            if (element.Role != ProposedRole.HeadingTopic)
                throw new InvalidOperationException("incompatible-structural-role");
        }
        var ids = materialized.Select(element => element.Id).ToHashSet(StringComparer.Ordinal);
        if (ids.Count != materialized.Length)
            throw new InvalidOperationException("duplicate-structural-element-id");
        var relations = StructuralRelationProposalValidator.Materialize(ids, relationProposals ?? []);
        var parentByChild = relations.ToDictionary(
            relation => relation.ToId, relation => relation.FromId, StringComparer.Ordinal);
        var projected = materialized.Select(element => element with
        {
            ParentId = parentByChild.GetValueOrDefault(element.Id),
        }).ToArray();
        return new ValidatedStructure(Array.AsReadOnly(projected), Array.AsReadOnly(relations.ToArray()));
    }
}
