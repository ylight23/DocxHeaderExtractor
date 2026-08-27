using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfStageCheckpointProvenanceTests
{
    [Fact]
    public async Task SpanAndDownstreamRetentionIsAppendOnlyAndSourceIdentified()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dhx-6d-{Guid.NewGuid():N}.jsonl");
        try
        {
            await using var checkpoint = new PdfStageCheckpoint(path, resume: false, "sample.pdf");
            var line = new PdfLine(1, 700, 14, "1. Scope", .8, "", 0, 72, 420, "serif", "black");
            var block = new PdfSemanticBlock("candidate-1", [line], PdfStyleClusterProfile.StyleOf(line), 1, 700, 700, 72, 420, line.Text);
            var decision = new PdfBlockDecision("candidate-1", PdfBlockRole.HeadingTopic, .9, "role", new TextOffsetSpan(0, line.Text.Length));
            var trace = new PdfCandidateStageTrace("candidate-1", "body", "TopicHeading", "valid", "eligible", null);

            await checkpoint.RecordSpanBatchAsync(
                new[] { (block.Id, block.Page, PdfCandidateProvenance.LineId(line), (IReadOnlyList<string>)new[] { PdfCandidateProvenance.LineId(line) }, decision.HeadingSpan) },
                null,
                CancellationToken.None);
            await checkpoint.RecordDownstreamProvenanceAsync(
                new[] { block }, new[] { decision }, new[] { trace }, new HashSet<string>([block.Id], StringComparer.Ordinal), new HashSet<string>([block.Id], StringComparer.Ordinal), Array.Empty<PdfSemanticClusterDecision>(), CancellationToken.None);

            var records = File.ReadLines(path).Select(lineText => JsonDocument.Parse(lineText)).ToArray();
            var span = records.Single(record => record.RootElement.GetProperty("lane").GetString() == "span").RootElement;
            var spanBlock = span.GetProperty("payload").GetProperty("blocks")[0];
            Assert.Equal("RESOLVED", spanBlock.GetProperty("spanOutcome").GetString());

            var downstream = records.Single(record => record.RootElement.GetProperty("lane").GetString() == "downstream").RootElement;
            var occurrence = downstream.GetProperty("payload").GetProperty("occurrences")[0];
            Assert.Equal("candidate-1", occurrence.GetProperty("candidateIdDiagnostic").GetString());
            Assert.Equal(PdfCandidateProvenance.LineId(line), occurrence.GetProperty("sourceIdentity").GetProperty("sourceLineIds")[0].GetString());
            Assert.Equal("RESOLVED", occurrence.GetProperty("spanOutcome").GetString());
            Assert.Equal("eligible", occurrence.GetProperty("validatorStatus").GetString());
            Assert.Equal("GROUNDED", occurrence.GetProperty("groundingStatus").GetString());
            Assert.Equal("EMITTED", occurrence.GetProperty("outputStatus").GetString());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
