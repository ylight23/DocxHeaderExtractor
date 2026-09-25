using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// SRC053_BLIND_GENERALIZATION_AUDIT_V1, protocol step 6: every residual of the committed raw held-out score
/// (bf15b80) gets exactly one cause, and every cause one bucket - A, B_KNOWN, B_NEW, C, D as for SRC-042 / SRC-044.
/// <para>
/// One cause explains all of them, and the test proves it from the committed evidence rather than listing it by
/// hand: the evidence profile of the blind run (d3152ce, before any Gold) shows every atom at font size 1 and not
/// bold, and no residual carries typographic evidence of being set apart. The source-only review read the glyphs
/// directly (atom-glyph-facts.tsv) and found 10-24pt Times-Bold headings. The raw score is not edited and neither V1.2
/// nor the extraction is changed: a fix is a new pre-registered version measured on a source it has not seen.
/// </para>
/// </summary>
public sealed class Src053ResidualClassificationTests
{
    private const string Score = "eval/a99-closed-loop/generic-audit-v1_2/SRC-053.blind-score.v1_2.json";
    private const string Profile = "eval/a99-closed-loop/generic-audit-v1_2/SRC-053.evidence-profile.v1_2.json";
    private const string GlyphFacts = "eval/a99-closed-loop/source-review-v1/SRC-053/atom-glyph-facts.tsv";

    [Fact]
    public void Freeze_the_residual_classification()
    {
        using var score = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(Score)));
        using var profile = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(Profile)));
        var residuals = score.RootElement.GetProperty("residuals");
        var goldMisses = residuals.GetProperty("goldNotEngineTrue").EnumerateArray().ToArray();
        var nonGoldTrue = residuals.GetProperty("nonGoldTrue").GetArrayLength();

        // What the engine was given: one typography cluster at size 1, never bold.
        var clusters = profile.RootElement.GetProperty("typographyClusters").EnumerateArray().ToArray();
        Assert.All(clusters, c => Assert.Equal(1.0, c.GetProperty("fontSize").GetDouble()));
        Assert.All(clusters, c => Assert.False(c.GetProperty("bold").GetBoolean()));
        Assert.Equal(profile.RootElement.GetProperty("occurrences").GetInt32(), clusters.Sum(c => c.GetProperty("occurrences").GetInt32()));

        // What the glyphs say: several sizes and bold faces.
        var glyphs = File.ReadAllLines(TestRepository.Path(GlyphFacts)).Select(l => l.Split('\t')).ToArray();
        var sizes = glyphs.Select(g => g[4]).Distinct().Count();
        var boldAtoms = glyphs.Count(g => g[5].Contains("Bold", StringComparison.Ordinal));
        Assert.True(sizes > 10 && boldAtoms > 500);

        // No residual was set apart strongly: the one signal every heading rule starts from was missing.
        var evidence = goldMisses.SelectMany(m => m.GetProperty("engine").EnumerateArray())
            .SelectMany(e => e.GetProperty("Evidence").EnumerateArray().Select(x => x.GetString()!)).ToArray();
        Assert.DoesNotContain(evidence, e => e.StartsWith("set apart strongly", StringComparison.Ordinal));
        Assert.Equal(0, nonGoldTrue);

        var buckets = goldMisses.GroupBy(m => m.GetProperty("bucket").GetString()!)
            .OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count());

        FreezeArtifact.AssertJson("eval/a99-closed-loop/generic-audit-v1_2", "SRC-053.residual-classification.v1_2.json", new
        {
            artifactKind = "a99_generic_audit_residual_classification",
            study = "SRC053_BLIND_GENERALIZATION_AUDIT_V1",
            rawScore = new { path = Score, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(Score)), commit = "bf15b80" },
            modelProviderVlmCalls = 0,
            status = "reviewer classification; the raw score is not edited; V1.2 and the extraction are not changed",
            residuals = new { goldNotEngineTrue = goldMisses.Length, nonGoldTrue },
            byBucket = new[] { "A", "B_KNOWN", "B_NEW", "C", "D" }.ToDictionary(b => b, b => new
            {
                goldNotEngineTrue = b == "B_NEW" ? goldMisses.Length : 0,
                nonGoldTrue = 0,
            }),
            byCause = new[]
            {
                new
                {
                    cause = "PDF_TEXT_MATRIX_TYPOGRAPHY_NOT_READ",
                    bucket = "B_NEW",
                    layer = "production PDF line extraction (PdfLineExtraction), upstream of the audit engine",
                    reason = "this PDF sets its type size through the text matrix (Tf 1, scaled by Tm). The lane reads Letter.FontSize - the Tf operand - and bold only from FontDetails.IsBold, so every atom reached the engine at size 1 and not bold. The glyphs themselves (PdfPig Letter.PointSize, font names Times-Bold / Times-Roman) carry 10-24pt bold headings over 9-10pt body. Without size or weight nothing is 'set apart strongly', so no occurrence can be TRUE and most are never proposed",
                    generic = "any PDF whose producer scales text by the matrix rather than the font size; no document-specific trait",
                    goldNotEngineTrue = goldMisses.Length,
                    goldBuckets = buckets,
                    nonGoldTrue = 0,
                    recordedBeforeGold = "d3152ce (evidence profile noted in the blind-run commit)",
                },
            },
            secondaryCausesObservable = false,
            secondaryCausesNote = "with the primary evidence missing for every atom, no other cause can be observed on this source; the A / B_KNOWN causes of SRC-042 / SRC-044 may be present underneath",
            knownGapsRecurring = Array.Empty<string>(),
            ontologyGaps = 0,
            documentSpecificExceptions = Array.Empty<string>(),
            gate = new
            {
                heldOutF1 = score.RootElement.GetProperty("headline").GetProperty("f1").GetDouble(),
                ontologyGaps = 0,
                documentSpecificExceptions = 0,
                passed = false,
                basis = "the gate the user set on 2026-09-25 (D ~ 0, C ~ 0 and high F1): C = 0 and D = 0 hold, F1 does not",
                consequence = "the chain continues to SRC-054 only if the gate passes; it stops here for the user's decision",
            },
        });
    }
}
