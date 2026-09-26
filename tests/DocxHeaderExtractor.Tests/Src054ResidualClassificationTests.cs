using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// SRC054_BLIND_GENERALIZATION_AUDIT_V1, protocol step 6: every residual of the committed raw held-out score (912dcc7)
/// gets exactly one cause, and every cause one bucket - A, B_KNOWN, B_NEW, C, D as for the V1.2 held-outs - plus one
/// bucket the protocol reserves for the reviewer's own mistakes: GOLD_REVISION_CANDIDATE, a residual whose cause is the
/// frozen Gold, found from source evidence while classifying. Those are proposed to the user as a Gold revision with its
/// own provenance; the raw score stays scored against the Gold pinned at the reveal. Neither V1.2 nor
/// PDF_SOURCE_FACTS_V2 is changed.
/// </summary>
public sealed class Src054ResidualClassificationTests
{
    private const string Score = "eval/a99-closed-loop/generic-audit-v1_2-pdf-facts-v2/SRC-054.blind-score.json";

    private sealed record Cause(string Id, string Bucket, string Reason, string[] GoldIdentities, string[] NonGoldIdentities);

    private static readonly Cause[] Causes =
    [
        new("ITALIC_STANDALONE_LABEL", "A",
            "pre-registered (S053_Q2, user-decided after V1.2 froze): an italic, not bold, label over its own prose is a heading by function; V1.2 sets italic apart only weakly and sends it to review - the fail-safe held for 12 of 13",
            ["L1370:S0:0-35", "L1389:S0:0-16", "L2110:S0:0-35", "L2129:S0:0-22", "L2145:S0:0-21", "L2196:S0:0-44", "L2233:S0:0-39",
             "L2262:S0:0-38", "L2419:S0:0-29", "L2421:S0:0-22", "L5542:S0:0-36", "L5546:S0:0-21", "L5557:S0:0-35"], []),
        new("COLON_ENDED_LABEL_OVER_PROSE", "A",
            "a colon-ended label over its prose is a heading or a field label by meaning; the engine sends it to review by design, and the fail-safe held for all three",
            ["L1754:S0:0-26", "L4589:S0:0-30", "L4604:S0:0-38"], []),
        new("COVER_ISSUER_VS_TITLE", "A",
            "which line of a cover block is the title and which the issuer is a meaning decision (S053_Q3, user-decided after V1.2 froze); the engine takes the second line of the two-line issuer name - the most prominent setting near the start, and again on the back cover - as a title",
            [], ["L0002:S0:0-15", "L6309:S0:0-15"]),
        new("APPENDIX_TABLE_TITLE_SCOPE", "A",
            "an unnumbered title that names only the table under it is an object title (SRC-041 A4, a scope decision); the engine reads it as a page-initial title",
            [], ["L3233:S0:0-65"]),
        new("METADATA_LINE_COUNTED_AS_PEER", "B_KNOWN",
            "N1: the one-line cover title, and its repeat atop the back cover, is followed by the issuer name set as large or larger; the region test counts that metadata as a peer and sends the title to review as 'opens nothing'",
            ["L0000:S0:0-21", "L6307:S0:0-21"], []),
        new("UNPREFIXED_TABLE_PART_CAPTION", "B_KNOWN",
            "K7: 'K8.1', the second part of Table K8 with its 'Table' prefix left off, has no caption shape and reads as a page-initial label",
            [], ["L6108:S0:0-4"]),
        new("UNMARKED_LABEL_NOT_A_CANDIDATE", "B_NEW",
            "a label no glyph fact sets apart - plain Times-Roman at body size (S054_Q1, 15) or a centred regular-weight report title (1) - is never a candidate: every V1.2 heading path starts from typographic prominence, and a label that only layout marks (standalone, at the margin or centred, over its own prose) has none",
            ["L0234:S0:0-16", "L0248:S0:0-21", "L0253:S0:0-19", "L0509:S0:0-21", "L0583:S0:0-22", "L0588:S0:0-19", "L0728:S0:0-19",
             "L0736:S0:0-19", "L1973:S0:0-28", "L1994:S0:0-27", "L2009:S0:0-39", "L2038:S0:0-28", "L2049:S0:0-59", "L2430:S0:0-34",
             "L2437:S0:0-25", "L3278:S0:0-42"], []),
        new("LESSER_LABEL_OR_CAPTION_COUNTED_AS_PEER", "B_NEW",
            "a heading followed directly by its own first sub-heading - at the same size, in bold or bold italic - or by a numbered figure caption: the region test counts that label as a peer, so the heading 'opens nothing' and goes to review. Under V1 facts these documents never showed it: their sub-levels differed in size",
            ["L0305:S0:0-25", "L0785:S0:0-56", "L1744:S0:0-15", "L4156:S0:0-15", "L4244:S0:0-25", "L4588:S0:0-37", "L5078:S0:0-10",
             "L5407:S0:0-23"], []),
        new("SMALL_CAPS_LINES_READ_AS_DIFFERENT_SIZES", "B_NEW",
            "the statement titles are set in simulated small caps - each word's initial at 18pt, the rest at 13.5pt - so a line's size under PDF_SOURCE_FACTS_V2, the mean of its glyphs' effective sizes, depends on how many initials it has (13.8 and 14.2 for the two lines of one title); the same-size test of the line join then splits every two-line title. A facts-layer aggregation gap: a per-character dominant size would read both lines as 13.5",
            ["L3858:S0:0-29|L3859:S0:0-30", "L3906:S0:0-29|L3907:S0:0-42", "L3952:S0:0-29|L3953:S0:0-42", "L3998:S0:0-29|L3999:S0:0-42",
             "L4043:S0:0-29|L4044:S0:0-42", "L4089:S0:0-29|L4090:S0:0-42"], ["L3859:S0:0-30"]),
        new("UNEMPHASISED_SUFFIX_READ_AS_RUN_IN_LEAD", "B_NEW",
            "'SUMMARY STATEMENT OF LOANS (Continued)': the suffix is set in regular weight after the bold title, so the title reads as the bold lead of a line that continues as body text - emphasis, not a label",
            ["L3804:S0:0-38"], []),
        new("CENTRED_TITLE_WRAP_JUDGED_UNFORCED", "B_NEW",
            "the two-line centred section title 'SECTION XIV: RECONCILIATIONS OF COMPONENTS OF ALLOCABLE / INCOME': the fit test (gap 3) judges the second line's first word would have fitted on the first, so the lines stay separate labels; a typesetter balancing a centred title wraps before the column is full",
            ["L2869:S0:0-55|L2870:S0:0-6"], ["L2869:S0:0-55", "L2870:S0:0-6"]),
        new("ATOM_JOINS_TITLE_WITH_NEIGHBOUR", "B_NEW",
            "one source atom carries a title and a neighbour's text - 'REPORTS' with the part's date, 'Equity-to-Loans Ratio' with the adjacent chart's unit line - and the engine proposes whole atoms or bold leads only: the first is proposed with its date, the second reads as a run-in lead",
            ["L0378:S0:0-21", "L3259:S0:0-41|L3260:S0:0-7"], ["L3260:S0:0-21"]),
        new("PERIOD_OR_UNIT_LINE_READ_AS_TITLE", "B_NEW",
            "a period line set larger than the body - 'June 30, 2025 and June 30, 2024' at 12pt, 'For the fiscal years ended ...', 'As of June 30, 2025, unless otherwise indicated' in bold italic - under a title reads as a title: V1.2 has a date shape for its line join but no metadata shape for period lines",
            [], ["L0033:S0:0-46", "L3529:S0:0-31", "L3563:S0:0-31", "L3597:S0:0-73", "L3633:S0:0-73", "L3647:S0:0-73", "L3685:S0:0-73"]),
        new("LETTERHEAD_READ_AS_PAGE_TITLE", "B_NEW",
            "the audit firm's letterhead name at the top of its report page ('Deloitte & Touche LLP') reads as the page's title",
            [], ["L3369:S0:0-21", "L3430:S0:0-21"]),
        new("BOLD_NOTE_SENTENCE_READ_AS_TITLE", "B_NEW",
            "a bold centred note of two lines ('The above information is qualified by the detailed information / and financial statements appearing elsewhere ...'): its first line, a sentence fragment, reads as a title",
            [], ["L0111:S0:0-62"]),
        new("BODY_SIZE_TABLE_LABELS", "B_NEW",
            "this report sets its tables, boxes and statements at body size, so their bold row and group labels ('Assets', 'Discount notes a', 'Credit Risk' in Box 5, 'Non trading portfolios, net') are set apart as strongly as headings; V1.2 has figure regions under numbered captions (gap 10) and a tabular flag for rows with figures, but no table region - a bold label alone on its line, with its figures on the next line or behind dot leaders, escapes both. The SRC-042/044 tables were set smaller than the body",
            [],
            ["L0269:S0:0-18|L0270:S0:0-20", "L0718:S0:0-75|L0719:S0:0-15", "L0768:S0:0-23", "L0797:S0:0-64", "L1085:S0:0-19|L1086:S0:0-20",
             "L1091:S0:0-12|L1092:S0:0-11", "L1139:S0:0-7", "L1228:S0:0-13|L1229:S0:0-10", "L1235:S0:0-12|L1236:S0:0-10",
             "L1240:S0:0-14|L1241:S0:0-18", "L1296:S0:0-30", "L1538:S0:0-16", "L1541:S0:0-53", "L1544:S0:0-29", "L1563:S0:0-10",
             "L1566:S0:0-18", "L1795:S0:0-11", "L1798:S0:0-11", "L1803:S0:0-16", "L3533:S0:0-26", "L3542:S0:0-11", "L3546:S0:0-33",
             "L3555:S0:0-12", "L3567:S0:0-23", "L3571:S0:0-85", "L3574:S0:0-17", "L3581:S0:0-20", "L3587:S0:0-77", "L3600:S0:0-16",
             "L3607:S0:0-57", "L3609:S0:0-20", "L3614:S0:0-21", "L3621:S0:0-64", "L3623:S0:0-70", "L3637:S0:0-33", "L3844:S0:0-5",
             "L4114:S0:0-5", "L4691:S0:0-77", "L4693:S0:0-17", "L4698:S0:0-22", "L5580:S0:0-6", "L5587:S0:0-11", "L5810:S0:0-27",
             "L5823:S0:0-27", "L5837:S0:0-27", "L5918:S0:0-29", "L5926:S0:0-25", "L5933:S0:0-28|L5934:S0:0-31", "L6033:S0:0-11"]),
        new("REVIEW_TOOL_ERROR", "GOLD_REVISION_CANDIDATE",
            "the source review's own rules erred, seen from source evidence while classifying: 'Total Guarantees and Credit Enhancements Received . . . .' is a table's total row with dot leaders, admitted as a heading because the prose test skipped the label after it; 'Results from Lending Activities' and 'Results from Investing activities' are page-initial 10pt bold section labels like their Gold sibling 'Results from Borrowing activities', rejected because the plain S054_Q1 label under each was not skipped. The engine is right on all three",
            ["L1312:S0:0-121"], ["L0508:S0:0-31", "L0582:S0:0-33"]),
    ];

    [Fact]
    public void Freeze_the_residual_classification()
    {
        using var score = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(Score)));
        var residuals = score.RootElement.GetProperty("residuals");
        var goldMisses = residuals.GetProperty("goldNotEngineTrue").EnumerateArray()
            .ToDictionary(x => x.GetProperty("goldIdentity").GetString()!, x => x.GetProperty("bucket").GetString()!, StringComparer.Ordinal);
        var nonGoldTrue = residuals.GetProperty("nonGoldTrue").EnumerateArray()
            .Select(x => x.GetProperty("hypothesis").GetProperty("identity").GetString()!).ToArray();
        Assert.Equal(goldMisses.Keys.Order(StringComparer.Ordinal), Causes.SelectMany(c => c.GoldIdentities).Order(StringComparer.Ordinal));
        Assert.Equal(nonGoldTrue.Order(StringComparer.Ordinal), Causes.SelectMany(c => c.NonGoldIdentities).Order(StringComparer.Ordinal));

        var headline = score.RootElement.GetProperty("headline");
        var tp = headline.GetProperty("truePositives").GetInt32();
        var fp = headline.GetProperty("falsePositives").GetInt32();
        var fn = headline.GetProperty("falseNegatives").GetInt32();
        var revision = Causes.Single(c => c.Bucket == "GOLD_REVISION_CANDIDATE");
        // If the user approves the revision: the two missed section labels become engine true positives, the total row leaves the Gold.
        var (rtp, rfp, rfn) = (tp + revision.NonGoldIdentities.Length, fp - revision.NonGoldIdentities.Length, fn - revision.GoldIdentities.Length);
        double Round(double v) => Math.Round(v, 4);
        var (rp, rr) = ((double)rtp / (rtp + rfp), (double)rtp / (rtp + rfn));

        FreezeArtifact.AssertJson("eval/a99-closed-loop/generic-audit-v1_2-pdf-facts-v2", "SRC-054.residual-classification.json", new
        {
            artifactKind = "a99_generic_audit_residual_classification",
            study = "SRC054_BLIND_GENERALIZATION_AUDIT_V1",
            rawScore = new { path = Score, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(Score)), commit = "912dcc7" },
            modelProviderVlmCalls = 0,
            status = "reviewer classification; the raw score is not edited; V1.2 and PDF_SOURCE_FACTS_V2 are not changed; the Gold revision is proposed, not applied",
            residuals = new { goldNotEngineTrue = goldMisses.Count, nonGoldTrue = nonGoldTrue.Length },
            byBucket = new[] { "A", "B_KNOWN", "B_NEW", "C", "D", "GOLD_REVISION_CANDIDATE" }.ToDictionary(b => b, b => new
            {
                goldNotEngineTrue = Causes.Where(c => c.Bucket == b).Sum(c => c.GoldIdentities.Length),
                nonGoldTrue = Causes.Where(c => c.Bucket == b).Sum(c => c.NonGoldIdentities.Length),
            }),
            byCause = Causes.Select(c => new
            {
                cause = c.Id,
                bucket = c.Bucket,
                c.Reason,
                goldNotEngineTrue = c.GoldIdentities.Length,
                goldBuckets = c.GoldIdentities.GroupBy(id => goldMisses[id]).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
                nonGoldTrue = c.NonGoldIdentities.Length,
                items = c.GoldIdentities.Concat(c.NonGoldIdentities).ToArray(),
            }).ToArray(),
            knownGapsRecurring = new[] { "N1 METADATA_LINE_COUNTED_AS_PEER", "K7 UNPREFIXED_TABLE_PART_CAPTION" },
            knownGapsNotObserved = new[] { "B2", "B4", "K1", "K2", "K3", "K4", "K5", "K6", "N2", "N3", "N4" },
            ontologyGaps = 0,
            documentSpecificExceptions = Array.Empty<string>(),
            facts = new
            {
                sourceFactsV1WouldRead = "size 1 and no bold on all 11955 atoms (5dc107c), the SRC-053 failure; PDF_SOURCE_FACTS_V2 gave the engine the document's typography",
                factsLayerGapFound = "SMALL_CAPS_LINES_READ_AS_DIFFERENT_SIZES: V2 aggregates a line's size as the mean of its glyphs' effective sizes",
            },
            goldRevisionCandidate = new
            {
                proposal = "remove L1312:S0 (a table total row); add L0508:S0 and L0582:S0 (section labels): 296 -> 297",
                needs = "user approval; a new Gold revision with its own provenance. The raw score above stays against the Gold pinned at the reveal (3d52729)",
                arithmeticAgainstRevision = new { truePositives = rtp, falsePositives = rfp, falseNegatives = rfn, precision = Round(rp), recall = Round(rr), f1 = Round(2 * rp * rr / (rp + rr)) },
            },
            gate = new
            {
                heldOutF1 = headline.GetProperty("f1").GetDouble(),
                ontologyGaps = 0,
                documentSpecificExceptions = 0,
                passed = false,
                basis = "the gate the user set on 2026-09-25 (D ~ 0, C ~ 0 and high F1): C = 0 and D = 0 hold; F1 0.80 is not high beside the V1.2 held-outs (SRC-042 0.978, SRC-044 0.982)",
                reading = "PDF_SOURCE_FACTS_V2 did its job - the source went from unreadable to F1 0.80 - and exposed ten new generic gaps, most of them in V1.2 itself, which this source reaches because its tables and sub-levels are set at body size",
            },
        });
    }
}
