using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Core.Eval;

/// <summary>
/// Where a reviewed gold heading actually occurs in the rendered PDF, expressed as the source lines
/// it is made of.
/// <para>
/// Evaluation needs this because gold names a canonical DOCX occurrence while candidates are built
/// from PDF source facts, and the two were previously joined by matching text. Text matching cannot
/// tell a heading from a cross-reference that quotes it, which is how a rank belonging to
/// "See Section XIV: ..." on one page came to be reported for the heading on another. Anchoring to
/// source lines removes that: a candidate either covers the lines the heading is made of or it does
/// not.
/// </para>
/// <para>
/// The bridge is reviewed data, not a matcher. Entries are proposed deterministically and then
/// confirmed; nothing here is inferred at evaluation time, and an entry that was never reviewed is
/// absent rather than guessed.
/// </para>
/// </summary>
public sealed record PdfReviewedOccurrenceBridge(
    [property: JsonPropertyName("document")] string Document,
    [property: JsonPropertyName("docxSha256")] string DocxSha256,
    [property: JsonPropertyName("pdfSha256")] string PdfSha256,
    [property: JsonPropertyName("goldKeySha256")] string GoldKeySha256,
    [property: JsonPropertyName("pdfLineExtractionFingerprint")] string PdfLineExtractionFingerprint,
    [property: JsonPropertyName("occurrences")] IReadOnlyList<PdfReviewedOccurrence> Occurrences)
{
    public const int SchemaVersion = 1;

    [JsonPropertyName("schemaVersion")]
    public int Schema => SchemaVersion;

    [JsonPropertyName("artifactKind")]
    public string ArtifactKind => "pdf_reviewed_occurrence_bridge";

    [JsonPropertyName("usesModel")]
    public bool UsesModel => false;

    [JsonPropertyName("usesPipelineOutput")]
    public bool UsesPipelineOutput => false;

    public static PdfReviewedOccurrenceBridge Load(string json) =>
        JsonSerializer.Deserialize<PdfReviewedOccurrenceBridge>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidOperationException("Occurrence bridge JSON could not be read.");

    /// <summary>
    /// Line indexes are only meaningful inside the extraction they were reviewed against. The PDF
    /// hash alone does not establish that: the same file re-read by a changed extractor or grouper
    /// can produce the same bytes and different line numbering. So the extraction is fingerprinted
    /// too, and a mismatch is refused rather than silently reused against shifted indexes.
    /// </summary>
    public void EnsureCurrent(string docxSha256, string pdfSha256, string goldKeySha256, string extractionFingerprint)
    {
        var stale = new List<string>();
        if (!string.Equals(DocxSha256, docxSha256, StringComparison.OrdinalIgnoreCase)) stale.Add("docxSha256");
        if (!string.Equals(PdfSha256, pdfSha256, StringComparison.OrdinalIgnoreCase)) stale.Add("pdfSha256");
        if (!string.Equals(GoldKeySha256, goldKeySha256, StringComparison.OrdinalIgnoreCase)) stale.Add("goldKeySha256");
        if (!string.Equals(PdfLineExtractionFingerprint, extractionFingerprint, StringComparison.OrdinalIgnoreCase))
            stale.Add("pdfLineExtractionFingerprint");
        if (stale.Count > 0)
            throw new InvalidOperationException(
                $"stale_occurrence_bridge: {string.Join(", ", stale)} no longer match the inputs this bridge was reviewed against.");
    }

    /// <summary>Only a reviewed occurrence may be used; a proposal is not evidence yet.</summary>
    public PdfReviewedOccurrence? Find(string goldStableId) => Occurrences.FirstOrDefault(item =>
        string.Equals(item.GoldStableId, goldStableId, StringComparison.Ordinal) &&
        string.Equals(item.ReviewStatus, "reviewed", StringComparison.Ordinal));
}

public sealed record PdfReviewedOccurrence(
    [property: JsonPropertyName("goldStableId")] string GoldStableId,
    [property: JsonPropertyName("goldText")] string GoldText,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("lines")] IReadOnlyList<PdfReviewedOccurrenceLine> Lines,
    [property: JsonPropertyName("reviewStatus")] string ReviewStatus,
    [property: JsonPropertyName("reviewMethod")] string ReviewMethod,
    [property: JsonPropertyName("proposalMatchCount")] int ProposalMatchCount)
{
    /// <summary>
    /// The lines a candidate has to cover to represent this occurrence. Punctuation the renderer
    /// emitted as its own line carries no text, and a producer that drops it is still representing
    /// the heading - requiring it would reject the only candidate that gets the occurrence right.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<PdfReviewedOccurrenceLine> RequiredLines => Lines
        .Where(line => PdfTextUtilities.CanonicalForMatch(line.Text).Length > 0)
        .ToArray();
}

public sealed record PdfReviewedOccurrenceLine(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("lineId")] string LineId,
    [property: JsonPropertyName("text")] string Text);

/// <summary>
/// Deterministic proposal for the bridge: the same whole-line equality the retrieval trace already
/// uses for <c>FoundExactSourceLine</c>, run over every extracted line.
/// <para>
/// It deliberately has no page filter, no containment, no fuzzy or ordered fallback, and no
/// normalisation of its own. Those are the shortcuts that put a gold entry on the wrong occurrence
/// in the first place; a heading the rule cannot place uniquely is returned for review instead.
/// </para>
/// </summary>
internal static class PdfOccurrenceBridgeProposal
{
    public static string LineId(PdfLine line) => string.Create(CultureInfo.InvariantCulture,
        $"{line.Page}|{line.Y:R}|{line.Left:R}|{line.Right:R}|{line.Text}");

    /// <summary>Binds a reviewed line index to the extraction it was reviewed against.</summary>
    public static string ExtractionFingerprint(IReadOnlyList<PdfLine> lines) =>
        PdfHierarchyFactHash.OfText(string.Join('\n', lines.Select((line, index) =>
            string.Create(CultureInfo.InvariantCulture, $"{index}|{LineId(line)}"))));

    public static IReadOnlyList<PdfOccurrenceProposal> Propose(
        IReadOnlyList<PdfLine> lines,
        IEnumerable<(string StableId, string Text)> gold) =>
        gold.Select(entry =>
        {
            var target = PdfTextUtilities.CanonicalForMatch(entry.Text);
            var matches = lines
                .Select((line, index) => (line, index))
                .Where(item => string.Equals(Canonical(item.line), target, StringComparison.Ordinal))
                .Select(item => new PdfReviewedOccurrenceLine(item.index, LineId(item.line), item.line.Text))
                .ToArray();
            return new PdfOccurrenceProposal(entry.StableId, entry.Text, matches, matches.Length switch
            {
                0 => "unresolved_for_review",
                1 => "proposed",
                _ => "ambiguous_for_review",
            });
        }).ToArray();

    private static string Canonical(PdfLine line) =>
        line.CanonicalMatchText ?? PdfTextUtilities.CanonicalForMatch(line.Text);
}

internal sealed record PdfOccurrenceProposal(
    string GoldStableId,
    string GoldText,
    IReadOnlyList<PdfReviewedOccurrenceLine> Matches,
    string Status);
