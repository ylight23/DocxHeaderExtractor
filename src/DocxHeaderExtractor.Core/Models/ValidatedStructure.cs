using DocxHeaderExtractor.Core.Semantics.Validation;
using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Core.Models;

/// <summary>
/// The heading authority graph. It retains exact source selections and validated parent relations.
/// </summary>
public sealed class ValidatedStructure
{
    public ValidatedStructure(
        IReadOnlyList<ValidatedStructuralElement> elements,
        IReadOnlyList<StructuralRelation>? relations = null)
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
        var proposedRelations = (relations ?? [])
            .Select(relation => new StructuralRelationProposal(relation.FromId, relation.ToId, relation.Type));
        Relations = StructuralRelationProposalValidator.Materialize(ids, proposedRelations);
        var parentByChild = Relations
            .Where(relation => relation.Type == StructuralRelationType.ParentChild)
            .ToDictionary(relation => relation.ToId, relation => relation.FromId, StringComparer.Ordinal);
        // ParentId is an outline view. It is always projected from the validated graph.
        Elements = materialized.Select(element => element with
        {
            ParentId = parentByChild.GetValueOrDefault(element.Id),
        }).ToArray();
    }

    [JsonPropertyName("elements")]
    public IReadOnlyList<ValidatedStructuralElement> Elements { get; }

    [JsonPropertyName("relations")]
    public IReadOnlyList<StructuralRelation> Relations { get; }

    /// <summary>Compatibility view: every admitted element is a heading. Preserves the wire property.</summary>
    public IReadOnlyList<ValidatedStructuralElement> OutlineElements => Elements;

    public static ValidatedStructure FromElements(
        IEnumerable<ValidatedStructuralElement> elements,
        IEnumerable<StructuralRelationProposal>? relationProposals = null)
    {
        var materialized = elements?.ToArray() ?? throw new ArgumentNullException(nameof(elements));
        var ids = materialized.Select(element => element.Id).ToHashSet(StringComparer.Ordinal);
        var proposals = relationProposals?.ToArray() ?? materialized
            .Where(element => element.ParentId is not null)
            .Select(element => new StructuralRelationProposal(
                element.ParentId!, element.Id, StructuralRelationType.ParentChild))
            .ToArray();
        var relations = StructuralRelationProposalValidator.Materialize(ids, proposals);
        return new ValidatedStructure(materialized, relations);
    }
}
