using DocxHeaderExtractor.Contracts.Models;
using DocxHeaderExtractor.Contracts.Models.Inference;
using DocxHeaderExtractor.DocumentProcessing.Projection;
using DocxHeaderExtractor.DocumentProcessing.Semantics.HeadingAuthority;
using DocxHeaderExtractor.Sources.Common;
using DocxHeaderExtractor.Contracts.Authority;
using DocxHeaderExtractor.DocumentProcessing.Authority;

namespace DocxHeaderExtractor.DocumentProcessing.Materialization;

/// <summary>Which occurrences of a repeated heading reach the canonical structure.</summary>
internal enum PrimaryOccurrenceSelection
{
    /// <summary>Every validated source occurrence; the authority defines no semantic node identity.</summary>
    EverySourceOccurrence,

    /// <summary>Only the first occurrence of each semantic node the authority declared.</summary>
    FirstOfSemanticNode,
}

/// <summary>Validated headings, their resolved placement and the canonical structure built from them.</summary>
internal sealed record HeadingStructureAssembly(
    IReadOnlyList<ValidatedHeading> Validated,
    IReadOnlyDictionary<string, ResolvedHeadingHierarchy> Hierarchies,
    ValidatedStructure Structure,
    HeadingProjectionContext ProjectionContext)
{
    public HeadingPlacementExecutionObservation? PlacementObservation { get; init; }
}

/// <summary>
/// The shared half of a heading route: bind and validate the authority's extents, place unresolved
/// headings, derive hierarchy and materialize canonical structure. It reads only the common
/// occurrence universe, so it cannot tell which format or which authority produced its input.
/// </summary>
internal static class HeadingStructureAssembler
{
    public static async Task<HeadingStructureAssembly> AssembleAsync(
        DocumentSourceSnapshot source,
        HeadingAuthorityResult authority,
        PrimaryOccurrenceSelection primaryOccurrences,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(authority);

        var validated = HeadingDecisionBinder.BindAndValidate(source.OccurrenceContexts, authority.Decisions);
        // Only validated headings are placed. A heading that fails validation never becomes an element, so a level or a parent taken from it
        // would have no element to stand on: its child would show a depth with no parent, and the relation would be dropped without a trace.
        var validatedIds = validated.Select(item => item.SourceId).ToHashSet(StringComparer.Ordinal);
        var bound = authority.BoundHeadings.Where(item => validatedIds.Contains(item.SourceId)).ToArray();
        // Selector routes carry source context and upstream gaps into a bounded parent-proposal
        // pass, followed only by deterministic graph validation. Other authorities keep their
        // existing narrow placement contract. Unresolved ancestry remains a legitimate outcome.
        var placement = authority.PlacementTransport is null
            ? HeadingParentResolver.Observe(bound,
                HeadingHierarchyResolver.DeriveHierarchyFromModelRelations(bound)
                    .Where(item => item.Resolution == HeadingHierarchyResolver.Unresolved)
                    .Select(item => item.SourceId).ToHashSet(StringComparer.Ordinal), "placement-not-requested")
            : await HeadingParentResolver.PlaceWithObservationAsync(
                bound, authority.PlacementTransport, cancellationToken, authority.PlacementEvidence).ConfigureAwait(false);
        var placed = placement.Headings;
        // The alias catalog spans the whole document while contexts hold only the occurrences the
        // route carries, so a bound heading can name a source this route cannot materialize.
        var derived = HeadingHierarchyResolver
            .DeriveHierarchyFromModelRelations(placed)
            .Where(item => source.OccurrenceContexts.ContainsKey(item.SourceId))
            .ToArray();
        var placements = derived.ToDictionary(
            item => item.SourceId,
            item => new ResolvedHeadingHierarchy(
                item.SourceId, item.Level, item.ParentSourceId, item.Resolution, "requires_review")
            {
                StructuralScope = source.OccurrenceContexts[item.SourceId].StructuralScope,
            },
            StringComparer.Ordinal);
        var primarySourceIds = primaryOccurrences == PrimaryOccurrenceSelection.FirstOfSemanticNode
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
        var structure = HeadingStructureMaterializer.Materialize(
            validated, placements, occurrences, source.SourceKind, StructuralDecisionOrigin.Model, primarySourceIds);
        return new HeadingStructureAssembly(validated, placements, structure.Structure, structure.ProjectionContext)
        { PlacementObservation = placement.Observation };
    }
}
