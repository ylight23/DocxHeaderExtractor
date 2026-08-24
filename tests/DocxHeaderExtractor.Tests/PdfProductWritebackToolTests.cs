using DocxHeaderExtractor.AgentHarness;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// M9.5b: the pdf-first-authority route's writeback acts on the exact <c>PdfProductOutput</c> the
/// pipeline materialized (carried on <see cref="DocumentOutline.ProductOutput"/>), never a
/// reconstruction through <see cref="HeadingRecord"/>.
/// </summary>
public sealed class PdfProductWritebackToolTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"dhx-product-action-{Guid.NewGuid():N}")).FullName;

    private readonly string _source;

    public PdfProductWritebackToolTests()
    {
        _source = Path.Combine(_dir, "nguon.docx");
        SampleDocumentFactory.Create(_source);
    }

    [Fact]
    public async Task Writes_via_PdfProductWriteback_using_the_outlines_own_ProductOutput()
    {
        var slim = new DocxSlimExtractor().Extract(_source);
        var target = Path.Combine(_dir, "dich.docx");
        var candidate = slim.Paragraphs.First(p =>
            p.Role == ParagraphRole.HeadingCandidate && p.OutlineLevel is null);

        var productOutput = new PdfProductOutput("sha",
        [
            new PdfProductHeading(
                $"@{candidate.StableId}#0-{candidate.Text.Length}", candidate.Index, candidate.StableId,
                new DocxTextSpan(0, candidate.Text.Length), candidate.Text, "Heading", 3, null, true, []),
        ]);
        // DecisionStatus deliberately not RequiresReview here so the harness's human-review gate lets
        // this run through to the action tool - that gate is a separate, deliberate policy this test
        // is not exercising; ANOTHER test covers that PdfProductOutlineAdapter always sets it.
        var headings = new[]
        {
            new HeadingRecord
            {
                Index = candidate.Index, StableId = candidate.StableId, Level = 3, Text = candidate.Text,
                Source = HeadingSource.Model, Confidence = 1.0,
                DecisionStatus = HeadingDecisionStatus.AutoAcceptedCalibrated,
            },
        };
        var outline = new DocumentOutline
        {
            File = slim.FileName,
            ParagraphCount = slim.Paragraphs.Count,
            CandidateCount = headings.Length,
            Headings = headings,
            ProductOutput = productOutput,
        };

        using var tool = new StubExtractionTool(outline);
        using var action = new PdfProductWritebackTool(new ExtractionOptions());
        var harness = new DocumentAgentHarness(tool, actionTool: action);

        var result = await harness.RunAsync(new DocumentAgentRequest(_source)
        {
            WritebackTargetPath = target,
        });

        Assert.Equal(AgentRunOutcome.Completed, result.Outcome);
        Assert.Equal(1, result.Writeback!.Applied);
        Assert.Equal(2, new DocxSlimExtractor().Extract(target).ByIndex(candidate.Index)!.OutlineLevel); // level 3 -> outlineLvl 2
    }

    [Fact]
    public async Task An_outline_without_ProductOutput_fails_closed_instead_of_writing_anything()
    {
        var slim = new DocxSlimExtractor().Extract(_source);
        var outline = new DocumentOutline
        {
            File = slim.FileName,
            ParagraphCount = slim.Paragraphs.Count,
            CandidateCount = 0,
            Headings = [],
            ProductOutput = null,
        };

        using var tool = new StubExtractionTool(outline);
        using var action = new PdfProductWritebackTool(new ExtractionOptions());
        var harness = new DocumentAgentHarness(tool, actionTool: action);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.RunAsync(new DocumentAgentRequest(_source)
            {
                WritebackTargetPath = Path.Combine(_dir, "dich.docx"),
            }));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private sealed class StubExtractionTool(DocumentOutline outline) : IDocumentExtractionTool
    {
        public AgentToolDescriptor Descriptor { get; } = new(
            "stub_extract", "Outline dựng sẵn", AgentToolRisk.Low,
            SendsDataExternally: false, MutatesExternalState: false) { SupportsRepair = true };

        public Task<DocumentOutline> ExecuteAsync(
            AgentToolInvocation invocation, CancellationToken ct = default) => Task.FromResult(outline);

        public void Dispose() { }
    }
}
