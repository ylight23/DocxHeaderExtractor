using DocxHeaderExtractor.DocumentProcessing.Source.Common;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.DocumentProcessing.Source.Pdf;

/// <summary>
/// M8.1 observability only. It inventories hierarchy evidence for headings that already passed
/// source/span validation. It cannot create headings, alter a structure, or call a model.
/// </summary>
internal static class PdfHierarchyFactsInventory
{
    internal static IReadOnlyList<HeadingHierarchyFactAudit> Inspect(
        IReadOnlyList<ValidatedHeading> validated,
        IReadOnlyDictionary<string, PdfSemanticSourceContext> contexts)
    {
        var eligible = validated.Where(heading => contexts.ContainsKey(heading.SourceId))
            .OrderBy(heading => PositionOf(contexts[heading.SourceId]))
            .ToArray();

        // Keep audit construction separate from relation lookup. The inventory must not reuse a
        // hierarchy resolver, because doing so would make a resolver look like evidence.
        var observed = new List<ObservedHeading>();
        var facts = new List<HeadingHierarchyFactAudit>(eligible.Length);
        string? previousId = null;
        for (var order = 0; order < eligible.Length; order++)
        {
            var heading = eligible[order];
            var context = contexts[heading.SourceId];
            var source = context.Source;
            var marker = SourceMarkerFactsParser.Parse(source.RawText) ?? source.Marker;
            var path = SourceMarkerFactsParser.ArabicPath(source.RawText);
            var parent = FindMarkerPrefixParent(observed, path);
            var hasResolvedRelation = path is { Length: 1 } || parent is not null;
            var parentResolution = parent is not null
                ? "marker_prefix_parent_observed"
                : "relationship_unresolved";
            var evidence = new List<string>
            {
                "validated_source_span",
                previousId is null ? "source_order:first" : "source_order:previous_validated",
            };
            if (marker is { } value)
            {
                evidence.Add($"marker:{value.Family}");
                evidence.Add($"marker_depth:{value.Depth}");
            }
            if (parent is not null) evidence.Add("marker_prefix_parent_observed");
            if (!hasResolvedRelation) evidence.Add("relationship_unresolved");

            // M8.1a source occurrence identity. TextOffsetSpan.End is exclusive: HeadingDecisionValidator
            // rejects End > RawText.Length but accepts End == Length. The artifact keeps that
            // semantics verbatim instead of renormalising it for a prettier identity string.
            var span = heading.HeadingSpan;
            var blockText = source.RawText;
            var spanInRange = span.Start >= 0 && span.End > span.Start && span.End <= blockText.Length;
            if (!spanInRange) evidence.Add("heading_span_out_of_range");

            var fact = new HeadingHierarchyFactAudit(
                heading.SourceId,
                order,
                source.Page,
                source.StructuralScope,
                context.DocumentRegime,
                marker?.Family,
                marker?.Depth,
                marker?.IsPath ?? false,
                path is null ? null : string.Join('.', path),
                previousId,
                parent?.Id,
                hasResolvedRelation ? path!.Length : null,
                parentResolution,
                evidence)
            {
                // M8.1d-2: complete parser components are recorded for observation only. MarkerPath,
                // ResolvedLevel, the observed ancestor pool, and FindMarkerPrefixParent deliberately
                // still run on the strict `path` above, so this commit adds no ancestry authority.
                // Where the two disagree, the source lost its separators and the strict grammar could
                // not read it; reconciling them is a later, separately gated step.
                MarkerComponents = marker is { Components.IsDefaultOrEmpty: false } complete
                    ? complete.Components
                    : [],
                FactId = $"p{source.Page}:{source.SourceId}:s{span.Start}-{span.End}",
                HeadingSpan = span,
                SourceBlockText = blockText,
                SourceBlockTextSha256 = HierarchyFactHash.OfText(blockText),
                HeadingText = spanInRange ? blockText[span.Start..span.End] : "",
                LineIds = source.LineIds,
                Geometry = new PdfSourceGeometry(source.Left, source.TopY, source.Right, source.BottomY),
            };
            facts.Add(fact);
            observed.Add(new ObservedHeading(fact.Id, path));
            previousId = heading.SourceId;
        }
        return facts;
    }

    private static ObservedHeading? FindMarkerPrefixParent(
        IReadOnlyList<ObservedHeading> observed,
        int[]? childPath)
    {
        if (childPath is not { Length: >= 2 }) return null;
        var parentPath = childPath[..^1];
        for (var index = observed.Count - 1; index >= 0; index--)
        {
            var observedHeading = observed[index];
            if (observedHeading.Path is not null && observedHeading.Path.SequenceEqual(parentPath)) return observedHeading;
        }
        return null;
    }

    private static (int Page, double InvertedY, string Id) PositionOf(PdfSemanticSourceContext context) =>
        (context.Source.Page, -context.Source.TopY, context.Source.SourceId);

    private sealed record ObservedHeading(string Id, int[]? Path);
}
