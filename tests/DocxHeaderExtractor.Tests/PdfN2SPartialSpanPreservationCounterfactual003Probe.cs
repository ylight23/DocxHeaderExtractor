using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Lane A of the N2-S follow-up: 003's canonical checkpoint (concurrency 2, the correct frozen
/// profile) collapsed the same way the earlier off-protocol run and 001's C1.5 replication did -
/// <c>spanLaneStatus: partial_timeout</c>, every decision discarded. That is now a second independent
/// document reproducing C1.6/C1.7's exact mechanism, which is the cross-document recurrence evidence
/// C1 was closed for lacking.
/// <para>
/// This replays only facts already persisted by the canonical run: completed source pointers are
/// preserved, every block without one is <c>Uncertain</c>. No provider call, retry, or candidate
/// construction change - the same operational shape as <see
/// cref="PdfC17PartialSpanPreservationCounterfactualProbe"/>, adapted from 001's occurrence bridge to
/// 003's N1.2-S silver labels joined through N1.3-S's decisionRelevant cohort.
/// </para>
/// </summary>
public sealed class PdfN2SPartialSpanPreservationCounterfactual003Probe
{
    [Fact]
    public void ReplayCompletedSpansThroughCurrentDownstreamStages()
    {
        var output = Environment.GetEnvironmentVariable("N2S_C17B_REPORT");
        if (string.IsNullOrWhiteSpace(output)) return;

        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var report = BuildReport(root);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public void CommittedReportReproducesFromCommittedRunAndCensus()
    {
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var path = Path.Combine(root, "eval", "benchmark-n0", "n2-s", "counterfactual", "003-partial-span-preservation.v1.json");
        if (!File.Exists(path)) return;

        var expected = JsonSerializer.Serialize(BuildReport(root), new JsonSerializerOptions { WriteIndented = true });
        Assert.Equal(expected.Replace("\r\n", "\n"), File.ReadAllText(path).Replace("\r\n", "\n"));
    }

    private static object BuildReport(string root)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var checkpointPath = Path.Combine(root, "eval", "benchmark-n0", "n2-s", "checkpoints", "003-n2-s.jsonl");
        var checkpoint = File.ReadLines(checkpointPath)
            .Select(line => JsonSerializer.Deserialize<CheckpointEntry>(line, options))
            .Where(entry => entry is not null).Cast<CheckpointEntry>().ToArray();

        var silverPath = Path.Combine(root, "eval", "benchmark-n0", "silver-labels", "003-n1.2-silver-model-assisted.v1.json");
        using var silver = JsonDocument.Parse(File.ReadAllText(silverPath));
        var lineIdsByStableId = silver.RootElement.GetProperty("headingOccurrences").EnumerateArray()
            .ToDictionary(
                o => o.TryGetProperty("goldStableId", out var g) ? g.GetString()! : o.GetProperty("silverStableId").GetString()!,
                o => o.GetProperty("sourceLineIds").EnumerateArray().Select(l => l.GetString()!).ToArray(),
                StringComparer.Ordinal);

        var censusPath = Path.Combine(root, "eval", "benchmark-n0", "census", "003-n1.3-census.v1.json");
        using var census = JsonDocument.Parse(File.ReadAllText(censusPath));
        var decisionRelevantIds = census.RootElement.GetProperty("occurrences").GetProperty("decisionRelevant")
            .EnumerateArray().Select(o => o.GetProperty("stableId").GetString()!).ToArray();

        var docx = Path.Combine(root, "todo10_8", "heading_corpus_95_word", "01_phap_quy",
            "003_Luat_Doanh_nghiep_59-2020-QH14.docx");
        var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(docx);
        var selected = PdfLayoutEvidenceOutline.SelectRankedCandidates(snapshot.CandidateBlocks, snapshot.Audit.Candidates, 160).Selected;
        var contexts = PdfCandidateContextBuilder.Build(selected, snapshot.Annotations);
        var roles = checkpoint.Where(entry => entry.Lane == "semantic").SelectMany(entry => entry.Payload.Blocks)
            .ToDictionary(block => block.Id, StringComparer.Ordinal);
        var spans = checkpoint.Where(entry => entry.Lane == "span").SelectMany(entry => entry.Payload.Blocks)
            .Where(block => block.Resolved && block.Start is not null && block.End is not null)
            .ToDictionary(block => block.Id, StringComparer.Ordinal);

        Assert.Equal(160, selected.Count);
        var decisions = selected.Select(block =>
        {
            if (!roles.TryGetValue(block.Id, out var role))
                return new PdfBlockDecision(block.Id, PdfBlockRole.Uncertain, 0, "missing-checkpoint-role");
            var parsed = Enum.TryParse<PdfBlockRole>(role.Role, true, out var value) ? value : PdfBlockRole.Uncertain;
            return spans.TryGetValue(block.Id, out var span)
                ? new PdfBlockDecision(block.Id, parsed, role.Confidence, role.Reason ?? "checkpoint",
                    new TextOffsetSpan(span.Start!.Value, span.End!.Value))
                : new PdfBlockDecision(block.Id, parsed, role.Confidence, role.Reason ?? "checkpoint");
        }).ToArray();

        var validated = PdfProposalValidator.Validate(contexts, decisions);
        var excluded = snapshot.Annotations.Where(a => a.ExcludeFromSemanticSamples).Select(a => a.Line).ToHashSet();
        var profile = PdfStyleClusterProfile.Learn(snapshot.Annotations.Where(a => !a.ExcludeFromSemanticSamples)
            .Select(a => a.Line).ToArray());
        var samples = PdfSemanticClusterAnalyst.BuildSamples(profile, snapshot.Lines, excluded);
        var grounded = PdfBlockGrounder.Ground(selected, validated.Select(item => decisions.Single(d => d.Id == item.SourceId)).ToArray(),
            profile, samples, [], requireLearnedCandidateStyle: false);
        var groundedIds = grounded.Headings.Select(h => h.Id).ToHashSet(StringComparer.Ordinal);
        var slim = new DocxSlimExtractor().Extract(docx);
        var alignment = PdfLayoutEvidenceOutline.BuildBroadAlignmentForCandidateIds(docx, slim, groundedIds);

        var bySource = snapshot.Provenance;
        int DecisionRelevantCovered(IEnumerable<string> candidateIds)
        {
            var ids = candidateIds.ToArray();
            return decisionRelevantIds.Count(stableId =>
                lineIdsByStableId.TryGetValue(stableId, out var required) &&
                ids.Any(id => bySource.TryGetValue(id, out var provenance) &&
                    required.All(line => provenance.LineIds.Contains(line, StringComparer.Ordinal))));
        }
        var emittedIds = alignment.Headings.Select(h => h.SourceId!).ToArray();

        // Precision/false-positive risk: does an emitted candidate correspond to ANY silver heading
        // occurrence at all (not only the 128 decisionRelevant ones), or none? Mirrors C1.7's
        // emittedWithReviewedGoldOccurrence/emittedWithoutReviewedGoldOccurrence split for 001, so the
        // two documents' collateral risk is reported the same way.
        bool EmittedMatchesAnySilverOccurrence(string candidateId) =>
            bySource.TryGetValue(candidateId, out var provenance) &&
            lineIdsByStableId.Values.Any(required => required.All(line => provenance.LineIds.Contains(line, StringComparer.Ordinal)));
        var emittedWithSilverOccurrence = emittedIds.Count(EmittedMatchesAnySilverOccurrence);

        return new
        {
            schemaVersion = 1,
            artifactKind = "n2s_partial_span_preservation_counterfactual",
            documentId = "003",
            usesModel = false,
            baseline = new { spanLaneStatus = "partial_timeout", validatedHeadings = 0, note = "the canonical run itself, wrapper discards everything" },
            checkpoint = new
            {
                selectedBlocks = selected.Count,
                spanBatchesCompleted = checkpoint.Count(e => e.Lane == "span"),
                spanBlocksResolved = spans.Count,
            },
            decisionRelevant = decisionRelevantIds.Length,
            silverHeadingOccurrenceCount = lineIdsByStableId.Count,
            partialPreserve = new
            {
                validatedBlocks = validated.Count,
                validatedDecisionRelevantOccurrences = DecisionRelevantCovered(validated.Select(item => item.SourceId)),
                groundedBlocks = grounded.Headings.Count,
                groundedDecisionRelevantOccurrences = DecisionRelevantCovered(groundedIds),
                emittedBlocks = alignment.Headings.Count,
                emittedDecisionRelevantOccurrences = DecisionRelevantCovered(emittedIds),
                emittedWithSilverOccurrence,
                emittedWithoutSilverOccurrence = emittedIds.Length - emittedWithSilverOccurrence,
            },
        };
    }

    private sealed record CheckpointEntry(string Lane, CheckpointPayload Payload);
    private sealed record CheckpointPayload(IReadOnlyList<CheckpointBlock> Blocks);
    private sealed record CheckpointBlock(string Id, string? Role, double Confidence, string? Reason, bool Resolved, int? Start, int? End);
}
