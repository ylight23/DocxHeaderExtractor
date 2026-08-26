using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// A3: the production implementation of partial-result preservation, locked directly against
/// <see cref="PdfLayoutEvidenceOutline.PreservePartialSpanResolutions"/> and
/// <see cref="PdfStageCheckpoint.ReadCompletedSpanResolutions"/> - the two units the fix actually
/// touches - rather than a reimplementation of the pipeline the way the offline counterfactual probes
/// were. No provider call: every scenario here is a synthetic or already-committed checkpoint file.
/// <para>
/// The neutrality claim for a complete span lane is stronger than empirical here: this code path is
/// only reached when <c>spanRun.TimedOut</c> is true (see the call site), so a complete lane never
/// executes <see cref="PdfLayoutEvidenceOutline.PreservePartialSpanResolutions"/> at all - unreachable
/// by construction, not merely observed unchanged.
/// </para>
/// </summary>
public sealed class PdfA3PartialSpanPreservationImplementationTests
{
    [Fact]
    public void NullCheckpointReproducesExactPriorBehaviorEveryDecisionBecomesUncertain()
    {
        var roleAnalysis = SampleAnalysis();

        var result = PdfLayoutEvidenceOutline.PreservePartialSpanResolutions(roleAnalysis, null);

        Assert.All(result.Decisions, d =>
        {
            Assert.Equal(PdfBlockRole.Uncertain, d.Role);
            Assert.Equal(0, d.Confidence);
            Assert.Equal("semantic_request_timeout", d.Reason);
            Assert.Null(d.HeadingSpan);
        });
    }

    [Fact]
    public void EmptyCheckpointFileReproducesExactPriorBehavior()
    {
        var path = TempCheckpointPath();
        File.WriteAllText(path, "");
        try
        {
            var checkpoint = new PdfStageCheckpoint(path, resume: false, documentIdentity: "doc");
            var result = PdfLayoutEvidenceOutline.PreservePartialSpanResolutions(SampleAnalysis(), checkpoint);

            Assert.All(result.Decisions, d => Assert.Equal(PdfBlockRole.Uncertain, d.Role));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ResolvedBlocksArePreservedAndUnresolvedBlocksBecomeUncertain()
    {
        var path = TempCheckpointPath();
        WriteSpanBatchLine(path, failureClass: null, ("b1", resolved: true, 0, 10), ("b2", resolved: false, null, null));
        try
        {
            var checkpoint = new PdfStageCheckpoint(path, resume: false, documentIdentity: "doc");
            var roleAnalysis = new PdfBlockAnalysis(
                [],
                [
                    new PdfBlockDecision("b1", PdfBlockRole.HeadingTopic, 0.95, ""),
                    new PdfBlockDecision("b2", PdfBlockRole.HeadingTopic, 0.9, ""),
                    new PdfBlockDecision("b3", PdfBlockRole.BodySentence, 0.8, ""),
                ],
                []);

            var result = PdfLayoutEvidenceOutline.PreservePartialSpanResolutions(roleAnalysis, checkpoint);

            var b1 = result.Decisions.Single(d => d.Id == "b1");
            Assert.Equal(PdfBlockRole.HeadingTopic, b1.Role); // role/confidence preserved as-is
            Assert.Equal(0.95, b1.Confidence);
            Assert.NotNull(b1.HeadingSpan);
            Assert.Equal(0, b1.HeadingSpan!.Start);
            Assert.Equal(10, b1.HeadingSpan!.End);

            var b2 = result.Decisions.Single(d => d.Id == "b2");
            Assert.Equal(PdfBlockRole.Uncertain, b2.Role);
            Assert.Equal("semantic_request_timeout", b2.Reason);

            // b3 was never in the span checkpoint at all (batch never started/completed) - same
            // fail-closed treatment as an explicitly unresolved block.
            var b3 = result.Decisions.Single(d => d.Id == "b3");
            Assert.Equal(PdfBlockRole.Uncertain, b3.Role);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ATornFinalLineIsSkippedNotFaultedAndEarlierBatchesRemainUsable()
    {
        var path = TempCheckpointPath();
        WriteSpanBatchLine(path, failureClass: null, ("b1", resolved: true, 5, 15));
        File.AppendAllText(path, "{\"lane\":\"span\",\"identity\":\"doc:batch:b2\",\"status\":\"comple"); // torn
        try
        {
            var checkpoint = new PdfStageCheckpoint(path, resume: false, documentIdentity: "doc");
            var resolved = checkpoint.ReadCompletedSpanResolutions();

            Assert.True(resolved.ContainsKey("b1"));
            Assert.Equal(new TextOffsetSpan(5, 15), resolved["b1"]);
            Assert.False(resolved.ContainsKey("b2"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AFailedBatchContributesNoResolutionsEvenWhenItRecordsSomeBlocks()
    {
        var path = TempCheckpointPath();
        WriteSpanBatchLine(path, failureClass: "TaskCanceledException", ("b1", resolved: false, null, null));
        try
        {
            var checkpoint = new PdfStageCheckpoint(path, resume: false, documentIdentity: "doc");
            Assert.Empty(checkpoint.ReadCompletedSpanResolutions());
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Regression lock against the real, already-committed 003 canonical checkpoint: the production
    /// implementation must recover exactly the same 88 blocks Lane A's offline counterfactual already
    /// measured and reported as `spanBlocksResolved`, not a reimplementation's own count.
    /// </summary>
    [Fact]
    public void Real003CheckpointRecoversTheSameCountLaneAAlreadyMeasured()
    {
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var checkpointPath = Path.Combine(root, "eval", "benchmark-n0", "n2-s", "checkpoints", "003-n2-s.jsonl");
        var counterfactualPath = Path.Combine(root, "eval", "benchmark-n0", "n2-s", "counterfactual", "003-partial-span-preservation.v1.json");
        if (!File.Exists(checkpointPath) || !File.Exists(counterfactualPath)) return;

        using var counterfactual = JsonDocument.Parse(File.ReadAllText(counterfactualPath));
        var expectedResolved = counterfactual.RootElement.GetProperty("checkpoint").GetProperty("spanBlocksResolved").GetInt32();

        var checkpoint = new PdfStageCheckpoint(checkpointPath, resume: false, documentIdentity: "003_Luat_Doanh_nghiep_59-2020-QH14.pdf");
        var resolved = checkpoint.ReadCompletedSpanResolutions();

        Assert.Equal(expectedResolved, resolved.Count);
    }

    private static PdfBlockAnalysis SampleAnalysis() => new(
        [],
        [
            new PdfBlockDecision("b1", PdfBlockRole.HeadingTopic, 0.95, "", new TextOffsetSpan(0, 5)),
            new PdfBlockDecision("b2", PdfBlockRole.BodySentence, 0.9, ""),
        ],
        []);

    private static string TempCheckpointPath() => Path.Combine(Path.GetTempPath(), $"dhx-a3-{Guid.NewGuid():N}.jsonl");

    private static void WriteSpanBatchLine(string path, string? failureClass, params (string Id, bool Resolved, int? Start, int? End)[] blocks)
    {
        var line = JsonSerializer.Serialize(new
        {
            lane = "span",
            identity = "doc:batch:" + string.Join(',', blocks.Select(b => b.Id)),
            status = failureClass is null ? "completed" : "failed",
            completedAt = DateTimeOffset.UtcNow,
            payload = new
            {
                failureClass,
                blocks = blocks.Select(b => new { id = b.Id, page = 1, lineId = (string?)null, lineIds = Array.Empty<string>(), resolved = b.Resolved, start = b.Start, end = b.End }),
            },
        });
        File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
    }
}
