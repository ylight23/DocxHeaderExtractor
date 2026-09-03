using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// The canonical occurrence a validated fact belongs to. For a DOCX product the document is the
/// authority and the rendered PDF is an observation of it, so this — not the PDF block — is what
/// identifies a heading and what a writeback must be able to locate.
/// <para>
/// It is materialized from the reconciliation the route already performed and validated. Nothing
/// here re-matches by title: an unmatched fact stays ungrounded rather than being guessed.
/// </para>
/// </summary>
public sealed record PdfCanonicalGrounding(
    [property: JsonPropertyName("sourceFactId")] string SourceFactId,
    [property: JsonPropertyName("paragraphIndex")] int ParagraphIndex,
    [property: JsonPropertyName("stableId")] string? StableId,
    [property: JsonPropertyName("span")] DocxTextSpan Span,
    [property: JsonPropertyName("paragraphText")] string ParagraphText)
{
    /// <summary>Reads the reconciliation the route already produced; it performs no matching.</summary>
    public static IReadOnlyList<PdfCanonicalGrounding> FromGroundedHeadings(IEnumerable<HeadingRecord> headings) =>
        headings
            .Where(heading => !string.IsNullOrWhiteSpace(heading.SourceId) && heading.HeadingSpan is not null)
            .GroupBy(heading => heading.SourceId!, StringComparer.Ordinal)
            .Select(group => group.First())
            .Select(heading => new PdfCanonicalGrounding(
                heading.SourceId!,
                heading.Index,
                heading.StableId,
                new DocxTextSpan(heading.HeadingSpan!.Start, heading.HeadingSpan.End),
                heading.OriginalText ?? heading.Text))
            .ToArray();

    /// <summary>Builds canonical occurrences from the generic authority projection metadata.</summary>
    public static IReadOnlyList<PdfCanonicalGrounding> FromValidatedStructure(ValidatedStructure structure) =>
        structure.OutlineElements
            .Select(element => (Element: element, Source: element.Sources.FirstOrDefault()))
            .Where(item => item.Source is not null)
            .Select(item =>
            {
                var source = item.Source!;
                var paragraphText = item.Element.ProjectionMetadata?.OriginalText ?? item.Element.Text;
                return new PdfCanonicalGrounding(
                    source.SourceId,
                    source.SourceOrdinal,
                    source.StableId,
                    new DocxTextSpan(source.Span.Start, source.Span.End),
                    paragraphText);
            })
            .ToArray();
}

/// <summary>
/// An offset range inside a canonical DOCX paragraph. Deliberately a distinct type from
/// <see cref="PdfTextSpan"/>: the two coordinate systems were both called `HeadingSpan` and could be
/// passed for one another, which is exactly the confusion this separation removes at compile time.
/// </summary>
public sealed record DocxTextSpan(
    [property: JsonPropertyName("start")] int Start,
    [property: JsonPropertyName("end")] int End);

/// <summary>An offset range inside the raw text of an observed PDF block. Evidence, not authority.</summary>
public sealed record PdfTextSpan(
    [property: JsonPropertyName("start")] int Start,
    [property: JsonPropertyName("end")] int End);

/// <summary>Where the observation was made. Provenance for review, never the product identity.</summary>
public sealed record PdfEvidenceAnchor(
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("blockId")] string BlockId,
    [property: JsonPropertyName("span")] PdfTextSpan Span,
    [property: JsonPropertyName("observedText")] string ObservedText,
    [property: JsonPropertyName("lineIds")] IReadOnlyList<string> LineIds);

/// <summary>The canonical identity of a heading: which occurrence of the source document it is.</summary>
public sealed record DocxSourceAnchor(
    [property: JsonPropertyName("paragraphIndex")] int ParagraphIndex,
    [property: JsonPropertyName("stableId")] string? StableId,
    [property: JsonPropertyName("span")] DocxTextSpan Span);
