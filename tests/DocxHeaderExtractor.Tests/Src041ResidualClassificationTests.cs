using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// SRC041_BLIND_GENERALIZATION_AUDIT_V1, protocol step 6: every residual of the committed raw held-out score
/// (ba93900) gets exactly one cause, and every cause one bucket - A (a meaning decision the ontology
/// expresses and the evidence cannot settle), B_KNOWN (a gap pre-registered in 35a2040), B_NEW (a new generic
/// engine gap), C (an ontology gap), D (a document-specific exception). The raw score is not edited and V1.1
/// is not changed. These are the reviewer's readings for the user to confirm or overrule, cause by cause.
/// </summary>
public sealed class Src041ResidualClassificationTests
{
    private const string Score = "eval/a99-closed-loop/generic-audit-v1_1/SRC-041.blind-score.v1_1.json";

    private sealed record Cause(string Id, string Bucket, string Reason, bool IntroducedByV1_1, string[] GoldIdentities, string[] NonGoldIdentities);

    private static readonly Cause[] Causes =
    [
        new("FURNITURE_RULE_IGNORES_PROMINENCE", "B_NEW",
            "a statement title carried to the top of each page of its statement repeats in the top band on 3+ pages, and the furniture rule calls it page furniture whatever its size; at 16pt against a 10pt body it is a continued title, not a running header",
            false,
            ["L3526:S0:0-29|L3527:S0:0-29", "L3585:S0:0-29|L3586:S0:0-41", "L3644:S0:0-29|L3645:S0:0-41", "L3703:S0:0-29|L3704:S0:0-41"],
            ["L3527:S0:0-29"]),
        new("TWO_SEGMENT_ROW_CHART_PANEL", "B_NEW",
            "gap 5 makes each segment of a two-segment row stand alone in its column; a chart or diagram panel beside the text (panel titles, node labels) is not a column of text, and its bold labels become standalone headings",
            true, [],
            ["L0379:S1:0-3", "L0389:S1:0-21", "L0401:S1:0-10", "L0416:S1:0-21", "L0546:S1:0-15", "L1815:S1:0-3", "L1820:S1:0-3"]),
        new("CHART_INTERNAL_LABEL", "B_NEW",
            "a set-apart label inside a chart (series legend, axis labels, a panel title set larger than the body): the engine has no figure-region evidence, and the principles make chart-internal labels false whatever their type",
            false, [],
            ["L0229:S0:0-12", "L0362:S0:0-24", "L2105:S0:0-24", "L2490:S0:0-49"]),
        new("CAPTION_CONTINUATION_LINE", "B_NEW",
            "the wrapped second line of a numbered figure caption is judged alone: the caption shape is matched on its first line only",
            false, [], ["L1655:S0:0-18"]),
        new("LIST_ENTRY_CHAIN", "B_NEW",
            "gap 1 follows a label down its column and, where another column bounds it (here the contents page numbers), takes each same-set line below as a wrap; a contents sub-list opener was joined to the complete entries under it",
            true, ["L3169:S0:0-25"], []),
        new("COVER_TITLE_DELIBERATE_BREAKS", "B_NEW",
            "a centred cover title set in three lines by design, the middle one a single connective word; the fit test ends the claim at the short line, which fits, although the break is typographic",
            false, ["L0002:S0:0-34|L0003:S0:0-3|L0004:S0:0-20"], []),
        new("REPEATED_TITLE_READ_AS_POINTER", "B_KNOWN_B1",
            "the report title's text recurs later (on a continuation page) at equal prominence, so the contents logic reads the first occurrence as a pointer to it - the B1 mechanism (repeated text turns a heading into a navigation entry), here on a report title rather than a contents opener",
            false, ["L3182:S0:0-27"], []),
        new("COLON_ENDED_LABEL_OVER_PROSE", "A",
            "a colon-ended standalone label over prose is a heading or a field label by meaning; the engine sends it to review by design, and the fail-safe held for all seven",
            false,
            ["L0816:S0:0-56", "L1822:S0:0-26", "L1848:S0:0-28", "L2514:S0:0-29", "L2517:S0:0-22", "L4248:S0:0-31", "L4264:S0:0-38"], []),
        new("CONTINUED_TITLE_OVER_IMAGE_PAGE", "A",
            "a report title repeated on a later page whose body is an image: nothing below it is text, so the engine cannot see a region; the user decided it is a heading (A2). Sent to review",
            false, ["L3185:S0:0-27"], []),
        new("OCCURRENCE_VS_RUNNING_HEADER", "A",
            "the same text, type and position tops pages 3, 4 and 5; only the first occurrence opens the part (user decision A3). Source evidence is identical across the three; the region role is the meaning decision",
            false, ["L0006:S0:0-36"], []),
        new("LIST_TITLE_SCOPE", "A",
            "a 14pt title whose scope is the country list below it (user decision A4: an ordinary local object title, false); the engine has no evidence of that scope",
            false, [], ["L3139:S0:0-64"]),
        new("UNPREFIXED_TABLE_PART_LABEL", "D",
            "'K8.1' - the only one of twelve table-continuation labels in this PDF set without the word 'Table'; the other eleven match the caption shape. A typesetting slip of this document",
            false, [], ["L5818:S0:0-4"]),
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

        // Every residual is classified exactly once, and nothing that is not a residual.
        var classifiedGold = Causes.SelectMany(c => c.GoldIdentities).ToArray();
        var classifiedNonGold = Causes.SelectMany(c => c.NonGoldIdentities).ToArray();
        Assert.Equal(goldMisses.Keys.Order(StringComparer.Ordinal), classifiedGold.Order(StringComparer.Ordinal));
        Assert.Equal(nonGoldTrue.Order(StringComparer.Ordinal), classifiedNonGold.Order(StringComparer.Ordinal));

        var reviewNoise = residuals.GetProperty("nonGoldNeedsReview").EnumerateArray()
            .Where(x => !x.GetProperty("touchesGold").GetBoolean())
            .Select(x => string.Join(" + ", x.GetProperty("hypothesis").GetProperty("Evidence").EnumerateArray().Select(e => e.GetString()!)
                .Where(e => !e.StartsWith("followed by", StringComparison.Ordinal) && !e.StartsWith("set apart", StringComparison.Ordinal))
                .DefaultIfEmpty("(set apart only)")))
            .GroupBy(e => e).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new { evidence = g.Key, count = g.Count() }).ToArray();

        FreezeArtifact.AssertJson("eval/a99-closed-loop/generic-audit-v1_1", "SRC-041.residual-classification.v1_1.json", new
        {
            artifactKind = "a99_generic_audit_residual_classification",
            study = "SRC041_BLIND_GENERALIZATION_AUDIT_V1",
            rawScore = new { path = Score, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(Score)), commit = "ba93900" },
            modelProviderVlmCalls = 0,
            status = "reviewer proposals for the user to confirm or overrule, cause by cause; the raw score is not edited and V1.1 is not changed",
            residuals = new { goldNotEngineTrue = goldMisses.Count, nonGoldTrue = nonGoldTrue.Length },
            byBucket = new[] { "A", "B_KNOWN_B1", "B_NEW", "C", "D" }.ToDictionary(b => b, b => new
            {
                goldNotEngineTrue = Causes.Where(c => c.Bucket == b).Sum(c => c.GoldIdentities.Length),
                nonGoldTrue = Causes.Where(c => c.Bucket == b).Sum(c => c.NonGoldIdentities.Length),
            }),
            byCause = Causes.Select(c => new
            {
                cause = c.Id,
                bucket = c.Bucket,
                c.Reason,
                introducedByV1_1 = c.IntroducedByV1_1,
                goldNotEngineTrue = c.GoldIdentities.Length,
                goldBuckets = c.GoldIdentities.GroupBy(id => goldMisses[id]).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
                nonGoldTrue = c.NonGoldIdentities.Length,
                items = c.GoldIdentities.Concat(c.NonGoldIdentities).ToArray(),
            }).ToArray(),
            knownGapsRecurring = new[] { new { id = "B1", cases = 1, note = "on a report title whose text recurs on its continuation page" } },
            knownGapsNotObserved = new[] { "B2", "B4", "K1 cross-page titles", "K2 stacked cover titles of different sizes", "K3 connective lines", "K4 DOCX page breaks", "K5 FORM without fill-ins" },
            introducedByV1_1 = new
            {
                causes = Causes.Where(c => c.IntroducedByV1_1).Select(c => c.Id).ToArray(),
                goldNotEngineTrue = Causes.Where(c => c.IntroducedByV1_1).Sum(c => c.GoldIdentities.Length),
                nonGoldTrue = Causes.Where(c => c.IntroducedByV1_1).Sum(c => c.NonGoldIdentities.Length),
                note = "side effects of V1.1's own mechanisms (gap 5 two-segment rows; gap 1 column chain beside a bounding column) - recorded, not fixed here",
            },
            ontologyGaps = 0,
            documentSpecificExceptions = Causes.Where(c => c.Bucket == "D").Select(c => new { cause = c.Id, c.Reason, items = c.NonGoldIdentities.Concat(c.GoldIdentities).ToArray() }).ToArray(),
            axisFindings = new[]
            {
                new
                {
                    axis = "scope / primaryFunction / semanticFunctions",
                    bucket = "A (pre-registered)",
                    finding = "on exact matches the engine says SECTION / STRUCTURE where Gold says NOTE (15), FINANCIAL_STATEMENT (7), DOCUMENT_PART (4), TOC (3); pre-registered as meaning decisions the engine has no evidence for. occurrenceRoles, titleRelation and informationType agree on every exact match",
                },
            },
            reviewNoiseProfile = new
            {
                note = "NEEDS_REVIEW outside Gold that touches no Gold claim; it never counts as a heading and costs reviewer time only",
                total = reviewNoise.Sum(r => r.count),
                byEvidence = reviewNoise,
            },
        });
    }
}
