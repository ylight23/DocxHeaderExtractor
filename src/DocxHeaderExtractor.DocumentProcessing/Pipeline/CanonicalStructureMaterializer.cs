using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// What structural materialization needs from a source occurrence, in any format. A DOCX paragraph
/// and a PDF text block both reduce to this.
/// </summary>
internal sealed record CanonicalSourceOccurrence(
    string SourceId,
    int SourceOrdinal,
    string Text,
    string? StyleId);

/// <summary>
/// Builds canonical structure from validated headings, for any source format.
/// <para>
/// It reads only what every format can supply: an identity, a position in reading order, the exact
/// text, and an optional style name. It lived in DocxAuthorityPipeline while the PDF lane called
/// it, which read as though PDF structure were a DOCX by-product; it is neither lane's property.
/// </para>
/// <para>
/// Shared on purpose. A PDF copy of this would be free to drift from the DOCX one on level
/// derivation, parent wiring and emission - the three things this pipeline has spent its history
/// getting right - and the drift would surface only as an unexplainable gap between a document and
/// the same document in the other format.
/// </para>
/// </summary>
internal static class CanonicalStructureMaterializer
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
    internal static ValidatedStructure Materialize(
        IReadOnlyList<PdfValidatedHeading> validated,
        IReadOnlyDictionary<string, PdfValidatedStructure> structures,
        IReadOnlyDictionary<string, CanonicalSourceOccurrence> occurrences,
        string routeKey,
        IReadOnlySet<string>? primarySourceIds = null)
    {
        ArgumentNullException.ThrowIfNull(validated);
        ArgumentNullException.ThrowIfNull(structures);
        ArgumentNullException.ThrowIfNull(occurrences);
        ArgumentException.ThrowIfNullOrWhiteSpace(routeKey);

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

        foreach (var item in selected)
        {
            var sourceParagraph = occurrences[item.SourceId];
            var hierarchy = structures[item.SourceId];
            var sourceFacts = new SourceFacts
            {
                SourceId = sourceParagraph.SourceId,
                RawText = sourceParagraph.Text,
                Source = new SourceAnchor
                {
                    SourceType = "docx",
                    ParagraphId = sourceParagraph.SourceId,
                    ParagraphIndex = sourceParagraph.SourceOrdinal,
                },
                RawSpan = new SourceTextSpan(0, sourceParagraph.Text.Length),
            };
            var candidate = new StructuralCandidate
            {
                CandidateId = item.SourceId,
                ObservedSourceFacts = [sourceFacts],
            };
            // Two different states both end without a level, and the reason is kept because they
            // mean opposite things to a reviewer: "unresolved" is an absence of judgement and needs
            // one, "out-of-hierarchy" IS the judgement - a title or running header is a heading
            // that simply holds no position in the section tree. Neither may default to level 1,
            // which would assert "top level" without evidence.
            var placed = hierarchy.ParentResolution is
                ModelRelationHierarchyResolver.ResolvedParent or ModelRelationHierarchyResolver.ResolvedRoot;
            var derivedLevel = placed ? hierarchy.Level : (int?)null;
            var proposal = new StructuralProposal
            {
                CandidateId = item.SourceId,
                Type = StructuralElementType.Heading,
                Role = ProposedRole.HeadingTopic,
                ProposedSources =
                [
                    new ProposedSourceReference(item.SourceId,
                        new StructuralSpan(item.HeadingSpan.Start, item.HeadingSpan.End)),
                ],
                ProposedParentId = hierarchy.ParentId is { } parent
                    ? elementIdBySourceId.GetValueOrDefault(parent)
                    : null,
                ProposedLevel = derivedLevel,
            };
            var decision = new StructuralDecision(
                "structure", nameof(HeadingDecisionStatus.RequiresReview), 0,
                "docx-authority-validated-review");
            var element = StructuralProposalValidator.Materialize(
                candidate, proposal, elementIdBySourceId[item.SourceId], decision,
                elementIdBySourceId.Values.ToHashSet(StringComparer.Ordinal),
                new StructuralProjectionMetadata
                {
                    CompatibilitySourceId = sourceParagraph.SourceId,
                    // Declaring the level "set" while leaving it null made the projection prefer
                    // that null over the materialized element level, so this route emitted headings
                    // with no level at all whatever the resolver decided.
                    CompatibilityLevelIsSet = true,
                    CompatibilityLevel = derivedLevel,
                    HierarchyResolution = hierarchy.ParentResolution,
                    OriginalText = sourceParagraph.Text,
                    BoundarySource = "docx-source-pointer-span",
                    StyleId = sourceParagraph.StyleId,
                });
            if (element is null)
                throw new InvalidOperationException($"Validated DOCX heading '{item.SourceId}' failed generic materialization.");

            elements.Add(element with
            {
                Sources = element.Sources.Select(source => source with { StableId = sourceParagraph.SourceId }).ToArray(),
            });
        }

        var relationProposals = elements
            .Where(element => element.ParentId is not null)
            .Select(element => new StructuralRelationProposal(
                element.ParentId!, element.Id, StructuralRelationType.ParentChild));
        return ValidatedStructure.FromElements(elements, relationProposals);
    }
}

/// <summary>
/// Canonical route transport for the materialized structure and the source elements it emitted.
/// The old structural materializer implementation was removed; this shared result remains live
/// because the normal authority pipeline uses it to carry canonical materialization output.
/// </summary>
public sealed record StructuralMaterializationResult(
    ValidatedStructure Structure,
    IReadOnlySet<string> EmittedElementIds,
    int UnjoinedSourceCount,
    int UnjoinedParentCount);
