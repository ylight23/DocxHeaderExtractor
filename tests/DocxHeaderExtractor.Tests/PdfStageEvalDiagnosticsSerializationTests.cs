using System.Text.Json;
using DocxHeaderExtractor.Cli;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfStageEvalDiagnosticsSerializationTests
{
    [Fact]
    public void StageEvalPersistsAllThreeLaneFactsExplicitly()
    {
        var audit = MinimalAudit() with
        {
            SemanticLane = new RouteLaneExecutionAudit("complete", 2, 2, 0, 0),
            SpanLane = new RouteLaneExecutionAudit("partial_timeout", 2, 1, 1, 0, "timeout"),
            VisualLane = new RouteLaneExecutionAudit("complete", 0, 0, 0, 0),
        };
        var lanes = PdfStageEvalDiagnostics.BuildLaneDiagnostics(audit);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            semanticLane = lanes.SemanticLane,
            spanLane = lanes.SpanLane,
            visualLane = lanes.VisualLane,
            lanes = lanes.Lanes,
        }));
        var root = doc.RootElement;

        Assert.Equal("complete", root.GetProperty("semanticLane").GetProperty("status").GetString());
        Assert.Equal("partial_timeout", root.GetProperty("spanLane").GetProperty("status").GetString());
        Assert.Equal("complete", root.GetProperty("visualLane").GetProperty("status").GetString());
        Assert.Equal("partial_timeout", root.GetProperty("lanes").GetProperty("span").GetProperty("status").GetString());
    }

    [Fact]
    public void ProposalResolutionKeepsAggregateWithoutPerItemTraceByDefault()
    {
        var resolutions = new[]
        {
            new PdfProposalResolutionAudit("b1", "HeadingTopic", null, "HeadingTopic", "no-visual-proposal"),
            new PdfProposalResolutionAudit("b2", "HeadingTopic", "BodySentence", "Uncertain", "conflict-lowered-to-unresolved"),
            new PdfProposalResolutionAudit("b3", "BodySentence", null, "BodySentence", "no-visual-proposal"),
        };

        var diagnostic = PdfStageEvalDiagnostics.BuildProposalResolutionDiagnostics(resolutions, includeItems: false);

        Assert.Equal(2, diagnostic.Decisions.Single(item => item.Resolution == "no-visual-proposal").Count);
        Assert.Equal(1, diagnostic.Decisions.Single(item => item.Resolution == "conflict-lowered-to-unresolved").Count);
        Assert.Null(diagnostic.Items);
    }

    [Fact]
    public void ProposalResolutionPerItemTraceIsRawModeOnly()
    {
        var resolutions = new[]
        {
            new PdfProposalResolutionAudit("b1", "HeadingTopic", null, "HeadingTopic", "no-visual-proposal"),
        };

        var raw = PdfStageEvalDiagnostics.BuildProposalResolutionDiagnostics(resolutions, includeItems: true);

        Assert.Same(resolutions, raw.Items);
    }

    [Fact]
    public void RawModelOutputIsNotEnabledByDefault()
    {
        var options = CommandLineOptions.Parse(["pdf-stage-eval", "input.docx"]);

        Assert.False(options.Pipeline.ShowRawOutput);
    }

    private static RouteExecutionAudit MinimalAudit() => new(
        "diagnostic",
        0,
        0,
        0,
        0,
        [],
        [],
        [],
        [],
        [],
        [],
        []);
}
