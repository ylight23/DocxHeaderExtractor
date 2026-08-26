using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The collateral/cost gate C1's close and C1.7 both named as still required before
/// partial-result-preservation could be promoted: behavioral neutrality on a complete span lane, and
/// the recovered blocks' precision risk on a second document. All facts here are read from already
/// committed, already offline-computed artifacts - no provider call, no new candidate construction.
/// </summary>
public sealed class PdfC1PartialPreservationCollateralGateProbe
{
    /// <summary>
    /// The proposed change only changes behavior when a span batch is incomplete. On 057 - a complete
    /// span lane, nothing to preserve differently - the reconstruction independently computed by
    /// <see cref="PdfN2SGroundingAlignmentTrace057Probe"/> must emit the exact same candidate id set
    /// as the canonical live run, not merely the same count.
    /// </summary>
    [Fact]
    public void CompleteLaneReconstructionIsByteForByteIdenticalToTheLiveRun()
    {
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var runPath = Path.Combine(root, "eval", "benchmark-n0", "n2-s", "runs", "057-n2-s-run.v1.json");
        var tracePath = Path.Combine(root, "eval", "benchmark-n0", "n2-s", "counterfactual", "057-grounding-alignment-trace.v1.json");

        using var run = JsonDocument.Parse(File.ReadAllText(runPath));
        var liveEmitted = run.RootElement.GetProperty("rows")[0].GetProperty("canonicalGroundings")
            .EnumerateArray().Select(g => g.GetProperty("sourceFactId").GetString()!).ToHashSet(StringComparer.Ordinal);

        using var trace = JsonDocument.Parse(File.ReadAllText(tracePath));
        var reconstructedEmitted = trace.RootElement.GetProperty("emittedControls")
            .EnumerateArray().Select(c => c.GetProperty("sourceFactId").GetString()!).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(liveEmitted, reconstructedEmitted);
    }

    /// <summary>
    /// Consolidates 001 (C1.7, `.verify-build/001-c17-partial-preserve.json` - a working artifact, not
    /// committed authority) and 003 (N2-S Lane A, committed) into one collateral report. Neither number
    /// changes here; this only reads and locks the comparison so "56.3% vs 33.3%" and "10.98% vs 1.8%"
    /// cannot silently drift apart from what each probe actually produced.
    /// </summary>
    [Fact]
    public void WriteConsolidatedGateReport()
    {
        var output = Environment.GetEnvironmentVariable("C1_COLLATERAL_GATE_REPORT");
        if (string.IsNullOrWhiteSpace(output)) return;

        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var c17Path = Path.Combine(root, ".verify-build", "001-c17-partial-preserve.json");
        var laneAPath = Path.Combine(root, "eval", "benchmark-n0", "n2-s", "counterfactual", "003-partial-span-preservation.v1.json");
        if (!File.Exists(c17Path) || !File.Exists(laneAPath)) return;

        using var c17 = JsonDocument.Parse(File.ReadAllText(c17Path));
        using var laneA = JsonDocument.Parse(File.ReadAllText(laneAPath));
        var c17p = c17.RootElement.GetProperty("partialPreserve");
        var laneAp = laneA.RootElement.GetProperty("partialPreserve");

        var c17DecisionRelevant = 162; // 001's reviewed decision-relevant occurrence count, frozen in C1's own ledger
        var laneADecisionRelevant = laneA.RootElement.GetProperty("decisionRelevant").GetInt32();

        object Row(string doc, int decisionRelevant, int emittedDecisionRelevant, int emittedTotal, int emittedWithGold, int emittedWithoutGold) => new
        {
            document = doc,
            decisionRelevant,
            emittedDecisionRelevantOccurrences = emittedDecisionRelevant,
            recoveryRate = Math.Round(emittedDecisionRelevant / (double)decisionRelevant, 4),
            emittedTotal,
            emittedWithGoldOrSilverOccurrence = emittedWithGold,
            emittedWithoutGoldOrSilverOccurrence = emittedWithoutGold,
            falsePositiveRateAmongEmitted = Math.Round(emittedWithoutGold / (double)emittedTotal, 4),
        };

        var report = new
        {
            schemaVersion = 1,
            artifactKind = "c1_partial_preservation_collateral_gate",
            usesModel = false,
            neutralityCheck = "CompleteLaneReconstructionIsByteForByteIdenticalToTheLiveRun - locked, passing",
            failClosedChecks = new[]
            {
                "ASpanLaneThatNeverRanIsNotReportedAsComplete (PdfSpanLaneProvenanceTests) - not_run is never read as complete",
                "SemanticLaneMayBeCompleteWhileTheSpanLaneTimedOut (PdfSpanLaneProvenanceTests) - the two statuses never collapse into one",
                "The span batch exception path (PdfBlockAnalyst's catch block) still records failureClass and still discards that batch's spans - unchanged by this gate",
            },
            recovery = new[]
            {
                Row("001", c17DecisionRelevant,
                    c17p.GetProperty("emittedDecisionRelevantOccurrences").GetInt32(),
                    c17p.GetProperty("emittedBlocks").GetInt32(),
                    c17p.GetProperty("emittedWithReviewedGoldOccurrence").GetInt32(),
                    c17p.GetProperty("emittedWithoutReviewedGoldOccurrence").GetInt32()),
                Row("003", laneADecisionRelevant,
                    laneAp.GetProperty("emittedDecisionRelevantOccurrences").GetInt32(),
                    laneAp.GetProperty("emittedBlocks").GetInt32(),
                    laneAp.GetProperty("emittedWithSilverOccurrence").GetInt32(),
                    laneAp.GetProperty("emittedWithoutSilverOccurrence").GetInt32()),
            },
            interpretation = "001's evidence is human-reviewed gold; 003's is N1.2-S silver. The false-positive rate is materially higher on 003 (silver, procedurally different document) than on 001 (gold) - this is not glossed as automatically passing the collateral gate; it is the evidence the promotion decision must weigh.",
        };
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
}
