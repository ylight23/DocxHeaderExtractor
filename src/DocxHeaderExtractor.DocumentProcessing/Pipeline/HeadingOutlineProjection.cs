using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// Outline projection from generic structural authority to the existing heading output.
/// It performs no source selection, matching, validation, or hierarchy inference.
/// </summary>
public static class HeadingOutlineProjection
{
    public static DocumentOutline Project(
        DocumentOutline template,
        ValidatedStructure structure)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(structure);
        return new DocumentOutline
        {
            File = template.File,
            ParagraphCount = template.ParagraphCount,
            SourceCount = template.SourceCount,
            Headings = Project(structure),
            ElapsedMs = template.ElapsedMs,
            Model = template.Model,
            DeterministicRoute = template.DeterministicRoute,
            RouteAudit = template.RouteAudit,
            Provenance = template.Provenance,
            ProductOutput = template.ProductOutput,
            Outcome = template.Outcome,
        };
    }

    public static IReadOnlyList<HeadingRecord> Project(ValidatedStructure structure)
        => Project(structure, null);

    public static IReadOnlyList<HeadingRecord> Project(
        ValidatedStructure structure,
        IReadOnlySet<string>? emittedElementIds)
    {
        ArgumentNullException.ThrowIfNull(structure);
        return structure.OutlineElements
            .Where(element => emittedElementIds is null || emittedElementIds.Contains(element.Id))
            // ValidatedStructure.Elements already carries the producer's canonical order. Sorting
            // by source ordinal here loses distinct PDF occurrences that share one paragraph.
            .Select(ProjectHeading)
            .ToArray();
    }

    private static HeadingRecord ProjectHeading(ValidatedStructuralElement element)
    {
        var source = element.Sources.FirstOrDefault();
        if (source is null)
            throw new InvalidOperationException($"Structural element '{element.Id}' has no validated source.");
        var metadata = element.ProjectionMetadata;

        return new HeadingRecord
        {
            Index = metadata?.OutlineSourceOrdinal ?? source.SourceOrdinal,
            StableId = metadata?.OutlineStableId ?? source.StableId ?? source.SourceId,
            SourceId = metadata?.OutlineSourceId ?? source.SourceId,
            Level = metadata?.OutlineLevelIsSet == true ? metadata.OutlineLevel : element.Level,
            Text = metadata?.OutlineText ?? element.Text,
            OriginalText = metadata?.OriginalText,
            HeadingSpan = metadata?.OutlineHeadingSpan is { } outlineSpan
                ? new TextOffsetSpan(outlineSpan.Start, outlineSpan.End)
                : new TextOffsetSpan(source.Span.Start, source.Span.End),
            InlineBody = metadata?.InlineBody,
            InlineBodySpan = metadata?.InlineBodySpan is { } bodySpan
                ? new TextOffsetSpan(bodySpan.Start, bodySpan.End)
                : null,
            HierarchyResolution = metadata?.HierarchyResolution,
            BoundarySource = metadata?.BoundarySource,
            StyleId = metadata?.StyleId,
            Source = ParseSource(element.Decision.Origin),
            Confidence = element.Decision.Confidence,
            DecisionStatus = ParseStatus(element.Decision.Status),
            ConfidenceBasis = element.Decision.ConfidenceBasis,
            Disputed = element.Decision.Disputed,
        };
    }

    private static HeadingSource ParseSource(string value) => value.ToLowerInvariant() switch
    {
        "style" => HeadingSource.Style,
        "model" => HeadingSource.Model,
        "structure" => HeadingSource.Structure,
        "human" or "humancorrection" => HeadingSource.HumanCorrection,
        _ => HeadingSource.LocalRules,
    };

    private static HeadingDecisionStatus ParseStatus(string value) => value.ToLowerInvariant() switch
    {
        "autoacceptedevidence" => HeadingDecisionStatus.AutoAcceptedEvidence,
        "humanverified" => HeadingDecisionStatus.HumanVerified,
        _ => HeadingDecisionStatus.RequiresReview,
    };
}
