namespace DocxHeaderExtractor.Core.Models;

/// <summary>
/// Which PDF coordinate authority a document uses, as one bundled, indivisible choice.
/// <para>
/// A profile is not a document type. It is a declared property of one document's authority -
/// recorded beside its Gold, never inferred from its extension - and it fixes, together, the
/// coordinate system, the response contract, and the evaluator that can score it. Those three
/// cannot be mixed: scoring a structured-tuple Gold with the legacy occurrence evaluator, or
/// binding a legacy Gold's aliases against the structured contract's schema, would each silently
/// compare two things that were never the same claim.
/// </para>
/// <para>
/// Most of the PDF corpus is <see cref="LegacyOccurrence"/> today. DOC-0252 is the first document
/// whose authority declares <see cref="StructuredSourceParts"/>, because it is the first with an
/// audited, source-backed migration behind it - not because it is somehow a different kind of PDF.
/// A second document adopts the same profile by having its own authority declare it; nothing here
/// names a document.
/// </para>
/// </summary>
public sealed record PdfSemanticAuthorityProfile(
    string ProfileId,
    string CoordinateSystem,
    SemanticCoordinateContract Contract,
    string EvaluatorId,
    bool StructuredSourcePartsEvaluable,
    string SourceAuthorityId)
{
    /// <summary>Visual-segment atoms addressed as <c>L{row}:S{segment}</c>, carrying layout metadata.</summary>
    public const string StructuredAtomSourceAuthority = "STRUCTURED_ATOMS";

    /// <summary>The historical occurrence universe.</summary>
    public const string LegacyOccurrenceSourceAuthority = "LEGACY_OCCURRENCES";

    /// <summary>
    /// The historical PDF authority: one bound occurrence per claim, addressed by alias and a
    /// selection mode. <see cref="CanonicalSemanticPdfAuthorityAdapter"/>'s existing behaviour,
    /// unchanged, and still the default for any PDF whose authority does not declare otherwise.
    /// </summary>
    public static readonly PdfSemanticAuthorityProfile LegacyOccurrence = new(
        "LEGACY_OCCURRENCE",
        "SOURCE_ALIAS_PLUS_SELECTION_MODE",
        SemanticCoordinateContract.PdfAliasSelection,
        "a99-pdf-gold-evaluator-v3-bound-occurrence-semantic-role",
        StructuredSourcePartsEvaluable: false,
        SourceAuthorityId: LegacyOccurrenceSourceAuthority);

    /// <summary>
    /// The structured authority: an ordered tuple of harness-resolved coordinates over
    /// visual-segment atoms, addressed by <c>PdfLineIdentity.Of(line)</c> aliases
    /// (<c>L{row}:S{segment}</c>). Adopted per document, by that document's own authority
    /// declaration - never inferred from being a PDF.
    /// </summary>
    public static readonly PdfSemanticAuthorityProfile StructuredSourceParts = new(
        "STRUCTURED_SOURCE_PARTS",
        "STRUCTURED_SOURCE_PART_TUPLE",
        SemanticCoordinateContract.PdfStructuredSourceParts,
        "a99-pdf-gold-evaluator-v4-structured-source-parts-semantic-role",
        StructuredSourcePartsEvaluable: true,
        SourceAuthorityId: StructuredAtomSourceAuthority);
}
