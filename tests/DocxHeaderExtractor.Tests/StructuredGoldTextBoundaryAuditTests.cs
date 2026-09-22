using System.Globalization;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Audits the structured Gold's source selections against the atoms they name. Reads only; Gold is
/// not modified here.
/// <para>
/// Two claims were noticed because the coherent-packing rerun scored them as a false positive and
/// a false negative in every repeat: the model selected a whole atom, Gold selected all but its
/// last character. The pattern was worth chasing because the model's reading and Gold's differed by
/// exactly one closing parenthesis, twice, and never varied.
/// </para>
/// </summary>
public sealed class StructuredGoldTextBoundaryAuditTests
{
    private const string AuditRoot = "eval/a99-closed-loop/structured-gold-text-boundary-audit-v1";
    private const string Doc0252Pdf =
        "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";
    private const string GoldSha256 = "870c06ac4585d89f50496b5ae004f8a06c8072584e163184f817634fe03b468e";

    /// <summary>
    /// The Gold this result was produced against, pinned by path and hash rather than by authority
    /// id. DOC-0252's selections were later corrected for four headings whose migrated coordinates
    /// stopped one character short of their own approved wording, which moved the authority's hash;
    /// resolving by id here would score a finished run against a Gold it never ran against.
    /// </summary>
    private const string PredecessorGoldPath =
        "eval/a99-closed-loop/gold-current/documents/DOC-0252.structured-boundary-predecessor.gold.v1.json";
    private const string SourceUniverseSha256 = "2a953bf785ed1af00bc908ff9e5d6a1d988b04c0d980ecd95336bc5a9702f46f";

    [Fact]
    public void Audit_every_structured_gold_selection_against_its_own_atoms()
    {
        Assert.Equal(GoldSha256, CanonicalGoldRegistry.EntryAt(PredecessorGoldPath, GoldSha256).GoldSha256);
        var plan = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(Doc0252Pdf));
        Assert.Equal(SourceUniverseSha256, plan.SourceUniverseSha256);
        var atomByAlias = plan.Atoms.ToDictionary(atom => atom.Alias, StringComparer.Ordinal);

        using var gold = CanonicalGoldRegistry.ResolveAt(PredecessorGoldPath, GoldSha256);
        var claims = gold.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray().ToArray();
        Assert.Equal(41, claims.Length);

        var findings = claims.Select(claim =>
        {
            var approved = claim.GetProperty("approvedWording").GetString()!;
            var projected = claim.GetProperty("projectedText").GetString()!;
            var identity = claim.GetProperty("identity").GetString()!;
            var parts = claim.GetProperty("sourceParts").EnumerateArray().ToArray();
            var lastPart = parts[^1];
            var lastAlias = lastPart.GetProperty("sourceAlias").GetString()!;
            var lastAtom = atomByAlias[lastAlias];

            // What the claim's own last atom holds, and where its selection stops inside it.
            var selectedEnd = int.Parse(identity.Split('|')[^1].Split('-')[^1], CultureInfo.InvariantCulture);
            var stopsShortOfAtomEnd = selectedEnd < lastAtom.Text.Length;
            var omitted = stopsShortOfAtomEnd ? lastAtom.Text[selectedEnd..] : string.Empty;

            // Gold disagreeing with itself is the strongest signal available here: approvedWording
            // is what a person approved, projectedText is what the migrated coordinates render.
            var selectionMatchesApprovedWording = string.Equals(projected, approved, StringComparison.Ordinal);
            var approvedIsWholeAtomText = parts.Length == 1 &&
                string.Equals(approved, lastAtom.Text, StringComparison.Ordinal);

            // The defect is a selection that stops short of its atom while the approved wording runs
            // to the same end. Tested on the omitted characters themselves rather than on whole-string
            // equality, because a claim can carry the legacy reconstruction's stale punctuation at the
            // front and still be truncated at the back - and that one is the easiest to overlook.
            var omittedIsPunctuation = omitted.Length > 0 && omitted.All(character =>
                char.IsPunctuation(character) || char.IsSymbol(character));
            var approvedEndsWithOmitted = omitted.Length > 0 &&
                approved.EndsWith(omitted, StringComparison.Ordinal);

            var classification =
                stopsShortOfAtomEnd && omittedIsPunctuation && approvedEndsWithOmitted
                    ? "GOLD_TEXT_BOUNDARY_DEBT_CONFIRMED"
                    : stopsShortOfAtomEnd && !selectionMatchesApprovedWording && !approvedEndsWithOmitted
                        && projected.Length < approved.Length
                        ? "REQUIRES_REVIEW"
                        : "NO_DEFECT";

            return new
            {
                sourceAlias = claim.GetProperty("sourceAlias").GetString(),
                identity,
                lastPartAlias = lastAlias,
                approvedWording = approved,
                projectedText = projected,
                atomText = lastAtom.Text,
                atomLength = lastAtom.Text.Length,
                selectedEnd,
                omittedTail = omitted,
                omittedCodePoints = omitted.Select(character => $"U+{(int)character:X4}").ToArray(),
                selectionMatchesApprovedWording,
                approvedEqualsWholeAtomText = approvedIsWholeAtomText,
                selectionMode = lastPart.GetProperty("selectionMode").GetString(),
                classification,
            };
        }).ToArray();

        var confirmed = findings.Where(item => item.classification == "GOLD_TEXT_BOUNDARY_DEBT_CONFIRMED").ToArray();
        var review = findings.Where(item => item.classification == "REQUIRES_REVIEW").ToArray();

        // The other direction, recorded so it is not mistaken for the same thing: claims whose
        // approved wording is SHORTER than what their coordinates render. Those are the legacy
        // reconstruction's dropped punctuation, which the structured migration restored - the
        // selection is complete and the stale string is the approved wording, not the coordinates.
        var restored = findings
            .Where(item => !item.selectionMatchesApprovedWording && item.projectedText.Length > item.approvedWording.Length)
            .ToArray();

        FreezeArtifact.AssertJson(AuditRoot, "structured-gold-text-boundary-audit.v1.json", new
        {
            artifactKind = "a99_structured_gold_text_boundary_audit",
            schemaVersion = "a99-structured-gold-text-boundary-audit-v1",
            authorityId = "DOC-0252",
            providerCalls = 0,
            modelCalls = 0,
            goldModified = false,
            goldSha256 = GoldSha256,
            sourceUniverseSha256 = SourceUniverseSha256,
            goldClaims = claims.Length,

            question = "Two claims scored as a false positive and a false negative in every repeat of the "
                + "coherent-packing rerun, differing from the model only by a final ')'. Is the model "
                + "over-selecting, or does Gold stop one character short of the heading it approved?",

            answer = new
            {
                boundaryDebtConfirmed = confirmed.Length,
                requiresReview = review.Length,
                note = "Gold disagrees with itself in these claims: approvedWording - the string a person "
                    + "approved - is exactly the atom's full text, while the migrated sourceParts select "
                    + "all of it but the last character. No semantic judgement is needed to see it, and "
                    + "none is proposed: membership, role and the approved wording all stay as they are.",
            },

            rootCause = new
            {
                where = "StructuredSourcePartLocator.Locate, punctuationInsensitive branch "
                    + "(tests/DocxHeaderExtractor.Tests/StructuredSourcePartLocator.cs)",
                what = "The locator builds its search stream by skipping whitespace and punctuation, so a "
                    + "target ending in ')' matches up to its last letter. The end offset is then taken "
                    + "from that last matched character, which lands one short of the atom's end - and the "
                    + "whole-atom case, which would have produced WHOLE_ALIAS, is never recognised.",
                whyOnlyThese = "It can only bite a heading whose approved wording ends in punctuation. Four "
                    + "of the forty-one do, and all four are affected; the other thirty-seven end in a "
                    + "letter or digit and are unaffected.",
                usedBy = "PdfStructuredGoldMigrationTests, when DOC-0252's Gold was migrated from legacy "
                    + "occurrence coordinates to structured sourceParts.",
            },

            confirmedDebt = confirmed,

            requiresReview = review,

            punctuationRestoredByMigration = new
            {
                count = restored.Length,
                note = "The opposite direction and not a defect: these claims' coordinates render MORE text "
                    + "than their approvedWording holds, because the legacy line reconstruction had dropped "
                    + "punctuation that the atom stream still has. The selection is source-faithful; the "
                    + "approved string is the stale one. Recorded here so a reader does not count them.",
                examples = restored.Take(3).Select(item => new
                {
                    item.lastPartAlias,
                    item.approvedWording,
                    item.projectedText,
                }).ToArray(),
            },

            proposedCorrection = new
            {
                applied = false,
                scope = "the last part's selection of each confirmed claim, and nothing else",
                change = "selectionMode VERBATIM_TEXT with a truncated verbatimText becomes WHOLE_ALIAS, "
                    + "because in every confirmed case the approved heading is exactly the atom's text",
                semanticMembershipChanged = false,
                goldCountBefore = claims.Length,
                goldCountAfter = claims.Length,
                identityChanges = confirmed.Select(item => new
                {
                    item.lastPartAlias,
                    before = item.identity,
                    after = $"{item.lastPartAlias}:0-{item.atomLength}",
                    beforeText = item.projectedText,
                    afterText = item.atomText,
                }).ToArray(),
                blastRadius = new
                {
                    note = "Correcting these changes four claim identities and therefore DOC-0252's Gold hash, "
                        + "which is named by the registry, by both experiment runs' frozen summaries and by "
                        + "every score artifact already committed. The captures themselves are unaffected - "
                        + "they record what the provider returned - but every scored result would be "
                        + "recomputed against a different Gold identity.",
                    artifactsNamingCurrentGoldHash = new[]
                    {
                        "eval/a99-closed-loop/gold-current/registry.v1.json",
                        "eval/a99-closed-loop/occurrence-baseline-structured-v1/run.v1.json",
                        "eval/a99-closed-loop/occurrence-baseline-structured-v1/offline-score.v1.json",
                        "eval/a99-closed-loop/structured-baseline-forensics-v1/repeat2-precision-forensics.v1.json",
                        "eval/a99-closed-loop/structured-context-packing-preflight-v1/context-packing-intervention.v1.json",
                        "eval/a99-closed-loop/structured-context-packing-experiment-v2/DOC-0252/targeted-packing-rerun-v2-summary.v1.json",
                        "eval/a99-closed-loop/structured-context-packing-experiment-v2/DOC-0252/targeted-packing-rerun-v2-score.v1.json",
                    },
                    authorization = "DOC-0252's Gold carries approvalAuthority USER and userFinalApproval "
                        + "true, and the one prior correction of this kind (S0616) was named explicitly in "
                        + "its migration's authorization. This audit therefore stops at the proposal.",
                },
            },

            observedImpactOnScoredRuns = new
            {
                note = "What these four claims have been costing, measured from artifacts already committed "
                    + "rather than predicted.",
                fixed120Baseline = "all four appear in the baseline's missedInAllThree list, and three of "
                    + "them appear as stable false positives in the same runs - the model named the heading, "
                    + "Gold recorded a different end, and the pair was counted twice as an error.",
                coherentPackingRerunV2 = "two of the four fall inside target packs 5-7 and account for both "
                    + "false negatives and two of the seven stable false positives in every repeat.",
            },
        });

        // The audit's own conclusion, asserted so this file fails if the evidence ever stops saying it.
        Assert.NotEmpty(confirmed);
        Assert.Empty(review);
        Assert.All(confirmed, item => Assert.Equal(")", item.omittedTail));
        Assert.All(confirmed, item => Assert.EndsWith(item.omittedTail, item.approvedWording, StringComparison.Ordinal));
    }
}
