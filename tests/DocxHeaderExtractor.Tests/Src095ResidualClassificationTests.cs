using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// SRC095_BLIND_GENERALIZATION_AUDIT_V1, protocol step 6: every residual of the committed raw held-out score (3b87711)
/// gets exactly one cause, and every cause one bucket - A, B_KNOWN, B_NEW, C, D, GOLD_REVISION_CANDIDATE as for SRC-054.
/// Causes are read from the source's glyph facts (source-review-v1/SRC-095/atom-glyph-facts.tsv) and the frozen V1.3
/// decision path. Neither GENERIC_AUDIT_ENGINE_V1.3 nor PDF_SOURCE_FACTS_V3 is changed.
/// </summary>
public sealed class Src095ResidualClassificationTests
{
    private const string Root = Src095BlindGeneralizationTests.Root;
    private const string Score = Root + "/SRC-095.blind-score.json";

    private sealed record Cause(string Id, string Bucket, string Reason, string[] GoldIdentities, string[] NonGoldIdentities);

    private static readonly Cause[] Causes =
    [
        new("INDEX_GROUP_LETTER_MEANING", "A",
            "S095_Q2 (user-decided after V1.3 froze, no precedent): a single regular-weight letter opening its group of index entries is a heading by function; no glyph fact sets it apart and V1.3 sends it to review - the fail-safe held for 7 of 8 (5 as region openers, D and S as 'opens nothing' because the all-caps index term under each counts as a peer)",
            ["L1914:S0:0-1", "L1940:S0:0-1", "L1961:S0:0-1", "L2012:S0:0-1", "L2023:S0:0-1", "L2056:S0:0-1", "L2063:S0:0-1"], []),
        new("SPLIT_SECTION_NUMBER_READ_AS_FIGURES", "B_NEW",
            "where xml2rfc sets a section number apart from its title ('4.6.' | 'Server Push', two segments of one row), V1.3 assembles the two parts exactly but tests the figures-only shape on the first part alone: '4.6.' is digits and dots, so the label is emitted FALSE as a figure. The test belongs on the label's whole text. All six split-row sections, each assembled exactly",
            ["L0608:S0:0-4|L0608:S1:0-11", "L0944:S0:0-4|L0944:S1:0-16", "L1320:S0:0-5|L1320:S1:0-32", "L1431:S0:0-5|L1431:S1:0-10",
             "L1465:S0:0-5|L1465:S1:0-14", "L1613:S0:0-5|L1613:S1:0-20"], []),
        new("INDEX_LOCATOR_WRAP_READ_AS_CAPTION", "B_NEW",
            "an index entry's locator list wraps at 'Table 2; Appendix A.1, ...': the wrapped line begins with the numbered caption shape, so it opens a figure region and the next group letter 'G' reads as a label inside a figure (FALSE, where its siblings went to review). The same shape puts two more locator lines in review as captions",
            ["L1947:S0:0-1"], []),
        new("INDEX_ENTRY_READ_AS_CONTENTS_OPENER", "B_NEW",
            "'H3_REQUEST_INCOMPLETE' is an index term with its locators ('Section 4.1, Paragraph 14; Section 8.1; Table 4') in the second segment of its row; the locators read as a run of contents entries, so the term is proposed as the opener of a contents list",
            [], ["L1994:S0:0-21"]),
        new("EMPHASISED_CROSS_REFERENCE_READ_AS_STRUCTURAL_LABEL", "B_NEW",
            "the index sets an entry's primary locator in bold italic; where that locator begins a wrapped line ('Section 4.6; Section 5.2, Paragraph 1; ...') it is a bold lead with the numbered structural label shape, and V1.3 proposes it as a run-in section label. A cross-reference is not a label: nothing follows it but more locators",
            [], ["L2025:S0:0-11"]),
        new("AUTHOR_NAME_READ_AS_SECTION_LABEL", "B_NEW",
            "the author's name, 10pt bold at the margin under 'Author's Address', over the affiliation and email lines, reads as a label over its own two body lines; the gap-20 letterhead test covers an address block set right of the page centre, not one at the text margin",
            [], ["L2077:S0:0-20"]),
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
        var axes = score.RootElement.GetProperty("axesOnTruePositives");

        FreezeArtifact.AssertJson(Root, "SRC-095.residual-classification.json", new
        {
            artifactKind = "a99_generic_audit_residual_classification",
            study = "SRC095_BLIND_GENERALIZATION_AUDIT_V1",
            rawScore = new { path = Score, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(Score)), commit = "3b87711" },
            modelProviderVlmCalls = 0,
            status = "reviewer classification; the raw score is not edited; GENERIC_AUDIT_ENGINE_V1.3 and PDF_SOURCE_FACTS_V3 are not changed; no Gold revision is proposed",
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
            knownGapsRecurring = Array.Empty<string>(),
            knownGapsNotObserved = new[] { "B2", "B4", "K1", "K2", "K3", "K4", "K5", "K6", "K7", "N1", "N2", "N3", "N4" },
            ontologyGaps = 0,
            documentSpecificExceptions = Array.Empty<string>(),
            axesOnTruePositives = new
            {
                compared = axes.GetProperty("compared").GetInt32(),
                semanticFunctionsDisagreements = axes.GetProperty("semanticFunctions").GetProperty("disagreements").GetInt32(),
                otherAxesDisagreements = 0,
                reading = "primaryFunction, scope, roles, titleRelation and informationType agree on all 89; the 64 semanticFunctions pairs are the secondary IDENTITY function: V1.3 adds it to numbered structural labels and not to unnumbered ones, the Gold to top-level and front/back matter sections only (58 numbered sub-sections one way, 6 unnumbered matter sections the other). Not a residual; recorded for the hierarchy evaluation",
            },
            gate = new
            {
                heldOutF1 = headline.GetProperty("f1").GetDouble(),
                truePrecision = headline.GetProperty("truePrecision").GetDouble(),
                ontologyGaps = 0,
                documentSpecificExceptions = 0,
                passed = false,
                basis = "the gate the user set on 2026-09-25 (D ~ 0, C ~ 0 and high F1): C = 0 and D = 0 hold; F1 0.913 is below the V1.2 held-outs (SRC-042 0.978, SRC-044 0.982)",
                reading = "precision held (0.967, 3 false positives, all in the index or the author block); recall lost 6 of 14 to one generic defect - the figures-only test on the first part of an assembled label - and 7 to a meaning decision the fail-safe sent to review. Five new B causes, three of them in the index: back matter no development source had",
            },
        });
    }
}
