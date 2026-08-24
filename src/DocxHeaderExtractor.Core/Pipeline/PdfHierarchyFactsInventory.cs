using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>
/// M8.1 observability only. It inventories hierarchy evidence for headings that already passed
/// source/span validation. It cannot create headings, alter a structure, or call a model.
/// </summary>
internal static class PdfHierarchyFactsInventory
{
    internal static IReadOnlyList<PdfHierarchyFactAudit> Inspect(
        IReadOnlyList<PdfValidatedHeading> validated,
        IReadOnlyDictionary<string, PdfCandidateContext> contexts)
    {
        var eligible = validated.Where(heading => contexts.ContainsKey(heading.SourceId))
            .OrderBy(heading => PositionOf(contexts[heading.SourceId]))
            .ToArray();

        // Keep audit construction separate from relation lookup. The inventory is not allowed to
        // reuse PdfHierarchyResolver, because doing so would make a resolver look like evidence.
        var observed = new List<ObservedHeading>();
        var facts = new List<PdfHierarchyFactAudit>(eligible.Length);
        string? previousId = null;
        for (var order = 0; order < eligible.Length; order++)
        {
            var heading = eligible[order];
            var context = contexts[heading.SourceId];
            var source = context.Source;
            var marker = PdfMarkerFactsParser.Parse(source.RawText) ?? source.Marker;
            var path = NumberingAudit.ParseArabicPath(source.RawText);
            var parent = FindMarkerPrefixParent(observed, path, source.StructuralScope, context.DocumentRegime);
            var hasResolvedRelation = path is { Length: 1 } || parent is not null;
            var parentResolution = parent is not null
                ? "marker_prefix_parent_candidate"
                : "relationship_unresolved";
            var evidence = new List<string>
            {
                "validated_source_span",
                $"scope:{source.StructuralScope}",
                $"regime:{context.DocumentRegime}",
                previousId is null ? "source_order:first" : "source_order:previous_validated",
            };
            if (marker is { } value)
            {
                evidence.Add($"marker:{value.Family}");
                evidence.Add($"marker_depth:{value.Depth}");
            }
            if (parent is not null) evidence.Add("marker_prefix_parent_candidate");
            if (!hasResolvedRelation) evidence.Add("relationship_unresolved");

            // M8.1a source occurrence identity. TextOffsetSpan.End is exclusive: PdfProposalValidator
            // rejects End > RawText.Length but accepts End == Length. The artifact keeps that
            // semantics verbatim instead of renormalising it for a prettier identity string.
            var span = heading.HeadingSpan;
            var blockText = source.RawText;
            var spanInRange = span.Start >= 0 && span.End > span.Start && span.End <= blockText.Length;
            if (!spanInRange) evidence.Add("heading_span_out_of_range");

            var fact = new PdfHierarchyFactAudit(
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
                FactId = $"p{source.Page}:{source.SourceId}:s{span.Start}-{span.End}",
                HeadingSpan = span,
                SourceBlockText = blockText,
                SourceBlockTextSha256 = PdfHierarchyFactHash.OfText(blockText),
                HeadingText = spanInRange ? blockText[span.Start..span.End] : "",
                LineIds = source.LineIds,
                Geometry = new PdfSourceGeometry(source.Left, source.TopY, source.Right, source.BottomY),
            };
            facts.Add(fact);
            observed.Add(new ObservedHeading(fact.Id, path, source.StructuralScope, context.DocumentRegime));
            previousId = heading.SourceId;
        }
        return facts;
    }

    private static ObservedHeading? FindMarkerPrefixParent(
        IReadOnlyList<ObservedHeading> observed,
        int[]? childPath,
        string scope,
        string regime)
    {
        if (childPath is not { Length: >= 2 }) return null;
        var parentPath = childPath[..^1];
        for (var index = observed.Count - 1; index >= 0; index--)
        {
            var candidate = observed[index];
            if (!string.Equals(candidate.Scope, scope, StringComparison.Ordinal) ||
                !string.Equals(candidate.Regime, regime, StringComparison.Ordinal)) continue;
            if (candidate.Path is not null && candidate.Path.SequenceEqual(parentPath)) return candidate;
        }
        return null;
    }

    private static (int Page, double InvertedY, string Id) PositionOf(PdfCandidateContext context) =>
        (context.Source.Page, -context.Source.TopY, context.Source.SourceId);

    private sealed record ObservedHeading(string Id, int[]? Path, string Scope, string Regime);
}

/// <summary>Source-derived audit record, deliberately separate from <see cref="PdfValidatedStructure"/>.</summary>
public sealed record PdfHierarchyFactAudit(
    string Id,
    int SourceOrder,
    int Page,
    string StructuralScope,
    string DocumentRegime,
    string? MarkerFamily,
    int? MarkerDepth,
    bool MarkerIsPath,
    string? MarkerPath,
    string? PreviousValidatedId,
    string? MarkerPrefixParentCandidate,
    int? ResolvedLevel,
    string ParentResolution,
    IReadOnlyList<string> Evidence)
{
    /// <summary>
    /// M8.1a readable occurrence id: <c>p{page}:{blockId}:s{start}-{end}</c>. It is opaque to
    /// consumers — page, block, and span authority live in their own fields, so a future format
    /// change cannot silently break an identity bridge that parsed the string.
    /// </summary>
    public string FactId { get; init; } = "";

    /// <summary>Immutable source authority: the whole raw block text the span points into.</summary>
    public string SourceBlockText { get; init; } = "";

    public string SourceBlockTextSha256 { get; init; } = "";

    /// <summary>Source pointer into <see cref="SourceBlockText"/>; End is exclusive.</summary>
    public TextOffsetSpan HeadingSpan { get; init; } = new(0, 0);

    /// <summary>Deterministic slice of <see cref="SourceBlockText"/>. Never model-authored.</summary>
    public string HeadingText { get; init; } = "";

    /// <summary>Parser line identities, kept only to correlate with line-level M7 artifacts.</summary>
    public IReadOnlyList<string> LineIds { get; init; } = [];

    public PdfSourceGeometry Geometry { get; init; } = new(0, 0, 0, 0);
}

public sealed record PdfSourceGeometry(double Left, double TopY, double Right, double BottomY);
