using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// SRC042_BLIND_GENERALIZATION_AUDIT_V1, protocol step 6: every residual of the committed raw held-out score
/// (1f28301) gets exactly one cause, and every cause one bucket - A (a meaning decision the ontology expresses
/// and the evidence cannot settle), B_KNOWN (pre-registered in 1e7bc7f), B_NEW (a new generic engine gap),
/// C (an ontology gap), D (a document-specific exception). The raw score is not edited and V1.2 is not changed.
/// Also recorded: the agreed Box 2 check (user, 2026-09-25).
/// </summary>
public sealed class Src042ResidualClassificationTests
{
    private const string Score = "eval/a99-closed-loop/generic-audit-v1_2/SRC-042.blind-score.v1_2.json";
    private const string Proposals = "eval/a99-closed-loop/generic-audit-v1_2/SRC-042.proposals.v1_2.json";

    private sealed record Cause(string Id, string Bucket, string Reason, string[] GoldIdentities, string[] NonGoldIdentities);

    private static readonly Cause[] Causes =
    [
        new("COLON_ENDED_LABEL_OVER_PROSE", "A",
            "a colon-ended standalone label over its list or prose is a heading or a field label by meaning; the engine sends it to review by design, and the fail-safe held for all three",
            ["L0803:S0:0-66", "L1737:S0:0-26", "L1762:S0:0-28"], []),
        new("CONTINUED_TITLE_OVER_IMAGE_PAGE", "A",
            "the auditor's report title repeated on a later page whose body is an image: no text region is visible below it; a heading by the A2 precedent. Sent to review",
            ["L2591:S0:0-27"], []),
        new("OCCURRENCE_VS_RUNNING_HEADER", "A",
            "the same text, type and position tops pages 3, 4 and 5; only the first occurrence opens the part (A3 precedent). Source evidence is identical across the three",
            ["L0005:S0:0-36"], []),
        new("METADATA_LINE_COUNTED_AS_PEER", "B_NEW",
            "the cover title (three lines, assembled exactly) is followed by the reporting date set at the same size; the region test counts that date-shaped metadata line as a peer label and sends the title to review as 'opens nothing'",
            ["L0001:S0:0-34|L0002:S0:0-3|L0003:S0:0-20"], []),
        new("BARE_BULLET_GLYPH", "B_NEW",
            "a bullet glyph set bold in its own segment beside the item's text: the first segment of a two-segment row stands alone, and a lone symbol has no shape that says it is a list marker",
            [], ["L0767:S0:0-1", "L0776:S0:0-1", "L0778:S0:0-1"]),
        new("FIGURE_REGION_STOPPED_BY_SUBTITLE", "B_NEW",
            "V1.2's figure region (gap 10) ends at the first line of 8+ words; the figure's small descriptive subtitle under its caption is such a line, so the chart's panel title below it fell outside the region",
            [], ["L2239:S0:0-17|L2240:S0:0-12"]),
        new("PAGE_NOTICE", "B_NEW",
            "'This page left intentionally blank', set large at the top of an otherwise empty page, reads as a page-initial title; a notice about the page has no shape of its own",
            [], ["L2562:S0:0-34"]),
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

        // The agreed Box 2 check: what the engine proposed for "Box 2: Financing Principles".
        using var proposals = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(Proposals)));
        var box2 = proposals.RootElement.GetProperty("hypotheses").EnumerateArray()
            .Single(h => h.GetProperty("Text").GetString()!.StartsWith("Box 2:", StringComparison.Ordinal));

        var reviewNoise = residuals.GetProperty("nonGoldNeedsReview").EnumerateArray()
            .Where(x => !x.GetProperty("touchesGold").GetBoolean())
            .Select(x => string.Join(" + ", x.GetProperty("hypothesis").GetProperty("Evidence").EnumerateArray().Select(e => e.GetString()!)
                .Where(e => !e.StartsWith("followed by", StringComparison.Ordinal) && !e.StartsWith("set apart", StringComparison.Ordinal))
                .DefaultIfEmpty("(set apart only)")))
            .GroupBy(e => e).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new { evidence = g.Key, count = g.Count() }).ToArray();

        FreezeArtifact.AssertJson("eval/a99-closed-loop/generic-audit-v1_2", "SRC-042.residual-classification.v1_2.json", new
        {
            artifactKind = "a99_generic_audit_residual_classification",
            study = "SRC042_BLIND_GENERALIZATION_AUDIT_V1",
            rawScore = new { path = Score, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(Score)), commit = "1f28301" },
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
            knownGapsRecurring = Array.Empty<string>(),
            knownGapsNotObserved = new[] { "B2", "B4", "K1", "K2", "K3", "K4", "K5", "K6 (the one chart label that escaped had a numbered caption: FIGURE_REGION_STOPPED_BY_SUBTITLE)", "K7" },
            ontologyGaps = 0,
            documentSpecificExceptions = Array.Empty<string>(),
            box2Check = new
            {
                occurrence = "Box 2: Financing Principles (L1100:S0), Gold FALSE (user decision revised before the reveal, 336c6b6)",
                engineProposal = box2.GetProperty("ProposedIsHeading").GetString(),
                engineEvidence = box2.GetProperty("Evidence").EnumerateArray().Select(e => e.GetString()!).ToArray(),
                outcome = box2.GetProperty("ProposedIsHeading").GetString() switch
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
                    finding = "on exact matches the engine says SECTION / STRUCTURE where Gold says FINANCIAL_STATEMENT (14), NOTE (14), DOCUMENT_PART (4), EMBEDDED_ARTIFACT (3); occurrenceRoles, titleRelation and informationType agree on every exact match",
                },
            },
            gate = new
            {
                heldOutF1 = score.RootElement.GetProperty("headline").GetProperty("f1").GetDouble(),
                ontologyGaps = 0,
                documentSpecificExceptions = 0,
                passed = true,
                basis = "user, 2026-09-25: D ~ 0, C ~ 0 and high F1 on the held-out source",
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
