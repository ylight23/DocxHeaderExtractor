using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// M9.3. Turns a decided <see cref="PdfFinalStructure"/> into the product's minimal heading shape.
/// After M9.1b there is nothing left to translate: identity, text and hierarchy are already
/// canonical, so this layer only selects and reshapes what M9.1/M9.2 already decided.
/// <para>
/// It reads <see cref="PdfFinalStructure"/> and <see cref="PdfOutputDecision"/> and nothing else. It
/// may not read <c>HeadingRecord</c> or the legacy policy to fill a field it lacks — a field this
/// layer needs and the projection does not carry is a contract gap in M9.1, not something to recover
/// here by re-matching a title, resolving a new parent, normalising scope/role, or inventing a style
/// id the evidence never produced.
/// </para>
/// <para>
/// Only decisions with <see cref="PdfOutputDecision.Emit"/> set become a product record. A heading
/// without a <see cref="DocxSourceAnchor"/> is never one of them: the emission invariant in
/// <see cref="PdfOutputDecisionPolicy"/> already guarantees that, and this layer re-checks it rather
/// than trusting it silently, because a record without a canonical occurrence cannot be written back.
/// </para>
/// </summary>
public static class PdfProductOutputSerializer
{
    public const int SchemaVersion = 1;

    public static PdfProductOutput Serialize(PdfFinalStructure structure, IReadOnlyList<PdfOutputDecision> decisions)
    {
        var records = structure.Headings
            .Join(decisions, heading => heading.Id, decision => decision.HeadingId, (heading, decision) => (heading, decision))
            .Where(pair => pair.decision.Emit && pair.heading.SourceAnchor is not null)
            .Select(pair => ToProductHeading(pair.heading, pair.decision))
            .ToArray();

        return new PdfProductOutput(structure.SourceDocumentSha256, records);
    }

    private static PdfProductHeading ToProductHeading(PdfFinalHeading heading, PdfOutputDecision decision)
    {
        var anchor = heading.SourceAnchor!;
        return new PdfProductHeading(
            heading.Id,
            anchor.ParagraphIndex,
            anchor.StableId,
            anchor.Span,
            heading.Text,
            heading.Role,
            heading.Level,
            heading.ParentId,
            decision.RequiresReview,
            decision.Reasons,
            heading.SourceText);
    }
}

public sealed record PdfProductOutput(
    [property: JsonPropertyName("sourceDocumentSha256")] string SourceDocumentSha256,
    [property: JsonPropertyName("headings")] IReadOnlyList<PdfProductHeading> Headings)
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion => PdfProductOutputSerializer.SchemaVersion;

    [JsonPropertyName("artifactKind")]
    public string ArtifactKind => "pdf_product_output";
}

/// <summary>
/// A product heading: the canonical DOCX occurrence, its text as the document states it, and the
/// review state the decision already computed. Level and parent are carried exactly as materialized
/// — null when unresolved, never filled here.
/// </summary>
public sealed record PdfProductHeading(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("paragraphIndex")] int ParagraphIndex,
    [property: JsonPropertyName("stableId")] string? StableId,
    [property: JsonPropertyName("span")] DocxTextSpan Span,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("level")] int? Level,
    [property: JsonPropertyName("parentId")] string? ParentId,
    [property: JsonPropertyName("requiresReview")] bool RequiresReview,
    [property: JsonPropertyName("reasons")] IReadOnlyList<string> Reasons,
    [property: JsonPropertyName("sourceText")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SourceText = null);
