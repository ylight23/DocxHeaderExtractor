using DocxHeaderExtractor.AgentHarness;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Tests;

public sealed class AgentHarnessTests : IDisposable
{
    private readonly string _input = Path.Combine(
        Path.GetTempPath(), $"dhx-agent-{Guid.NewGuid():N}.docx");

    public AgentHarnessTests() => File.WriteAllBytes(_input, []);

    [Fact]
    public async Task Remote_tool_is_blocked_without_per_run_consent()
    {
        using var tool = new FakeTool(Outline(), sendsDataExternally: true);
        var harness = new DocumentAgentHarness(tool);

        var error = await Assert.ThrowsAsync<AgentRunBlockedException>(() =>
            harness.RunAsync(new DocumentAgentRequest(_input)));

        Assert.Equal("external_data_not_approved", error.Code);
        Assert.Equal("external_data_transfer", error.Guardrail);
        Assert.Equal(0, tool.Calls);
        Assert.Contains(error.Trace, e =>
            e.Stage == "guardrail.external_data_transfer" && e.Kind == AgentRunEventKind.Blocked);
    }

    [Fact]
    public async Task Approved_remote_tool_runs_and_trace_is_ordered()
    {
        using var tool = new FakeTool(Outline(), sendsDataExternally: true);
        var observed = new List<AgentRunEvent>();
        var sink = new DelegateAgentRunSink((evt, _) =>
        {
            observed.Add(evt);
            return ValueTask.CompletedTask;
        });
        var harness = new DocumentAgentHarness(tool, sink: sink);

        var result = await harness.RunAsync(new DocumentAgentRequest(
            _input, AllowExternalDataTransfer: true));

        Assert.Equal(AgentRunOutcome.Completed, result.Outcome);
        Assert.Equal(1, tool.Calls);
        Assert.Equal(5, result.Steps);
        Assert.Equal(result.Trace, observed);
        Assert.All(result.Trace, e => Assert.Equal(result.RunId, e.RunId));
        Assert.Equal(Enumerable.Range(1, result.Trace.Count), result.Trace.Select(e => e.Sequence));
    }

    [Fact]
    public async Task Precision_gate_hands_uncertain_heading_to_human()
    {
        var heading = new HeadingRecord
        {
            Index = 1,
            Level = 1,
            Text = "Mục Alpha",
            DecisionStatus = HeadingDecisionStatus.RequiresReview,
        };
        using var tool = new FakeTool(Outline(heading));
        var harness = new DocumentAgentHarness(tool);

        var result = await harness.RunAsync(new DocumentAgentRequest(_input));

        Assert.Equal(AgentRunOutcome.NeedsHumanReview, result.Outcome);
        Assert.Equal(1, result.RequiresReview);
        Assert.Contains(result.Trace, e =>
            e.Stage == "gate.human_review" && e.Message.Contains("1 heading"));
    }

    [Fact]
    public async Task Step_budget_stops_unbounded_workflow_before_tool_call()
    {
        using var tool = new FakeTool(Outline());
        var guardrails = Enumerable.Range(1, 4).Select(i => new PassGuardrail($"g{i}")).ToArray();
        var harness = new DocumentAgentHarness(
            tool,
            guardrails,
            options: new AgentHarnessOptions { MaxSteps = 4 });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.RunAsync(new DocumentAgentRequest(_input)));

        Assert.Contains("vượt giới hạn 4 bước", error.Message);
        Assert.Equal(0, tool.Calls);
    }

    [Fact]
    public async Task Grounding_validator_fails_closed_on_hallucinated_source_index()
    {
        var heading = new HeadingRecord
        {
            Index = 99,
            Level = 1,
            Text = "Không tồn tại trong nguồn",
            Confidence = 0.9,
        };
        using var tool = new FakeTool(Outline(heading));
        var harness = new DocumentAgentHarness(tool);

        var error = await Assert.ThrowsAsync<AgentOutputValidationException>(() =>
            harness.RunAsync(new DocumentAgentRequest(_input)));

        Assert.Contains(error.Issues, issue => issue.Code == "source_index_out_of_range");
        Assert.Contains(error.Trace, e =>
            e.Stage == "validator.outline_grounding" && e.Kind == AgentRunEventKind.Blocked);
    }

    [Fact]
    public async Task Grounding_validator_checks_inline_text_against_source_span()
    {
        var heading = new HeadingRecord
        {
            Index = 1,
            Level = 1,
            Text = "Nội dung bịa",
            OriginalText = "3.2. Tỉ lệ thành công: đạt 20%",
            HeadingSpan = new TextOffsetSpan(0, 23),
            Confidence = 0.9,
        };
        using var tool = new FakeTool(Outline(heading));
        var harness = new DocumentAgentHarness(tool);

        var error = await Assert.ThrowsAsync<AgentOutputValidationException>(() =>
            harness.RunAsync(new DocumentAgentRequest(_input)));

        Assert.Contains(error.Issues, issue => issue.Code == "heading_span_not_grounded");
    }

    private static DocumentOutline Outline(params HeadingRecord[] headings) => new()
    {
        File = "synthetic.docx",
        ParagraphCount = 2,
        CandidateCount = headings.Length,
        Headings = headings,
    };

    public void Dispose()
    {
        if (File.Exists(_input)) File.Delete(_input);
    }

    private sealed class FakeTool(DocumentOutline outline, bool sendsDataExternally = false)
        : IDocumentExtractionTool
    {
        public int Calls { get; private set; }

        public AgentToolDescriptor Descriptor { get; } = new(
            "fake_extract",
            "Synthetic test tool",
            sendsDataExternally ? AgentToolRisk.Medium : AgentToolRisk.Low,
            sendsDataExternally,
            MutatesExternalState: false);

        public Task<DocumentOutline> ExecuteAsync(
            DocumentAgentRequest request,
            CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(outline);
        }

        public void Dispose() { }
    }

    private sealed class PassGuardrail(string name) : IDocumentAgentGuardrail
    {
        public string Name => name;

        public ValueTask<AgentGuardrailDecision> EvaluateAsync(
            DocumentAgentGuardrailContext context,
            CancellationToken ct = default) =>
            ValueTask.FromResult(AgentGuardrailDecision.Pass("ok", "ok"));
    }
}
