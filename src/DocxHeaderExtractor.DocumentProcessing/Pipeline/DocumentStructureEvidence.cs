using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Policy;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// Evidence that survived in the input DOCX itself. This is deliberately independent of language,
/// document genre, and a classifier's inferred mode: a PDF-to-DOCX conversion often preserves text
/// but loses every one of these structural declarations.
/// </summary>
internal static class DocumentStructureEvidence
{
    private static readonly HashSet<string> AuthoritativeInternalOutlineBases =
    [
        RfcTocDictionaryOutline.Basis,
        BookTocDictionaryOutline.Basis,
        PdfBookmarkOutline.Basis,
        PdfTaggedEvidenceOutline.Basis,
        PartSectionOutline.Basis,
    ];

    public static bool HasNativeSemanticStructure(DocxPolicyState policyState) =>
        policyState.Paragraphs.Any(p => p.OutlineLevel is not null ||
            p.TrustedHeadingStyle || p.NumberingStyleHeadingLevel is not null || p.NumberingId is not null);

    public static bool HasNativeSemanticStructure(IReadOnlyList<IPolicyParagraph> paragraphs) =>
        paragraphs.Any(p => p.OutlineLevel is not null ||
            p.HasBuiltInHeadingStyle || p.NumberingStyleLevel is not null || p.NumberingId is not null);

    /// <summary>
    /// A table of contents supplied by the document is semantic evidence even when the conversion
    /// erased all OOXML properties. It must outrank a visual PDF cluster, which can legitimately
    /// discover a deeper content outline but must not replace the author's navigation outline.
    /// </summary>
    public static bool HasAuthoritativeInternalOutline(IReadOnlyCollection<HeadingRecord>? headings) =>
        headings is { Count: > 0 } && headings.All(h => AuthoritativeInternalOutlineBases.Contains(h.ConfidenceBasis));
}
