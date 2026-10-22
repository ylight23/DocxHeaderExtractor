using DocxHeaderExtractor.Contracts.Models;

namespace DocxHeaderExtractor.DocumentProcessing.Selection;

/// <summary>One exact heading extent selected by an authority implementation.</summary>
internal sealed record HeadingExtentDecision(
    string Id,
    string Reason,
    TextOffsetSpan? HeadingSpan = null,
    string? SemanticFunction = null,
    IReadOnlyList<CanonicalSemanticBoundPart>? Parts = null);
