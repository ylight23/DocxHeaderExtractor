using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// SRC044_BLIND_GENERALIZATION_AUDIT_V1, protocol step 6: every residual of the committed raw held-out score
/// (382c58c) gets exactly one cause, and every cause one bucket - A (a meaning decision the ontology expresses
/// and the evidence cannot settle), B_KNOWN (pre-registered in 370447e), B_NEW (a new generic engine gap),
/// C (an ontology gap), D (a document-specific exception). The raw score is not edited and V1.2 is not changed.
/// Also recorded: the same check as SRC-042's Box 2 one, on this report's box title (S042_A1 precedent).
/// </summary>
public sealed class Src044ResidualClassificationTests
{
    private const string Score = "eval/a99-closed-loop/generic-audit-v1_2/SRC-044.blind-score.v1_2.json";
    private const string Proposals = "eval/a99-closed-loop/generic-audit-v1_2/SRC-044.proposals.v1_2.json";

    private sealed record Cause(string Id, string Bucket, string Reason, string[] GoldIdentities, string[] NonGoldIdentities);

    private static readonly Cause[] Causes =
    [
        new("COLON_ENDED_LABEL_OVER_PROSE", "A",
            "a colon-ended standalone label over its prose is a heading or a field label by meaning; the engine sends it to review by design, and the fail-safe held for both (as on SRC-044)",
            ["L1527:S0:0-26", "L1551:S0:0-28"], []),
        new("CONTINUED_TITLE_OVER_IMAGE_PAGE", "A",
            "the auditor's report title repeated on a later page whose body is an image: no text region is visible below it; a heading by the A2 precedent. Sent to review (as on SRC-044)",
            ["L2258:S0:0-27"], []),
        new("OCCURRENCE_VS_RUNNING_HEADER", "A",
            "the same text, type and position tops pages 3 and 4; only the first occurrence opens the part (A3 precedent). Source evidence is identical across the two; the engine read both as furniture (as on SRC-044)",
            ["L0005:S0:0-36"], []),
        new("METADATA_LINE_COUNTED_AS_PEER", "B_KNOWN",
            "N1 (pre-registered from SRC-044): the three-line cover title, assembled exactly, is followed by the reporting date set at the same size; the region test counts the date as a peer and sends the title to review as 'opens nothing'",
            ["L0001:S0:0-34|L0002:S0:0-3|L0003:S0:0-20"], []),
        new("BARE_BULLET_GLYPH", "B_KNOWN",
            "N2 (pre-registered from SRC-044): a bullet glyph set bold in its own segment beside the item's text stands alone as a label",
            [], ["L0624:S0:0-1"]),
        new("CHART_WITHOUT_NUMBERED_CAPTION", "B_KNOWN",
            "K6 (pre-registered): a chart set in the right column beside prose, under an unnumbered unit line; its bold series label has no figure region to fall in and reads as a region opener",
            [], ["L0172:S0:0-16"]),
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

        // The S042_A1 check: what the engine proposed for "Box 1: Financing Principles".
        using var proposals = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(Proposals)));
        var box = proposals.RootElement.GetProperty("hypotheses").EnumerateArray()
            .Single(h => h.GetProperty("Text").GetString()!.StartsWith("Box 1:", StringComparison.Ordinal));

        var reviewNoise = residuals.GetProperty("nonGoldNeedsReview").EnumerateArray()
            .Where(x => !x.GetProperty("touchesGold").GetBoolean())
            .Select(x => string.Join(" + ", x.GetProperty("hypothesis").GetProperty("Evidence").EnumerateArray().Select(e => e.GetString()!)
                .Where(e => !e.StartsWith("followed by", StringComparison.Ordinal) && !e.StartsWith("set apart", StringComparison.Ordinal))
                .DefaultIfEmpty("(set apart only)")))
            .GroupBy(e => e).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new { evidence = g.Key, count = g.Count() }).ToArray();

        FreezeArtifact.AssertJson("eval/a99-closed-loop/generic-audit-v1_2", "SRC-044.residual-classification.v1_2.json", new
        {
            artifactKind = "a99_generic_audit_residual_classification",
            study = "SRC044_BLIND_GENERALIZATION_AUDIT_V1",
            rawScore = new { path = Score, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(Score)), commit = "382c58c" },
            modelProviderVlmCalls = 0,
            status = "reviewer proposals for the user to confirm or overrule, cause by cause; the raw score is not edited and V1.2 is not changed",
            residuals = new { goldNotEngineTrue = goldMisses.Count, nonGoldTrue = nonGoldTrue.Length },
            byBucket = new[] { "A", "B_KNOWN", "B_NEW", "C", "D" }.ToDictionary(b => b, b => new
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
            knownGapsRecurring = new[] { "N1 METADATA_LINE_COUNTED_AS_PEER", "N2 BARE_BULLET_GLYPH", "K6 CHART_WITHOUT_NUMBERED_CAPTION" },
            knownGapsNotObserved = new[] { "B2", "B4", "K1", "K2", "K3", "K4", "K5", "K7", "N3", "N4" },
            ontologyGaps = 0,
            documentSpecificExceptions = Array.Empty<string>(),
            boxTitleCheck = new
            {
                occurrence = "Box 1: Financing Principles (L0950:S0), Gold FALSE by the S042_A1 precedent (revised 336c6b6)",
                engineProposal = box.GetProperty("ProposedIsHeading").GetString(),
                engineEvidence = box.GetProperty("Evidence").EnumerateArray().Select(e => e.GetString()!).ToArray(),
                outcome = box.GetProperty("ProposedIsHeading").GetString() switch
                {
                    "TRUE" => "TRUE - an error; to be split into SEMANTIC_REASONING_ERROR or CONTEXT_PACKING_GAP",
                    "FALSE" => "FALSE - handled correctly",
                    _ => "NEEDS_REVIEW - not a heading in the score (no false positive): the engine saw the caption shape over prose and deferred to review",
                },
                scopeNote = "this measures the offline audit engine V1.2; the production LLM request's context packing is not exercised here",
            },
            axisFindings = new[]
            {
                new
                {
                    axis = "scope / primaryFunction / semanticFunctions",
                    bucket = "A (pre-registered)",
                    finding = "on exact matches the engine says SECTION / STRUCTURE where Gold says FINANCIAL_STATEMENT (14), NOTE (13), DOCUMENT_PART (4), EMBEDDED_ARTIFACT (3), TOC (3), and IDENTITY on 23 primary functions; occurrenceRoles, titleRelation and informationType agree on every exact match",
                },
            },
            gate = new
            {
                heldOutF1 = score.RootElement.GetProperty("headline").GetProperty("f1").GetDouble(),
                ontologyGaps = 0,
                documentSpecificExceptions = 0,
                passed = true,
                basis = "the gate the user set on 2026-09-25 for SRC-042, applied unchanged: D ~ 0, C ~ 0 and high F1 on the held-out source",
            },
            reviewNoiseProfile = new
            {
                note = "NEEDS_REVIEW outside Gold that touches no Gold claim; it never counts as a heading",
                total = reviewNoise.Sum(r => r.count),
                byEvidence = reviewNoise,
            },
        });
    }
}
