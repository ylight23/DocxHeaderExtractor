using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Projection;
using DocxHeaderExtractor.DocumentProcessing.Semantics.HeadingAuthority;
using DocxHeaderExtractor.DocumentProcessing.Source.Common;

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
    HeadingProjectionContext ProjectionContext);

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
        // Reasoning surface #3: headings the first pass left unplaced come back in one narrow
        // follow-up that asks only about position. Bounded to a single round; an unresolved heading
        // is a legitimate outcome.
        var placed = authority.PlacementTransport is null
            ? authority.BoundHeadings
            : await HeadingParentResolver.PlaceUnresolvedHeadingsAsync(
                authority.BoundHeadings, authority.PlacementTransport, cancellationToken).ConfigureAwait(false);
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
        return new HeadingStructureAssembly(validated, placements, structure.Structure, structure.ProjectionContext);
    }
}
