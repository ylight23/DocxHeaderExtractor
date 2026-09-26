using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// SRC089_BLIND_GENERALIZATION_AUDIT_V1, protocol step 6: every residual of the committed raw held-out score (59be187)
/// gets exactly one cause, and every cause one bucket - A, B_KNOWN, B_NEW, C, D, GOLD_REVISION_CANDIDATE as for SRC-095.
/// Causes are read from the source's glyph facts (source-review-v1/SRC-089/) and the frozen V1.4 decision path. Neither
/// GENERIC_AUDIT_ENGINE_V1.4 nor PDF_SOURCE_FACTS_V3 is changed: V1.4 was the last development cycle, and by the user's
/// rule (2026-09-26) a gate failing on real semantic or generalization gaps goes to the verdict with its limitation
/// stated; only a source-representation defect that invalidates the measurement would defer it.
/// </summary>
public sealed class Src089ResidualClassificationTests
{
    private const string Root = Src089BlindGeneralizationTests.Root;
    private const string Score = Root + "/SRC-089.blind-score.json";

    private sealed record Cause(string Id, string Bucket, string Reason, string[] GoldIdentities, string[] NonGoldIdentities);

    private static readonly Cause[] Causes =
    [
        new("NUMBER_LEAD_WITH_UNEMPHASISED_TITLE", "B_NEW",
            "every article heading sets only its number in bold ('Article 1.') and its title in regular weight on the same line, wrapping onto lines that begin in lower case. V1.4's run-in rule (gap 2) takes the bold lead for the whole label and the rest of the line for body text, and the lead, ending in a period, then fails the short-label test as a sentence: FALSE on the number alone. The engine has no reading in which a structural number's label runs on in regular weight as its title - the heading is the number with its title, over the article's clauses",
            ["L0016:S0:0-56", "L0024:S0:0-89|L0025:S0:0-45", "L0051:S0:0-95|L0052:S0:0-35", "L0080:S0:0-90|L0081:S0:0-35", "L0094:S0:0-70",
             "L0106:S0:0-80|L0107:S0:0-88|L0108:S0:0-13", "L0126:S0:0-86|L0127:S0:0-88|L0128:S0:0-13", "L0162:S0:0-86",
             "L0174:S0:0-89|L0175:S0:0-51", "L0195:S0:0-81", "L0243:S0:0-40", "L0260:S0:0-62", "L0286:S0:0-89|L0287:S0:0-41",
             "L0317:S0:0-81", "L0350:S0:0-95", "L0359:S0:0-95", "L0384:S0:0-71", "L0426:S0:0-96|L0427:S0:0-44",
             "L0455:S0:0-90|L0456:S0:0-27", "L0478:S0:0-59", "L0490:S0:0-36", "L0499:S0:0-92|L0500:S0:0-58",
             "L0522:S0:0-92|L0523:S0:0-12", "L0537:S0:0-18", "L0555:S0:0-93|L0556:S0:0-12", "L0572:S0:0-41"], []),
        new("CHAPTER_LABEL_AND_TITLE_ONE_CLAIM", "A",
            "S089_Q2 (user-decided after V1.4 froze, no precedent): 'Chapter I' at the margin and its title centred on the next line are one heading of two parts. V1.4 proposes each line as a heading of its own - the alternative the question put - so each chapter is one partial Gold miss and two non-Gold TRUEs",
            ["L0014:S0:0-9|L0015:S0:0-18", "L0160:S0:0-10|L0161:S0:0-10", "L0284:S0:0-11|L0285:S0:0-41", "L0382:S0:0-10|L0383:S0:0-45", "L0535:S0:0-9|L0536:S0:0-25"],
            ["L0014:S0:0-9", "L0015:S0:0-18", "L0160:S0:0-10", "L0161:S0:0-10", "L0284:S0:0-11", "L0285:S0:0-41", "L0382:S0:0-10", "L0383:S0:0-45", "L0535:S0:0-9", "L0536:S0:0-25"]),
        new("DECREE_TITLE_COMPOSITION", "A",
            "S089_Q1 (user-decided after V1.4 froze, no precedent): the title is 'DECREE' with the two regular capital lines under it, without the footnote mark. V1.4 proposes the bold 'DECREE' alone and sends the two lines below to review - one of the readings the question put",
            ["L0006:S0:0-6|L0007:S0:0-65|L0008:S0:0-19"], ["L0006:S0:0-6"]),
        new("COLON_CLAUSE_LABEL_MEANING", "A",
            "S089_Q3 (user-decided TRUE after V1.4 froze, against the reviewer's proposal): a numbered clause that is only a short noun phrase ending in a colon is a sub-heading of its article. Colon-ended labels are a meaning decision by design; here the fail-safe did not hold - plain body-size clauses set at the margin like every other clause are no candidate (gap 14 excludes colon lead-ins), so they are unproposed, not in review",
            ["L0109:S0:0-33", "L0117:S0:0-22", "L0246:S0:0-26", "L0249:S0:0-16"], []),
        new("DOCUMENT_STATUS_NOTICE_READ_AS_TITLE", "B_NEW",
            "'Unofficial Translation - For Reference Purposes Only', bold italic 14pt at the top right of p1 - the most prominent setting near the start - is proposed as the document's title; the engine has no reading of a notice about the document's status",
            [], ["L0000:S0:0-24|L0001:S0:0-27"]),
        new("SIGNATURE_NAME_READ_AS_LABEL", "B_NEW",
            "the signatory's name 'Nguyen Tan Dung', bold at the right under 'PRIME MINISTER' after the last article, reads as a label over the footnote; gap 25 (contact blocks) needs a contact line and gap 20 (letterheads) short lines at its own left edge - a signature block has neither",
            [], ["L0578:S0:0-15"]),
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
        FreezeArtifact.AssertJson(Root, "SRC-089.residual-classification.json", new
        {
            artifactKind = "a99_generic_audit_residual_classification",
            study = "SRC089_BLIND_GENERALIZATION_AUDIT_V1",
            rawScore = new { path = Score, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(Score)), commit = "59be187" },
            modelProviderVlmCalls = 0,
            status = "reviewer classification; the raw score is not edited; GENERIC_AUDIT_ENGINE_V1.4 and PDF_SOURCE_FACTS_V3 are not changed; no Gold revision is proposed",
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
            ontologyGaps = 0,
            documentSpecificExceptions = Array.Empty<string>(),
            measurementValidity = new
            {
                sourceRepresentation = "VALID - PDF_SOURCE_FACTS_V3 reads this PDF's typography (12pt body; the bold article numbers, chapter lines and title are seen as bold, which is why the engine proposes them); not an SRC-053-type defect",
                reading = "F1 0 is not a measurement failure: every Gold heading but the four clause labels was found at its place (32 partial matches); what failed is the claim's extent - the number without its title, or a heading's lines apart",
            },
            gate = new
            {
                heldOutF1 = headline.GetProperty("f1").GetDouble(),
                partialMatchesOfGold = 32,
                ontologyGaps = 0,
                documentSpecificExceptions = 0,
                passed = false,
                basis = "the gate the user set on 2026-09-25 (D ~ 0, C ~ 0 and high F1): C = 0 and D = 0 hold; F1 0 is not high",
                consequence = "user rule 2026-09-26: the gate failed on real generalization gaps, not on a source-representation defect, so the study goes to the cross-document summary, the hierarchy evaluation and the final verdict, with this limitation stated; V1.4 is not tuned on this held-out",
            },
        });
    }
}
