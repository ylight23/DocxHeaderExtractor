using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// C1.7A replays only facts already persisted by C1.5. It preserves completed source pointers and
/// leaves every uncompleted block uncertain; no provider call, retry, or candidate construction change.
/// </summary>
public sealed class PdfC17PartialSpanPreservationCounterfactualProbe
{
    [Fact]
    public void ReplayCompletedSpansThroughCurrentDownstreamStages()
    {
        var output = Environment.GetEnvironmentVariable("C17_REPORT");
        if (string.IsNullOrWhiteSpace(output)) return;
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var checkpoint = File.ReadLines(Required("C17_CHECKPOINT"))
            .Select(line => JsonSerializer.Deserialize<CheckpointEntry>(line, options))
            .Where(entry => entry is not null).Cast<CheckpointEntry>().ToArray();
        var preflight = JsonSerializer.Deserialize<Preflight>(File.ReadAllText(Required("C17_PREFLIGHT")), options)
            ?? throw new InvalidOperationException("C1.5 preflight JSON could not be read.");
        var bridge = DocxHeaderExtractor.Core.Eval.PdfReviewedOccurrenceBridge.Load(
            File.ReadAllText(Directory.GetFiles(Path.Combine(PdfExtractorQualityBenchmarkProbe.RepositoryRoot(), "keys", "occurrence-bridge"), "001_*.json").Single()));

        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var docx = Path.Combine(root, "todo10_8", "heading_corpus_95_word", "01_phap_quy",
            "001_Bo_luat_Dan_su_91-2015-QH13.docx");
        var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(docx);
        var selected = PdfLayoutEvidenceOutline.SelectRankedCandidates(snapshot.CandidateBlocks, snapshot.Audit.Candidates, 160).Selected;
        var contexts = PdfCandidateContextBuilder.Build(selected, snapshot.Annotations);
        var roles = checkpoint.Where(entry => entry.Lane == "semantic").SelectMany(entry => entry.Payload.Blocks)
            .ToDictionary(block => block.Id, StringComparer.Ordinal);
        var spans = checkpoint.Where(entry => entry.Lane == "span").SelectMany(entry => entry.Payload.Blocks)
            .Where(block => block.Resolved && block.Start is not null && block.End is not null)
            .ToDictionary(block => block.Id, StringComparer.Ordinal);

        Assert.Equal(160, selected.Count);
        Assert.Equal(80, spans.Count);
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
        var excluded = snapshot.Annotations.Where(annotation => annotation.ExcludeFromSemanticSamples).Select(annotation => annotation.Line).ToHashSet();
        var profile = PdfStyleClusterProfile.Learn(snapshot.Annotations.Where(annotation => !annotation.ExcludeFromSemanticSamples)
            .Select(annotation => annotation.Line).ToArray());
        var samples = PdfSemanticClusterAnalyst.BuildSamples(profile, snapshot.Lines, excluded);
        var grounded = PdfBlockGrounder.Ground(selected, validated.Select(item => decisions.Single(decision => decision.Id == item.SourceId)).ToArray(),
            profile, samples, [], requireLearnedCandidateStyle: false);
        var groundedIds = grounded.Headings.Select(heading => heading.Id).ToHashSet(StringComparer.Ordinal);
        var slim = new DocxSlimExtractor().Extract(docx);
        var alignment = PdfLayoutEvidenceOutline.BuildBroadAlignmentForCandidateIds(docx, PolicyStateFixture.FromSlim(slim), groundedIds);

        var reviewed = bridge.Occurrences.Where(item => item.ReviewStatus == "reviewed").ToArray();
        var bySource = snapshot.Provenance;
        int GoldOccurrencesCovered(IEnumerable<string> candidateIds) => preflight.Occurrences.Count(occurrence =>
            candidateIds.Any(id => bySource.TryGetValue(id, out var provenance) &&
                occurrence.RequiredLineIds.All(line => provenance.LineIds.Contains(line, StringComparer.Ordinal))));
        var emittedIds = alignment.Headings.Select(heading => heading.SourceId!).ToArray();
        var outputWithGold = emittedIds.Count(id => bySource.TryGetValue(id, out var provenance) && reviewed.Any(occurrence =>
            occurrence.RequiredLines.Select(line => line.LineId).All(line => provenance.LineIds.Contains(line, StringComparer.Ordinal))));

        var report = new
        {
            artifactKind = "c17_partial_span_preservation_counterfactual",
            usesModel = false,
            current = new { survivingGoldOccurrences = 0 },
            partialPreserve = new
            {
                selectedBlocks = selected.Count,
                checkpointResolvedSpanBlocks = spans.Count,
                validatedBlocks = validated.Count,
                validatedDecisionRelevantOccurrences = GoldOccurrencesCovered(validated.Select(item => item.SourceId)),
                groundedBlocks = grounded.Headings.Count,
                groundedDecisionRelevantOccurrences = GoldOccurrencesCovered(groundedIds),
                emittedBlocks = alignment.Headings.Count,
                emittedDecisionRelevantOccurrences = GoldOccurrencesCovered(emittedIds),
                emittedWithReviewedGoldOccurrence = outputWithGold,
                emittedWithoutReviewedGoldOccurrence = emittedIds.Length - outputWithGold,
            },
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value : throw new InvalidOperationException($"Missing required environment variable {name}.");

    private sealed record Preflight(IReadOnlyList<PreflightOccurrence> Occurrences);
    private sealed record PreflightOccurrence(string GoldStableId, IReadOnlyList<string> RequiredLineIds);
    private sealed record CheckpointEntry(string Lane, CheckpointPayload Payload);
    private sealed record CheckpointPayload(IReadOnlyList<CheckpointBlock> Blocks);
    private sealed record CheckpointBlock(string Id, string? Role, double Confidence, string? Reason, bool Resolved, int? Start, int? End);
}
