using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;

namespace DocxHeaderExtractor.DocumentProcessing.Projection;

/// <summary>
/// Outline projection from validated heading authority to the existing heading output.
/// It performs no source selection, matching, validation, or hierarchy inference.
/// </summary>
public static class HeadingOutlineProjection
{
    public static IReadOnlyList<HeadingRecord> Project(
        ValidatedStructure structure,
        IReadOnlySet<string>? emittedElementIds = null,
        HeadingProjectionContext? projectionContext = null)
        => Project(structure, emittedElementIds, projectionContext, null);

    public static IReadOnlyList<HeadingRecord> Project(
        ValidatedStructure structure,
        IReadOnlySet<string>? emittedElementIds,
        HeadingProjectionContext? projectionContext,
        DocumentSourceCatalog? sourceCatalog)
    {
        ArgumentNullException.ThrowIfNull(structure);
        var units = sourceCatalog?.Units.ToDictionary(unit => unit.SourceId, StringComparer.Ordinal);
        return structure.Elements
            .Where(element => emittedElementIds is null || emittedElementIds.Contains(element.Id))
            // ValidatedStructure.Elements already carries the producer's canonical order. Sorting
            // by source ordinal here loses distinct PDF occurrences that share one paragraph.
            .Select(element => ProjectHeading(element, projectionContext ?? HeadingProjectionContext.Empty, units))
            .ToArray();
    }

    private static HeadingRecord ProjectHeading(ValidatedStructuralElement element, HeadingProjectionContext context,
        IReadOnlyDictionary<string, DocumentSourceUnit>? units)
    {
        var source = element.Sources.FirstOrDefault();
        if (source is null)
            throw new InvalidOperationException($"Structural element '{element.Id}' has no validated source.");
        var metadata = context.ForElement(element.Id);

        return new HeadingRecord
        {
            StructuralElementId = element.Id,
            ValidatedSourcePartCount = element.Sources.Count,
            SourceParts = units is null ? null : Array.AsReadOnly(element.Sources.Select(part =>
                units.TryGetValue(part.SourceId, out var unit)
                    ? new HeadingSourcePart(part.SourceId, part.SourceOrdinal,
                        new TextOffsetSpan(part.Span.Start, part.Span.End), unit.Text)
                    : throw new InvalidOperationException($"outline-projection-source-missing:{element.Id}:{part.SourceId}"))
                .ToArray()),
            Index = metadata?.OutlineSourceOrdinal ?? source.SourceOrdinal,
            StableId = metadata?.OutlineStableId ?? context.StableIdFor(element.Id, source.SourceId) ?? source.SourceId,
            SourceId = metadata?.OutlineSourceId ?? source.SourceId,
            Level = metadata?.OutlineLevelIsSet == true ? metadata.OutlineLevel : element.Level,
            Text = metadata?.OutlineText ?? element.Text,
            OriginalText = metadata?.OriginalText ?? units?.GetValueOrDefault(source.SourceId)?.Text,
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
            DecisionStatus = ParseStatus(element.Decision.Status),
            ConfidenceBasis = element.Decision.ConfidenceBasis,
            Disputed = element.Decision.Disputed,
        };
    }

    // Fail closed: an origin or status this projection does not know is a producer contract break,
    // not something to relabel silently.
    private static HeadingSource ParseSource(string value) => value switch
    {
        StructuralDecisionOrigin.Model => HeadingSource.Model,
        StructuralDecisionOrigin.HumanCorrection => HeadingSource.HumanCorrection,
        _ => throw new InvalidOperationException($"unknown-structural-decision-origin:{value}"),
    };

    private static HeadingDecisionStatus ParseStatus(string value) =>
        Enum.TryParse<HeadingDecisionStatus>(value, ignoreCase: false, out var status) && Enum.IsDefined(status)
            ? status
            : throw new InvalidOperationException($"unknown-structural-decision-status:{value}");
}
