using DocxHeaderExtractor.Contracts.Models;

namespace DocxHeaderExtractor.DocumentProcessing.Selection;

/// <summary>Source-grounded heading accepted by the common binder.</summary>
internal sealed record ValidatedHeading(
    string SourceId,
    TextOffsetSpan HeadingSpan,
    string? SemanticFunction,
    string StructuralScope,
    string ValidationBasis)
{
    public IReadOnlyList<CanonicalSemanticBoundPart>? Parts { get; init; }
}
