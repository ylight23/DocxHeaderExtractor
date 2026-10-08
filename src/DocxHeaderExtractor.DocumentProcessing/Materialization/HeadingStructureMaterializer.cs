using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Semantics.Validation;
using DocxHeaderExtractor.DocumentProcessing.Projection;
using DocxHeaderExtractor.DocumentProcessing.Source.Docx;
using DocxHeaderExtractor.DocumentProcessing.Authority;

namespace DocxHeaderExtractor.DocumentProcessing.Materialization;

/// <summary>
/// What structural materialization needs from a source occurrence, in any format. A DOCX paragraph
/// and a PDF text block both reduce to this.
/// </summary>
internal sealed record StructureSourceOccurrence(
    string SourceId,
    int SourceOrdinal,
    string Text,
    string? StyleId,
    string SourceKind = "unknown",
    string BoundarySource = "source-pointer-span");

/// <summary>
/// Builds canonical structure from validated headings, for any source format.
/// <para>
/// It reads only what every format can supply: an identity, a position in reading order, the exact
/// text, and an optional style name. It lived in DocxSourceAdapter while the PDF lane called
/// it, which read as though PDF structure were a DOCX by-product; it is neither lane's property.
/// </para>
/// <para>
/// Shared on purpose. A PDF copy of this would be free to drift from the DOCX one on level
/// derivation, parent wiring and emission - the three things this pipeline has spent its history
/// getting right - and the drift would surface only as an unexplainable gap between a document and
/// the same document in the other format.
/// </para>
/// </summary>
internal static class HeadingStructureMaterializer
{
    /// <summary>
    /// Turns validated headings into canonical structure, reading only what any source format can
    /// supply: an identity, a position in reading order, the exact text, and an optional style name.
    /// <para>
    /// Kept format-neutral on purpose. A PDF lane that duplicated this would be free to drift from
    /// the DOCX lane on level derivation, parent wiring or emission - the three things the whole
    /// hierarchy argument was about - and the drift would only show up as a metric difference
    /// between two formats nobody could explain.
    /// </para>
    /// </summary>
    internal static HeadingStructureMaterialization Materialize(
        IReadOnlyList<ValidatedHeading> validated,
        IReadOnlyDictionary<string, ResolvedHeadingHierarchy> structures,
        IReadOnlyDictionary<string, StructureSourceOccurrence> occurrences,
        string routeKey,
        string origin,
        IReadOnlySet<string>? primarySourceIds = null)
    {
        ArgumentNullException.ThrowIfNull(validated);
        ArgumentNullException.ThrowIfNull(structures);
        ArgumentNullException.ThrowIfNull(occurrences);
        ArgumentException.ThrowIfNullOrWhiteSpace(routeKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);

        // Primary occurrence selection is an upstream identity decision. The materializer only
        // applies that already-decided set at the boundary; it never derives identity or chooses
        // a primary occurrence from text, order, parent, or level.
        var selected = primarySourceIds is null
            ? validated
            : validated.Where(item => primarySourceIds.Contains(item.SourceId)).ToArray();

        var elementIdBySourceId = selected.ToDictionary(
            item => item.SourceId,
            item => $"structural:{routeKey}:{item.SourceId}",
            StringComparer.Ordinal);
        var elements = new List<ValidatedStructuralElement>(selected.Count);
        var relationProposals = new List<StructuralRelationProposal>();
        var projectionMetadata = new Dictionary<string, HeadingProjectionMetadata>(StringComparer.Ordinal);
        var stableIds = new Dictionary<HeadingProjectionSourceKey, string>();

        foreach (var item in selected)
        {
            var sourceParagraph = occurrences[item.SourceId];
            var hierarchy = structures[item.SourceId];
            var sourceFacts = FactsFor(sourceParagraph);
            // A claim that occupies several source occurrences supplies facts for each of them. The
            // structural contract already addresses a claim as a list of sources, so a heading that
            // wraps across two atoms is expressed as the two selections it actually is - there is no
            // span covering both, because between them lies the end of one piece of text and the
            // start of another, and inventing one would be the truncation in reverse.
            var claimFacts = item.Parts is { Count: > 1 } parts
                ? parts.Select(part => FactsFor(occurrences[part.SourceId])).ToArray()
                : [sourceFacts];
            var sourceOccurrence = new StructuralSourceOccurrence
            {
                SourceOccurrenceId = item.SourceId,
                ObservedSourceFacts = claimFacts,
            };
            // Two different states both end without a level, and the reason is kept because they
            // mean opposite things to a reviewer: "unresolved" is an absence of judgement and needs
            // one, "out-of-hierarchy" IS the judgement - a title or running header is a heading
            // that simply holds no position in the section tree. Neither may default to level 1,
            // which would assert "top level" without evidence.
            var placed = hierarchy.ParentResolution is
                HeadingHierarchyResolver.ResolvedParent or HeadingHierarchyResolver.ResolvedRoot;
            var derivedLevel = placed ? hierarchy.Level : (int?)null;
            var proposal = new StructuralProposal
            {
                SourceOccurrenceId = item.SourceId,
                Type = StructuralElementType.Heading,
                Role = ProposedRole.HeadingTopic,
                ProposedSources = item.Parts is { Count: > 1 } claimParts
                    ? claimParts.Select(part => new ProposedSourceReference(
                        part.SourceId, new StructuralSpan(part.Start, part.End))).ToArray()
                    : [
                        new ProposedSourceReference(item.SourceId,
                            new StructuralSpan(item.HeadingSpan.Start, item.HeadingSpan.End)),
                    ],
                ProposedParentId = hierarchy.ParentId is { } parent
                    ? elementIdBySourceId.GetValueOrDefault(parent)
                    : null,
                ProposedLevel = derivedLevel,
            };
            // The producing lane declares the origin and the validator its basis; nothing here
            // relabels them. Every materialized decision still awaits review.
            var decision = new StructuralDecision(
                origin, nameof(HeadingDecisionStatus.RequiresReview), item.ValidationBasis);
            var element = StructuralProposalValidator.Materialize(
                sourceOccurrence, proposal, elementIdBySourceId[item.SourceId], decision,
                elementIdBySourceId.Values.ToHashSet(StringComparer.Ordinal));
            if (element is null)
                throw new InvalidOperationException($"Validated heading '{item.SourceId}' failed canonical materialization.");

            projectionMetadata.Add(element.Id, new HeadingProjectionMetadata
                {
                    OutlineSourceId = sourceParagraph.SourceId,
                    // Declaring the level "set" while leaving it null made the projection prefer
                    // that null over the materialized element level, so this route emitted headings
                    // with no level at all whatever the resolver decided.
                    OutlineLevelIsSet = true,
                    OutlineLevel = derivedLevel,
                    HierarchyResolution = hierarchy.ParentResolution,
                    OriginalText = sourceParagraph.Text,
                    BoundarySource = sourceParagraph.BoundarySource,
                    StyleId = sourceParagraph.StyleId,
                });
            foreach (var source in element.Sources)
                stableIds.Add(new HeadingProjectionSourceKey(element.Id, source.SourceId), sourceParagraph.SourceId);
            elements.Add(element);
            if (proposal.ProposedParentId is { } parentElementId)
                relationProposals.Add(new StructuralRelationProposal(
                    parentElementId, element.Id, StructuralRelationType.ParentChild));
        }

        return new HeadingStructureMaterialization(ValidatedStructureFactory.Create(elements, relationProposals),
            new HeadingProjectionContext(projectionMetadata, stableIds));
    }

    private static SourceFacts FactsFor(StructureSourceOccurrence occurrence) => new()
    {
        SourceId = occurrence.SourceId,
        RawText = occurrence.Text,
        Source = new SourceAnchor
        {
            SourceType = occurrence.SourceKind,
            ParagraphId = occurrence.SourceId,
            ParagraphIndex = occurrence.SourceOrdinal,
        },
        RawSpan = new SourceTextSpan(0, occurrence.Text.Length),
    };
}

/// <summary>Structural authority and a separate projection-only runtime sidecar.</summary>
internal sealed record HeadingStructureMaterialization(
    ValidatedStructure Structure,
    [property: System.Text.Json.Serialization.JsonIgnore] HeadingProjectionContext ProjectionContext);
