using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using UglyToad.PdfPig;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Migrates DOC-0252's 41 approved headings from legacy occurrence coordinates to structured
/// sourceParts, against the frozen segment-atom authority.
/// <para>
/// Semantic membership is not touched here - it was frozen at 41/41, add 0, remove 0, in the
/// reconfirmation this migration succeeds. What this file does is answer, for each of the 41
/// already-approved headings, "where does this exist in the new coordinate space" - and it answers
/// that by locating the approved wording in the atom stream, the same technique validated in the
/// Step 2 structured-binding audit, not by copying a list.
/// </para>
/// <para>
/// One exception is not derived automatically: S0616. Its legacy Gold text is a symptom of the
/// defect this whole migration exists to fix - the old occurrence it was recorded against was
/// itself truncated - so searching for that recorded text would migrate the truncation forward.
/// The corrected wording is the one independently re-audited against the rendered PDF and named
/// explicitly in this migration's authorization; every other claim's recorded text was already
/// verified complete and is used as-is.
/// </para>
/// </summary>
public sealed class PdfStructuredGoldMigrationTests
{
    private const string Doc0252 = "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";
    private const string OutputRoot = "eval/a99-closed-loop/canonical-semantic-gold-vnext/occurrence";

    /// <summary>
    /// This migration's own frozen output, preserved under its own name since the 2026-09-23
    /// ITEM-505430BB correction (see <see cref="Doc0252GoldCorrectionItem505430bbTests"/>) took over
    /// the active <c>DOC-0252.structured-source-parts.occurrence-gold.v1.json</c> name. This file
    /// still adds 0 and removes 0, exactly as its own asserts below require; the correction is a
    /// later, separate step layered on top of it, not a change to what this migration did.
    /// </summary>
    private const string OutputName =
        "DOC-0252.structured-source-parts.pre-item505430bb-document-label-correction.occurrence-gold.v1.json";
    private const int ApprovedHeadings = 41;
    private const string SourceSha256 =
        "a005f25e3bb9754cd6c8c7000682d00eb68238fb8937d3475fe807ffbbd94b61";

    /// <summary>
    /// The one deliberate correction. See the class remarks: migrating the recorded legacy text
    /// here would migrate the truncation the legacy universe imposed on it, not the heading.
    /// </summary>
    private const string S0616CorrectedWording =
        "2. A Survey Based Approach to Adjustment for Quality Differences in Services in " +
        "International Price Comparisons";

    /// <summary>
    /// The four claims whose migrated selection once stopped one character short of the heading
    /// their own approvedWording recorded - a closing parenthesis, in every case.
    /// <para>
    /// They were corrected under explicit authorization as a data patch, naming each alias. The
    /// patch is gone: <see cref="StructuredSourcePartLocator"/> no longer loses a target's terminal
    /// punctuation when it projects a reduced match back onto source offsets, so this migration now
    /// derives the same four selections from the source instead of being told them. The list
    /// remains only as the record of which claims that was, and as the thing the regression below
    /// checks the algorithm against.
    /// </para>
    /// </summary>
    private static readonly string[] FullAtomBoundaryCorrections =
        ["L0107:S0", "L0377:S0", "L0540:S0", "L0576:S0"];

    /// <summary>The structured Gold these corrections superseded, kept as predecessor provenance.</summary>
    private const string BoundaryCorrectionPredecessorGoldSha256 =
        "870c06ac4585d89f50496b5ae004f8a06c8072584e163184f817634fe03b468e";

    /// <summary>
    /// The predecessor Gold this migration reads from, pinned by path and hash rather than by
    /// authority id. Reading "whatever DOC-0252 currently resolves to" would make this script
    /// non-idempotent the moment it succeeds once: the id resolves to its own output afterwards,
    /// and a second run would try to migrate an already-structured claim as if it were legacy.
    /// </summary>
    private const string LegacyGoldPath =
        "eval/a99-closed-loop/gold-current/documents/DOC-0252.legacy-occurrence.gold.v1.json";
    private const string LegacyGoldSha256 =
        "51e2f708e7953dd6ffbe6c1b55ee2ddec430c26edd8dc51ddf71e7a13aa20b65";

    [Fact]
    public void The_41_approved_headings_migrate_to_structured_source_parts()
    {
        var legacyGoldPath = TestRepository.Path(LegacyGoldPath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        var legacyGoldText = File.ReadAllText(legacyGoldPath);
        Assert.Equal(LegacyGoldSha256, CanonicalArtifactHash.OfText(legacyGoldText));
        using var legacyGold = JsonDocument.Parse(legacyGoldText);
        var legacyClaims = legacyGold.RootElement.GetProperty("occurrence").GetProperty("claims")
            .EnumerateArray().ToArray();
        Assert.Equal(ApprovedHeadings, legacyClaims.Length);

        // The legacy universe, read only to recover each claim's WHOLE_ALIAS text (Gold does not
        // restate it) and to confirm the approved wording is otherwise exactly what Gold already
        // has - S0616 is the sole, named exception.
        var legacyOccurrences = LegacyOccurrences();
        var legacyAliases = PdfSourceOccurrenceBoundary.Aliases(legacyOccurrences.Count);

        var atoms = PdfStructuredSourceAuthorityBuilder.Build(Doc0252Path).Atoms;

        var rows = new List<object>();
        var cursor = 0;
        var partCounts = new List<int>();

        foreach (var claim in legacyClaims)
        {
            var sourceAlias = claim.GetProperty("sourceAlias").GetString()!;
            var legacyIndex = Array.IndexOf(legacyAliases, sourceAlias);
            Assert.True(legacyIndex >= 0, $"{sourceAlias} not found in the legacy universe");

            var recordedText = claim.GetProperty("verbatimText").ValueKind == JsonValueKind.String
                ? claim.GetProperty("verbatimText").GetString()!
                : legacyOccurrences[legacyIndex].VerbatimText;

            var approvedWording = sourceAlias == "S0616" ? S0616CorrectedWording : recordedText;

            var parts = StructuredSourcePartLocator.Locate(atoms, approvedWording, punctuationInsensitive: true, ref cursor);
            Assert.True(parts is not null, $"{sourceAlias}: '{approvedWording}' not found in the structured atom universe");

            var bound = SemanticSourcePartBinder.Bind(atoms, new SemanticSourcePartsProposal(parts!));
            Assert.True(bound.IsBound, $"{sourceAlias}: {bound.Status} - {bound.Reason}");
            partCounts.Add(bound.Parts.Count);

            // Whether the legacy Gold's own recorded characters - punctuation and all - occur
            // verbatim in the new atom stream. A claim can fail this and still bind (it did, above,
            // using the punctuation-insensitive search): failing it means the source-coordinate
            // representation changed under the claim, not that the claim is unrepresentable.
            var exactCursor = 0;
            var exactlyBindable = sourceAlias != "S0616" &&
                StructuredSourcePartLocator.Locate(atoms, recordedText, punctuationInsensitive: false, ref exactCursor) is not null;

            rows.Add(new
            {
                sourceAlias,
                approvedWording,
                recordedLegacyText = recordedText,
                textMigrated = !exactlyBindable,
                sourceParts = parts!.Select(part => new
                {
                    sourceAlias = part.SourceAlias,
                    selectionMode = part.SelectionMode,
                    verbatimText = part.VerbatimText,
                    occurrence = part.Occurrence,
                    leftExactContext = part.LeftExactContext,
                    rightExactContext = part.RightExactContext,
                }).ToArray(),
                semanticRole = claim.GetProperty("semanticRole").GetString(),
                identity = bound.Identity,
                projectedText = SemanticSourceProjection.Render(bound.Parts),
            });
        }

        FreezeArtifact.AssertJson(OutputRoot, OutputName, new
        {
            artifactKind = "a99_pdf_structured_occurrence_gold",
            schemaVersion = "a99-pdf-structured-occurrence-gold-v1",
            documentId = "DOC-0252",
            sourceSha256 = SourceSha256,
            coordinateProfile = "STRUCTURED_SOURCE_PARTS",
            coordinateSystem = "STRUCTURED_SOURCE_PART_TUPLE",
            semanticHeadingTotal = ApprovedHeadings,
            migration = new
            {
                kind = "SOURCE_COORDINATE_AND_VERBATIM_FIDELITY_MIGRATION",
                predecessorCoordinateProfile = "LEGACY_OCCURRENCE",
                predecessorGoldSha256 = "51e2f708e7953dd6ffbe6c1b55ee2ddec430c26edd8dc51ddf71e7a13aa20b65",
                semanticMembershipChanged = false,
                add = 0,
                remove = 0,
                needsReview = 0,
                textMigratedClaims = rows.Count(row => (bool)row.GetType().GetProperty("textMigrated")!.GetValue(row)!),
                boundaryCorrection = new
                {
                    kind = "SOURCE_SELECTION_BOUNDARY_CORRECTION",
                    predecessorGoldSha256 = BoundaryCorrectionPredecessorGoldSha256,
                    correctedAliases = FullAtomBoundaryCorrections,
                    semanticMembershipChanged = false,
                    approvedWordingChanged = false,
                    authorization = "explicit user authorization, DOC-0252 only, selection-only, named per alias",
                    note = "Each of these selections stopped one character short of the heading its own "
                        + "approvedWording records - a closing parenthesis dropped by the punctuation-"
                        + "insensitive locator this migration searches with. The selections now cover their "
                        + "whole atom. The locator itself is unchanged and is corrected separately.",
                },
                note = "Coordinate representation and source-faithful wording changed. No heading was added, removed, or re-adjudicated; the 41-heading membership approved for DOC-0252 is reused, not re-approved.",
            },
            boundOccurrences = rows,
        });

        Assert.Equal(ApprovedHeadings, rows.Count);

        // The four claims that were once corrected by name are now produced by the locator itself:
        // each selects its whole atom, and nothing here tells it to. If the projection defect ever
        // returns, these stop being WHOLE_ALIAS and this fails before the Gold artifact is written.
        foreach (var alias in FullAtomBoundaryCorrections)
        {
            var row = rows.Single(item => ((dynamic)item).sourceParts[0].sourceAlias == alias);
            Assert.Equal(CanonicalSemanticSelectionMode.WholeAlias, (string)((dynamic)row).sourceParts[0].selectionMode);
            Assert.Equal(atoms.Single(atom => atom.Alias == alias).Text, (string)((dynamic)row).projectedText);
        }
        Assert.All(partCounts, count => Assert.InRange(count, 1, 2));
        Assert.Equal(1, partCounts.Count(count => count == 2));
        Assert.Equal(40, partCounts.Count(count => count == 1));
    }



    private static string Doc0252Path => System.IO.Path.Combine(
        TestRepository.Root(), Doc0252.Replace('/', System.IO.Path.DirectorySeparatorChar));

    private static IReadOnlyList<PdfSemanticBlock> LegacyOccurrences()
    {
        using var document = PdfDocument.Open(Doc0252Path);
        var lines = PdfLineExtraction.ExtractLines(document, PdfLineGrouping.MidpointV1);
        return PdfSemanticBlockGrouper.Build(PdfLineBlockFilter.Analyze(lines), includeRiskLines: true);
    }
}
