using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The one post-migration correction DOC-0252's structured occurrence Gold has received.
/// <para>
/// ITEM-505430BB - "International Comparison Program (ICP)" over "TECHNICAL ADVISORY GROUP (TAG)",
/// identity <c>L0513:S0:0-38|L0514:S0:0-30</c> - was not part of the 41-claim heading set the
/// migration in <see cref="PdfStructuredGoldMigrationTests"/> carried forward: it is absent from
/// that migration's predecessor and from its own frozen output, both preserved unchanged. A human
/// re-review on 2026-09-23 determined this occurrence functions as a document-level identifying
/// label for the embedded meeting/agenda artifact - not ordinary organization, date or venue
/// metadata - and approved it as a <c>DocumentTitle</c> claim. This is a genuine membership change
/// (add 1, remove 0), not a coordinate migration, so it is its own step rather than an edit to the
/// migration that came before it.
/// </para>
/// <para>
/// Everything else the 41-claim set already carried is reused verbatim, including the Agenda
/// claim (ITEM-CCE2C592, L0519:S0:0-6, SectionHeading) - this correction does not touch it. Every
/// experiment that ran before this correction pins the preserved 41-claim predecessor explicitly
/// (see <see cref="HistoricalGoldVintages"/>) rather than reading whatever DOC-0252 resolves to now.
/// </para>
/// </summary>
public sealed class Doc0252GoldCorrectionItem505430bbTests
{
    private const string OutputRoot = "eval/a99-closed-loop/canonical-semantic-gold-vnext/occurrence";

    // This was the live occurrence-gold path until the 2026-09-23 Agenda-identity correction
    // (Doc0252GoldCorrectionAgendaIdentityR3Tests) layered on top of it. What this test generates is
    // now the fixed R2 vintage that correction's own predecessor pins by hash - not the live file -
    // so its output moved to the preserved-snapshot name rather than staying at the live one.
    private const string OutputName = "DOC-0252.structured-source-parts.pre-agenda-identity-correction.occurrence-gold.v1.json";
    private const string Doc0252Pdf = "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";
    private const string SourceSha256 = "a005f25e3bb9754cd6c8c7000682d00eb68238fb8937d3475fe807ffbbd94b61";
    private const int PredecessorHeadings = 41;
    private const int CorrectedHeadings = 42;

    /// <summary>The 41-claim structured Gold this correction is layered on top of, pinned by path and hash.</summary>
    private const string PredecessorPath =
        "eval/a99-closed-loop/canonical-semantic-gold-vnext/occurrence/" +
        "DOC-0252.structured-source-parts.pre-item505430bb-document-label-correction.occurrence-gold.v1.json";

    private const string PredecessorSha256 =
        "322d431ff7be7af47034b8df711b9b23b247480900fdf5266ed0d0fb08b1b754";

    private const string ItemId = "ITEM-505430BB";
    private const string AddedIdentity = "L0513:S0:0-38|L0514:S0:0-30";

    [Fact]
    public void Item505430bb_is_added_as_a_document_title_claim_after_human_review()
    {
        var predecessorText = File.ReadAllText(
            TestRepository.Path(PredecessorPath.Replace('/', Path.DirectorySeparatorChar)));
        Assert.Equal(PredecessorSha256, CanonicalArtifactHash.OfText(predecessorText));
        using var predecessor = JsonDocument.Parse(predecessorText);
        var predecessorRoot = predecessor.RootElement;
        Assert.Equal(SourceSha256, predecessorRoot.GetProperty("sourceSha256").GetString());
        Assert.Equal(PredecessorHeadings, predecessorRoot.GetProperty("semanticHeadingTotal").GetInt32());
        var predecessorRows = predecessorRoot.GetProperty("boundOccurrences").EnumerateArray()
            .Select(row => row.Clone()).ToArray();
        Assert.Equal(PredecessorHeadings, predecessorRows.Length);

        // Not one of the 41: the correction adds a claim the migration never carried, not a
        // coordinate change to one it did.
        Assert.DoesNotContain(predecessorRows,
            row => row.GetProperty("identity").GetString() == AddedIdentity);
        // Agenda is untouched: same identity, same role, still present, unaffected by this change.
        var agenda = Assert.Single(predecessorRows,
            row => row.GetProperty("identity").GetString() == "L0519:S0:0-6");
        Assert.Equal("SectionHeading", agenda.GetProperty("semanticRole").GetString());

        // The added text, read from the same frozen atom universe every other claim here was
        // verified against - not hand-typed, so a text or length mismatch fails loudly here rather
        // than freezing a wrong claim.
        var atoms = PdfSourceAdapter.Build(
            TestRepository.Path(Doc0252Pdf.Replace('/', Path.DirectorySeparatorChar))).Atoms;
        var l0513 = atoms.Single(atom => atom.Alias == "L0513:S0");
        var l0514 = atoms.Single(atom => atom.Alias == "L0514:S0");
        Assert.Equal("International Comparison Program (ICP)", l0513.Text);
        Assert.Equal("TECHNICAL ADVISORY GROUP (TAG)", l0514.Text);
        var approvedWording = $"{l0513.Text} {l0514.Text}";
        var identity = $"L0513:S0:0-{l0513.Text.Length}|L0514:S0:0-{l0514.Text.Length}";
        Assert.Equal(AddedIdentity, identity);

        var addedRow = new
        {
            sourceAlias = "S0910",
            approvedWording,
            recordedLegacyText = approvedWording,
            textMigrated = false,
            sourceParts = new object[]
            {
                new
                {
                    sourceAlias = "L0513:S0", selectionMode = "WHOLE_ALIAS",
                    verbatimText = (string?)null, occurrence = (string?)null,
                    leftExactContext = (string?)null, rightExactContext = (string?)null,
                },
                new
                {
                    sourceAlias = "L0514:S0", selectionMode = "WHOLE_ALIAS",
                    verbatimText = (string?)null, occurrence = (string?)null,
                    leftExactContext = (string?)null, rightExactContext = (string?)null,
                },
            },
            semanticRole = "DocumentTitle",
            identity,
            projectedText = approvedWording,
        };

        // Document order, not append order: the same ordinal-by-identity order the 41 rows already
        // carry places L0513/L0514 between Session VI (L0507) and Agenda (L0519), where it occurs.
        var byIdentity = predecessorRows
            .Select(row => (Identity: row.GetProperty("identity").GetString()!, Row: (object)row))
            .Append((Identity: identity, Row: (object)addedRow));
        var rows = byIdentity.OrderBy(item => item.Identity, StringComparer.Ordinal)
            .Select(item => item.Row).ToArray();
        Assert.Equal(CorrectedHeadings, rows.Length);

        FreezeArtifact.AssertJson(OutputRoot, OutputName, new
        {
            artifactKind = "a99_pdf_structured_occurrence_gold",
            schemaVersion = "a99-pdf-structured-occurrence-gold-v1",
            documentId = "DOC-0252",
            sourceSha256 = SourceSha256,
            coordinateProfile = "STRUCTURED_SOURCE_PARTS",
            coordinateSystem = "STRUCTURED_SOURCE_PART_TUPLE",
            semanticHeadingTotal = CorrectedHeadings,
            migration = new
            {
                kind = "HUMAN_APPROVED_GOLD_CORRECTION",
                correctedAt = "2026-09-23",
                predecessorCoordinateProfile = "STRUCTURED_SOURCE_PARTS",
                predecessorGoldSha256 = PredecessorSha256,
                semanticMembershipChanged = true,
                add = 1,
                remove = 0,
                needsReview = 0,
                itemId = ItemId,
                addedIdentity = identity,
                addedSemanticRole = "DocumentTitle",
                oldExpectedLabel = "NON_STRUCTURAL",
                newExpectedLabel = "DOCUMENT_LABEL",
                reason = "Human re-review determined this occurrence functions as a document-level " +
                    "identifying/title-block label for the embedded meeting/agenda artifact, not " +
                    "ordinary organization/date/venue metadata. The decision is semantic-function " +
                    "based, not typography-based.",
                unaffectedControl = new
                {
                    itemId = "ITEM-CCE2C592",
                    identity = "L0519:S0:0-6",
                    semanticRole = "SectionHeading",
                    note = "Agenda is unchanged by this correction.",
                },
                // The coordinate migration this correction is layered on top of, carried forward
                // unchanged from the predecessor - so the full lineage back to the legacy occurrence
                // Gold stays traceable from this one file, the way the migration's own
                // boundaryCorrection already carries an earlier step forward the same way.
                priorMigration = predecessorRoot.GetProperty("migration").Clone(),
                note = "One claim added, none removed, none re-adjudicated. The 41 claims this " +
                    "migration carried forward are reused as-is; only the coordinate representation " +
                    "and source-faithful wording of those 41 were ever in scope for the migration " +
                    "that produced the predecessor this correction is layered on top of.",
            },
            boundOccurrences = rows,
        });
    }
}
