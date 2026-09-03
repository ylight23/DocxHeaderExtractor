using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// C1.6 is a replay of source facts and recorded pointer offsets only. It does not request a model,
/// alter the lane, or invent validator classes: the status/reason below come from PdfProposalValidator.Trace.
/// </summary>
public sealed class PdfC16SpanValidationAndThroughputAuditProbe
{
    [Fact]
    public void AuditFrozen001SpanPointersAndCompletionWindow()
    {
        var output = Environment.GetEnvironmentVariable("C16_REPORT");
        if (string.IsNullOrWhiteSpace(output)) return;
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var firstLoss = JsonSerializer.Deserialize<FirstLossReport>(File.ReadAllText(Required("C16_FIRST_LOSS")), options)
            ?? throw new InvalidOperationException("C1.5 first-loss report could not be read.");
        var preflight = JsonSerializer.Deserialize<Preflight>(File.ReadAllText(Required("C16_PREFLIGHT")), options)
            ?? throw new InvalidOperationException("C1.5 preflight JSON could not be read.");
        var checkpoint = File.ReadLines(Required("C16_CHECKPOINT"))
            .Select(line => JsonSerializer.Deserialize<CheckpointEntry>(line, options))
            .Where(entry => entry is not null).Cast<CheckpointEntry>().ToArray();

        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var docx = Path.Combine(root, "todo10_8", "heading_corpus_95_word", "01_phap_quy",
            "001_Bo_luat_Dan_su_91-2015-QH13.docx");
        var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(docx);
        var contexts = PdfCandidateContextBuilder.Build(snapshot.CandidateBlocks, snapshot.Annotations);
        var spanBlocks = checkpoint.Where(entry => entry.Lane == "span")
            .SelectMany(entry => entry.Payload.Blocks.Select(block => (block, entry.Payload.FailureClass))).ToArray();
        var resolved = firstLoss.Items.Where(item => item.FirstLoss == "SPAN_RESOLVED").ToArray();

        var reviewedById = preflight.Occurrences.ToDictionary(item => item.GoldStableId, StringComparer.Ordinal);
        var pointerAudits = resolved.Select(item => AuditPointer(item, reviewedById[item.GoldStableId], spanBlocks, contexts)).ToArray();
        Assert.Equal(94, pointerAudits.Length);
        Assert.All(pointerAudits, item => Assert.False(string.IsNullOrWhiteSpace(item.ValidatorSpanStatus)));

        var semanticEntries = checkpoint.Where(entry => entry.Lane == "semantic").OrderBy(entry => entry.CompletedAt).ToArray();
        var spanEntries = checkpoint.Where(entry => entry.Lane == "span").OrderBy(entry => entry.CompletedAt).ToArray();
        var intervals = spanEntries.Zip(spanEntries.Skip(1), (left, right) => (right.CompletedAt - left.CompletedAt).TotalSeconds).ToArray();
        var result = new
        {
            artifactKind = "c16_span_validation_and_throughput_audit",
            usesModel = false,
            document = "001",
            laneA = new
            {
                resolvedOccurrences = pointerAudits.Length,
                validatorSpanStatus = pointerAudits.GroupBy(item => item.ValidatorSpanStatus).ToDictionary(group => group.Key, group => group.Count()),
                validatorReason = pointerAudits.GroupBy(item => item.ValidatorReason ?? "none").ToDictionary(group => group.Key, group => group.Count()),
                runtimeDisposition = "discarded_by_span_partial_timeout",
                items = pointerAudits,
            },
            laneB = new
            {
                semanticBatchesCompleted = semanticEntries.Length,
                semanticBlocksCompleted = semanticEntries.Sum(entry => entry.Payload.Blocks.Count),
                spanBatchesCompleted = spanEntries.Length,
                spanBlocksCompleted = spanEntries.Sum(entry => entry.Payload.Blocks.Count),
                semanticFirstCheckpointAt = semanticEntries.FirstOrDefault()?.CompletedAt,
                semanticLastCheckpointAt = semanticEntries.LastOrDefault()?.CompletedAt,
                spanFirstCheckpointAt = spanEntries.FirstOrDefault()?.CompletedAt,
                spanLastCheckpointAt = spanEntries.LastOrDefault()?.CompletedAt,
                spanCheckpointWindowSeconds = spanEntries.Length < 2 ? 0 : (spanEntries[^1].CompletedAt - spanEntries[0].CompletedAt).TotalSeconds,
                consecutiveSpanBatchIntervalSeconds = Summary(intervals),
                // The old checkpoint has completion timestamps only; it cannot prove exact request
                // start time or distinguish provider stall from time before the first completion.
                exactSpanRequestStartMeasured = false,
                exactTimeoutInstantMeasured = false,
            },
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static PointerAudit AuditPointer(FirstLossItem item, PreflightOccurrence reviewed,
        IReadOnlyList<(CheckpointBlock Block, string? FailureClass)> spanBlocks,
        IReadOnlyDictionary<string, PdfCandidateContext> contexts)
    {
        var candidates = spanBlocks.Where(entry => entry.Block.Resolved &&
                item.DebugSpanCandidateIds.Contains(entry.Block.Id, StringComparer.Ordinal) &&
                entry.Block.Start is not null && entry.Block.End is not null && contexts.ContainsKey(entry.Block.Id))
            .Select(entry => (Block: entry.Block, Context: contexts[entry.Block.Id]))
            .ToArray();
        if (candidates.Length == 0) throw new InvalidOperationException($"No checkpoint pointer for {item.GoldStableId}.");

        var reports = candidates.Select(candidate =>
        {
            var decision = new PdfBlockDecision(candidate.Block.Id, PdfBlockRole.HeadingTopic, 1, "checkpoint",
                new TextOffsetSpan(candidate.Block.Start!.Value, candidate.Block.End!.Value));
            var trace = PdfProposalValidator.Trace(contexts, [decision]).Single(trace => trace.Id == candidate.Block.Id);
            var source = candidate.Context.Source.RawText;
            var spanText = candidate.Block.Start >= 0 && candidate.Block.End <= source.Length && candidate.Block.End > candidate.Block.Start
                ? source[candidate.Block.Start.Value..candidate.Block.End.Value] : null;
            return new { candidate.Block, Trace = trace, Source = source, SpanText = spanText };
        }).ToArray();
        var first = reports[0];
        return new PointerAudit(item.GoldStableId, item.GoldText, reviewed.Page, reviewed.RequiredLineIds, reviewed.SourceLines,
            first.Block.Id, first.Block.Start, first.Block.End,
            first.Source, first.SpanText, first.Trace.SpanStatus, first.Trace.Reason, first.Trace.ValidationStatus);
    }

    private static object Summary(IReadOnlyList<double> values) => values.Count == 0 ? new { count = 0 } : new
    {
        count = values.Count,
        min = values.Min(),
        median = values.OrderBy(value => value).ElementAt(values.Count / 2),
        max = values.Max(),
    };

    private static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value : throw new InvalidOperationException($"Missing required environment variable {name}.");

    private sealed record FirstLossReport(IReadOnlyList<FirstLossItem> Items);
    private sealed record FirstLossItem(string GoldStableId, string GoldText, string FirstLoss, IReadOnlyList<string> DebugSpanCandidateIds);
    private sealed record Preflight(IReadOnlyList<PreflightOccurrence> Occurrences);
    private sealed record PreflightOccurrence(string GoldStableId, int Page, IReadOnlyList<string> RequiredLineIds,
        IReadOnlyList<ReviewedLine> SourceLines);
    private sealed record ReviewedLine(int Index, string LineId, string Text);
    private sealed record CheckpointEntry(string Lane, DateTimeOffset CompletedAt, CheckpointPayload Payload);
    private sealed record CheckpointPayload(string? FailureClass, IReadOnlyList<CheckpointBlock> Blocks);
    private sealed record CheckpointBlock(string Id, bool Resolved, int? Start, int? End);
    private sealed record PointerAudit(string GoldStableId, string GoldText, int Page,
        IReadOnlyList<string> RequiredLineIds, IReadOnlyList<ReviewedLine> ExpectedReviewedSourceLines,
        string CandidateId, int? Start, int? End,
        string SourceText, string? SpanText, string ValidatorSpanStatus, string? ValidatorReason, string ValidationStatus);
}
