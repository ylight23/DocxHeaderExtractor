using DocxHeaderExtractor.AgentHarness;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Đường đi trọn vẹn của hành động ghi: harness thật, guardrail thật, validator thật và tool ghi
/// thật trên một .docx thật. Chỉ tầng suy luận là giả — nó không phải phần đang được kiểm ở đây.
/// </summary>
public sealed class OutlineWritebackToolTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"dhx-action-{Guid.NewGuid():N}")).FullName;

    private readonly string _source;

    public OutlineWritebackToolTests()
    {
        _source = Path.Combine(_dir, "nguon.docx");
        SampleDocumentFactory.Create(_source);
    }

    [Fact]
    public async Task Accepted_outline_reaches_the_disk_and_survives_a_re_read()
    {
        var slim = new DocxSlimExtractor().Extract(_source);
        var target = Path.Combine(_dir, "dich.docx");
        var candidate = slim.Paragraphs.First(p =>
            p.Role == ParagraphRole.HeadingCandidate && p.OutlineLevel is null);

        using var tool = new StubExtractionTool(Outline(slim, Accepted(candidate, level: 3)));
        using var action = new OutlineWritebackTool(new ExtractionOptions());
        var harness = new DocumentAgentHarness(tool, actionTool: action);

        var result = await harness.RunAsync(new DocumentAgentRequest(_source)
        {
            WritebackTargetPath = target,
        });

        Assert.Equal(AgentRunOutcome.Completed, result.Outcome);
        Assert.Equal(1, result.Writeback!.Applied);
        Assert.Equal(2, new DocxSlimExtractor().Extract(target).ByIndex(candidate.Index)!.OutlineLevel);
        Assert.Contains("dich.docx", AgentRunNarrator.Describe(result));
    }

    [Fact]
    public async Task Guardrail_rejects_a_target_directory_that_does_not_exist()
    {
        var slim = new DocxSlimExtractor().Extract(_source);
        using var tool = new StubExtractionTool(Outline(slim));
        using var action = new OutlineWritebackTool(new ExtractionOptions());
        var harness = new DocumentAgentHarness(tool, actionTool: action);

        var error = await Assert.ThrowsAsync<AgentRunBlockedException>(() =>
            harness.RunAsync(new DocumentAgentRequest(_source)
            {
                WritebackTargetPath = Path.Combine(_dir, "khong-ton-tai", "dich.docx"),
            }));

        Assert.Equal("writeback_directory_missing", error.Code);
        Assert.Equal(0, tool.Calls);
    }

    private static HeadingRecord Accepted(SlimParagraph paragraph, int level) => new()
    {
        Index = paragraph.Index,
        StableId = paragraph.StableId,
        Level = level,
        Text = paragraph.Text,
        StyleId = paragraph.StyleId,
        Source = HeadingSource.Model,
        Confidence = 0.95,
        DecisionStatus = HeadingDecisionStatus.AutoAcceptedCalibrated,
    };

    private static DocumentOutline Outline(SlimDocument slim, params HeadingRecord[] headings) => new()
    {
        File = slim.FileName,
        ParagraphCount = slim.Paragraphs.Count,
        CandidateCount = headings.Length,
        Headings = headings,
    };

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private sealed class StubExtractionTool(DocumentOutline outline) : IDocumentExtractionTool
    {
        public int Calls { get; private set; }

        public AgentToolDescriptor Descriptor { get; } = new(
            "stub_extract", "Outline dựng sẵn", AgentToolRisk.Low,
            SendsDataExternally: false, MutatesExternalState: false) { SupportsRepair = true };

        public Task<DocumentOutline> ExecuteAsync(
            AgentToolInvocation invocation, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(outline);
        }

        public void Dispose() { }
    }
}
