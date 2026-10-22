using DocxHeaderExtractor.Contracts.Models;
using DocxHeaderExtractor.DocumentProcessing.Output.Outline;
using DocxHeaderExtractor.DocumentProcessing.Output.Product;
using DocxHeaderExtractor.DocumentProcessing.Binding;
using DocxHeaderExtractor.DocumentProcessing.Selection;
using DocxHeaderExtractor.Sources.Snapshot;
using DocxHeaderExtractor.DocumentProcessing.Audit;
using DocxHeaderExtractor.DocumentProcessing.Output;
using DocxHeaderExtractor.DocumentProcessing.Hierarchy;
using DocxHeaderExtractor.DocumentProcessing.Placement;

namespace DocxHeaderExtractor.DocumentProcessing.Structure;

/// <summary>
/// The shared half of a heading route: bind and validate the authority's extents, place unresolved
/// headings, derive hierarchy and materialize canonical structure. It reads only the common
/// occurrence universe, so it cannot tell which format or which authority produced its input.
/// </summary>
internal static class StructureAssembler
{
    public static async Task<StructureAssembly> AssembleAsync(
        DocumentSourceSnapshot source,
        SelectionResult authority,
        PrimaryOccurrences primaryOccurrences,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(authority);

        var validated = DecisionBinder.BindAndValidate(source.OccurrenceContexts, authority.Decisions);
        // Only validated headings are placed. A heading that fails validation never becomes an element, so a level or a parent taken from it
        // would have no element to stand on: its child would show a depth with no parent, and the relation would be dropped without a trace.
        var validatedIds = validated.Select(item => item.SourceId).ToHashSet(StringComparer.Ordinal);
        var bound = authority.BoundHeadings.Where(item => validatedIds.Contains(item.SourceId)).ToArray();
        // The placer proposes parents from the source context and the upstream gaps; ParentResolver keeps only what the graph allows.
        // With the model disabled there is no access, and placement is not requested. Unresolved ancestry remains a legitimate outcome.
        var placement = authority.PlacementAccess is null || authority.Placer is null
            ? ParentResolver.Observe(bound,
                HierarchyResolver.DeriveHierarchyFromModelRelations(bound)
                    .Where(item => item.Resolution == HierarchyResolver.Unresolved)
                    .Select(item => item.SourceId).ToHashSet(StringComparer.Ordinal), "placement-not-requested")
            : await ParentResolver.PlaceAsync(bound, authority.Placer, authority.PlacementAccess,
                authority.PlacementEvidence ?? throw new InvalidOperationException("PLACEMENT_EVIDENCE_REQUIRED"), cancellationToken).ConfigureAwait(false);
        var placed = placement.Headings;
        // The alias catalog spans the whole document while contexts hold only the occurrences the
        // route carries, so a bound heading can name a source this route cannot materialize.
        var derived = HierarchyResolver
            .DeriveHierarchyFromModelRelations(placed)
            .Where(item => source.OccurrenceContexts.ContainsKey(item.SourceId))
            .ToArray();
        var placements = derived.ToDictionary(
            item => item.SourceId,
            item => new ResolvedHierarchy(
                item.SourceId, item.Level, item.ParentSourceId, item.Resolution, "requires_review")
            {
                StructuralScope = source.OccurrenceContexts[item.SourceId].StructuralScope,
            },
            StringComparer.Ordinal);
        var primarySourceIds = primaryOccurrences == PrimaryOccurrences.FirstOfSemanticNode
            ? derived.Where(item => item.IsPrimaryOccurrence).Select(item => item.SourceId).ToHashSet(StringComparer.Ordinal)
            : placements.Keys.ToHashSet(StringComparer.Ordinal);

        var styleBySourceId = source.Occurrences.ToDictionary(item => item.Id, item => item.StyleId, StringComparer.Ordinal);
        var occurrences = source.OccurrenceContexts.ToDictionary(
            pair => pair.Key,
            pair => new StructureSourceOccurrence(
                pair.Key,
                source.OrdinalBySourceId.GetValueOrDefault(pair.Key),
                pair.Value.RawText,
                styleBySourceId.GetValueOrDefault(pair.Key),
                source.SourceKind,
                $"{source.SourceKind}-source-pointer-span"),
            StringComparer.Ordinal);
        // Every validated heading that reaches here is a bound model claim.
        var structure = Materializer.Materialize(
            validated, placements, occurrences, source.SourceKind, StructuralDecisionOrigin.Model, primarySourceIds);
        return new StructureAssembly(validated, placements, structure.Structure, structure.ProjectionContext)
        { PlacementObservation = placement.Observation };
    }
}
