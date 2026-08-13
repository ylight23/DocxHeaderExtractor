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
        var harness = Harness(tool);

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
        var harness = Harness(tool, sink: sink);

        var result = await harness.RunAsync(new DocumentAgentRequest(
            _input, AllowExternalDataTransfer: true));

        Assert.Equal(AgentRunOutcome.Completed, result.Outcome);
        Assert.Equal(1, tool.Calls);
        // skill contract + chọn tool + 4 guardrail + tool + 2 validator + gate
        Assert.Equal(10, result.Steps);
        Assert.Equal(0, result.RepairAttempts);
        Assert.Null(result.Writeback);
        Assert.Equal(result.Trace, observed);
        Assert.All(result.Trace, e => Assert.Equal(result.RunId, e.RunId));
        Assert.Equal(Enumerable.Range(1, result.Trace.Count), result.Trace.Select(e => e.Sequence));
    }

    /// <summary>
    /// Pipeline ghi document view ra DumpXmlPath ngay giữa lượt chạy, không qua IDocumentActionTool.
    /// Chốt "agent không sửa file gốc" phải áp cho cả đường ghi đó, nếu không thì một cờ debug đủ
    /// để ghi đè tài liệu nguồn mà không guardrail nào lên tiếng.
    /// </summary>
    [Fact]
    public async Task Duong_ghi_phu_cua_tool_khong_duoc_de_len_tai_lieu_nguon()
    {
        using var tool = new FakeTool(Outline()) { SideEffectPaths = [_input] };
        var harness = Harness(tool);

        var error = await Assert.ThrowsAsync<AgentRunBlockedException>(() =>
            harness.RunAsync(new DocumentAgentRequest(_input)));

        Assert.Equal("side_effect_overwrites_source", error.Code);
        Assert.Equal("tool_side_effect_paths", error.Guardrail);
        Assert.Equal(0, tool.Calls);
    }

    [Fact]
    public async Task Duong_ghi_phu_tro_vao_thu_muc_khong_ton_tai_bi_chan_truoc_khi_chay()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"dhx-no-dir-{Guid.NewGuid():N}", "dump.xml");
        using var tool = new FakeTool(Outline()) { SideEffectPaths = [missing] };
        var harness = Harness(tool);

        var error = await Assert.ThrowsAsync<AgentRunBlockedException>(() =>
            harness.RunAsync(new DocumentAgentRequest(_input)));

        Assert.Equal("side_effect_directory_missing", error.Code);
        Assert.Equal(0, tool.Calls);
    }

    /// <summary>
    /// Guardrail chặn theo LỜI HỨA của descriptor — một cờ tính đúng một lần lúc dựng tool, trong
    /// khi bên trong tool có tới năm lượt hỏi mô hình. Validator này là vế còn lại: đối chiếu bằng
    /// cái ĐÃ XẢY RA. Ở đây tool khai "chỉ chạy cục bộ" nên guardrail cho qua, nhưng provenance nói
    /// backend OpenRouter đã chạy — run phải fail chứ không được im lặng cho qua.
    /// </summary>
    [Fact]
    public async Task Provenance_to_cao_gui_du_lieu_ra_ngoai_thi_run_fail_du_descriptor_hua_cuc_bo()
    {
        var outline = Outline();
        outline.Provenance = new OutlineRunProvenance("OpenRouter", true,
        [
            new OutlinePass("classify", 3, 12, true),
            new OutlinePass("critic", 1, 2, true),
        ]);
        using var tool = new FakeTool(outline);
        var harness = Harness(tool, repairAttempts: 0);

        var error = await Assert.ThrowsAsync<AgentOutputValidationException>(() =>
            harness.RunAsync(new DocumentAgentRequest(_input)));

        Assert.Contains(error.Issues, i => i.Code == "provenance_external_without_consent");
        Assert.Contains(error.Issues, i => i.Code == "provenance_contradicts_descriptor");
    }

    /// <summary>Cấp quyền rồi, và descriptor cũng khai đúng, thì provenance không cản gì.</summary>
    [Fact]
    public async Task Provenance_khop_voi_quyen_da_cap_thi_khong_can_gi()
    {
        var outline = Outline();
        outline.Provenance = new OutlineRunProvenance("OpenRouter", true,
            [new OutlinePass("classify", 2, 8, true)]);
        using var tool = new FakeTool(outline, sendsDataExternally: true);
        var harness = Harness(tool);

        var result = await harness.RunAsync(new DocumentAgentRequest(
            _input, AllowExternalDataTransfer: true));

        Assert.Equal(AgentRunOutcome.Completed, result.Outcome);
        Assert.Equal("OpenRouter", result.Outline.Provenance!.Backend);
    }

    [Fact]
    public async Task Precision_gate_hands_uncertain_heading_to_human()
    {
        using var tool = new FakeTool(Outline(Heading(1, review: true)));
        var harness = Harness(tool);

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
            options: new AgentHarnessOptions { MaxSteps = 4, MaxRepairAttempts = 0 },
            skill: Skill());

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
        var harness = Harness(tool, repairAttempts: 0);

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
        var harness = Harness(tool, repairAttempts: 0);

        var error = await Assert.ThrowsAsync<AgentOutputValidationException>(() =>
            harness.RunAsync(new DocumentAgentRequest(_input)));

        Assert.Contains(error.Issues, issue => issue.Code == "heading_span_not_grounded");
    }

    [Fact]
    public async Task Grounding_validator_accepts_cleaned_text_layout_heading_from_source_span()
    {
        var heading = new HeadingRecord
        {
            Index = 1,
            Level = 2,
            Text = "2.1 Negotiation",
            OriginalText = "2.1 • Negotiation 15 prone to zero-sum thinking.",
            HeadingSpan = new TextOffsetSpan(0, "2.1 • Negotiation 15 ".Length),
            InlineBody = "prone to zero-sum thinking.",
            InlineBodySpan = new TextOffsetSpan(
                "2.1 • Negotiation 15 ".Length,
                "2.1 • Negotiation 15 prone to zero-sum thinking.".Length),
            Confidence = 0.9,
            DecisionStatus = HeadingDecisionStatus.AutoAcceptedEvidence,
        };
        using var tool = new FakeTool(Outline(heading));
        var harness = Harness(tool, repairAttempts: 0);

        var result = await harness.RunAsync(new DocumentAgentRequest(_input));

        Assert.Equal(AgentRunOutcome.Completed, result.Outcome);
        Assert.Equal("2.1 Negotiation", Assert.Single(result.Outline.Headings).Text);
    }

    [Fact]
    public async Task Grounding_validator_cho_phep_nhieu_heading_cung_index_neu_text_khac_nhau()
    {
        using var tool = new FakeTool(Outline(
            new HeadingRecord { Index = 1, Level = 2, Text = "Chương I QUY ĐỊNH CHUNG", Confidence = 1.0, DecisionStatus = HeadingDecisionStatus.AutoAcceptedEvidence },
            new HeadingRecord { Index = 1, Level = 4, Text = "Điều 1. Phạm vi điều chỉnh", Confidence = 1.0, DecisionStatus = HeadingDecisionStatus.AutoAcceptedEvidence }));
        var harness = Harness(tool);

        var result = await harness.RunAsync(new DocumentAgentRequest(_input));

        Assert.Equal(AgentRunOutcome.Completed, result.Outcome);
        Assert.Equal(0, result.RepairAttempts);
        Assert.Equal(2, result.Outline.Headings.Count);
    }

    // ─── Vòng sửa có giới hạn ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Validator_failure_quarantines_offending_index_and_rebuilds()
    {
        var bad = new HeadingRecord { Index = 99, Level = 1, Text = "Bịa", Confidence = 0.9 };
        using var tool = new FakeTool(Outline(bad), Outline(Heading(1)));
        var harness = Harness(tool);

        var result = await harness.RunAsync(new DocumentAgentRequest(_input));

        Assert.Equal(AgentRunOutcome.Completed, result.Outcome);
        Assert.Equal(2, tool.Calls);
        Assert.Equal(1, result.RepairAttempts);
        Assert.Equal([99], tool.Invocations[1].Feedback!.QuarantineIndexes);
        Assert.Contains(result.Trace, e => e.Stage == "repair" && e.Kind == AgentRunEventKind.Repairing);
    }

    [Fact]
    public async Task Repair_budget_is_exhausted_then_run_fails_closed()
    {
        var bad = new HeadingRecord { Index = 99, Level = 1, Text = "Bịa", Confidence = 0.9 };
        using var tool = new FakeTool(Outline(bad), Outline(bad), Outline(bad));
        var harness = Harness(tool);

        await Assert.ThrowsAsync<AgentOutputValidationException>(() =>
            harness.RunAsync(new DocumentAgentRequest(_input)));

        // Một lượt đầu + đúng một lượt sửa; không lặp thêm dù kết quả vẫn hỏng.
        Assert.Equal(2, tool.Calls);
    }

    [Fact]
    public async Task Tool_without_repair_support_is_not_asked_twice()
    {
        var bad = new HeadingRecord { Index = 99, Level = 1, Text = "Bịa", Confidence = 0.9 };
        using var tool = new FakeTool(Outline(bad), Outline(Heading(1))) { SupportsRepair = false };
        var harness = Harness(tool);

        await Assert.ThrowsAsync<AgentOutputValidationException>(() =>
            harness.RunAsync(new DocumentAgentRequest(_input)));

        Assert.Equal(1, tool.Calls);
    }

    // ─── Hợp đồng skill ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Skill_contract_blocks_run_when_a_required_validator_is_missing()
    {
        using var tool = new FakeTool(Outline());
        var harness = new DocumentAgentHarness(
            tool,
            validators: [],
            skill: Skill(validators: ["outline_grounding"]));

        var error = await Assert.ThrowsAsync<AgentSkillContractException>(() =>
            harness.RunAsync(new DocumentAgentRequest(_input)));

        Assert.Contains("outline_grounding", error.Message);
        Assert.Equal(0, tool.Calls);
    }

    [Fact]
    public async Task Skill_contract_caps_repair_attempts()
    {
        using var tool = new FakeTool(Outline());
        var harness = new DocumentAgentHarness(
            tool,
            options: new AgentHarnessOptions { MaxRepairAttempts = 3 },
            skill: Skill(maxRepairAttempts: 1));

        var error = await Assert.ThrowsAsync<AgentSkillContractException>(() =>
            harness.RunAsync(new DocumentAgentRequest(_input)));

        Assert.Contains("MaxRepairAttempts", error.Message);
        Assert.Equal(0, tool.Calls);
    }

    [Fact]
    public async Task Shipped_skill_is_satisfied_by_the_default_harness_configuration()
    {
        using var tool = new FakeTool(Outline());
        var harness = new DocumentAgentHarness(tool);   // nạp SKILL.md thật từ đĩa

        var result = await harness.RunAsync(new DocumentAgentRequest(_input));

        Assert.Equal("heading-extraction", harness.Skill.Name);
        Assert.Equal(harness.Skill, result.Skill);
        Assert.Contains(result.Trace, e =>
            e.Stage == "skill.contract" && e.Kind == AgentRunEventKind.Passed &&
            e.Message.Contains(harness.Skill.Version));
    }

    // ─── Hành động ghi ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Writeback_is_skipped_while_headings_still_need_review()
    {
        using var tool = new FakeTool(Outline(Heading(1, review: true)));
        using var action = new FakeActionTool();
        var harness = Harness(tool, actionTool: action);

        var result = await harness.RunAsync(Request(target: _input + ".out.docx"));

        Assert.Equal(AgentRunOutcome.NeedsHumanReview, result.Outcome);
        Assert.Equal(0, action.Calls);
        Assert.Null(result.Writeback);
        Assert.Contains(result.Trace, e =>
            e.Stage == "action.fake_write" && e.Kind == AgentRunEventKind.Skipped);
    }

    [Fact]
    public async Task Writeback_runs_only_after_validator_and_gate_both_pass()
    {
        using var tool = new FakeTool(Outline(Heading(1)));
        using var action = new FakeActionTool();
        var harness = Harness(tool, actionTool: action);

        var result = await harness.RunAsync(Request(target: _input + ".out.docx"));

        Assert.Equal(AgentRunOutcome.Completed, result.Outcome);
        Assert.Equal(1, action.Calls);
        Assert.Equal(1, result.Writeback!.Applied);

        var stages = result.Trace.Select(e => e.Stage).ToList();
        Assert.True(stages.IndexOf("validator.outline_grounding") < stages.IndexOf("action.fake_write"));
        Assert.True(stages.IndexOf("gate.human_review") < stages.IndexOf("action.fake_write"));
    }

    [Fact]
    public async Task Writeback_target_may_not_be_the_source_document()
    {
        using var tool = new FakeTool(Outline(Heading(1)));
        using var action = new FakeActionTool();
        var harness = Harness(tool, actionTool: action);

        var error = await Assert.ThrowsAsync<AgentRunBlockedException>(() =>
            harness.RunAsync(Request(target: _input)));

        Assert.Equal("writeback_overwrites_source", error.Code);
        Assert.Equal(0, tool.Calls);
        Assert.Equal(0, action.Calls);
    }

    [Fact]
    public async Task Writeback_without_an_action_tool_is_refused_up_front()
    {
        using var tool = new FakeTool(Outline(Heading(1)));
        var harness = Harness(tool);

        var error = await Assert.ThrowsAsync<AgentRunBlockedException>(() =>
            harness.RunAsync(Request(target: _input + ".out.docx")));

        Assert.Equal("writeback_tool_not_configured", error.Code);
        Assert.Equal(0, tool.Calls);
    }

    // ─── Chọn tool bằng luật của code ────────────────────────────────────────────────────────

    [Fact]
    public async Task Registry_picks_the_local_tool_when_the_run_has_no_transfer_consent()
    {
        using var remote = new FakeTool(Outline(Heading(1)), sendsDataExternally: true);
        using var local = new FakeTool(Outline(Heading(0)));
        var harness = new DocumentAgentHarness(
            new AgentToolRegistry([remote, local]), skill: Skill());

        var result = await harness.RunAsync(new DocumentAgentRequest(_input));

        Assert.Equal(0, remote.Calls);
        Assert.Equal(1, local.Calls);
        Assert.Contains(result.Trace, e =>
            e.Stage == "plan.tools" && e.Message.Contains("cục bộ"));
    }

    [Fact]
    public async Task Registry_records_the_chosen_tool_and_the_reason_in_the_trace()
    {
        using var tool = new FakeTool(Outline(Heading(1)), sendsDataExternally: true);
        var harness = Harness(tool);

        var result = await harness.RunAsync(new DocumentAgentRequest(
            _input, AllowExternalDataTransfer: true));

        var plan = Assert.Single(result.Trace, e => e.Stage == "plan.tools");
        Assert.Contains("fake_extract", plan.Message);
        Assert.Contains("gửi dữ liệu ra ngoài", plan.Message);
        Assert.Contains("không hành động ghi", plan.Message);
    }

    [Fact]
    public async Task Registry_does_not_silently_downgrade_to_a_tool_the_run_did_not_ask_for()
    {
        // Chỉ có tool từ xa và run chưa cho phép gửi dữ liệu: đúng đắn là để guardrail chặn với
        // lý do chính xác, không phải lặng lẽ đổi sang một tool khác.
        using var remote = new FakeTool(Outline(Heading(1)), sendsDataExternally: true);
        var harness = new DocumentAgentHarness(
            new AgentToolRegistry(remote), skill: Skill());

        var error = await Assert.ThrowsAsync<AgentRunBlockedException>(() =>
            harness.RunAsync(new DocumentAgentRequest(_input)));

        Assert.Equal("external_data_not_approved", error.Code);
        Assert.Equal(0, remote.Calls);
    }

    [Fact]
    public void Registry_exposes_every_registered_tool_as_the_permission_surface()
    {
        using var tool = new FakeTool(Outline());
        using var action = new FakeActionTool();
        var harness = Harness(tool, actionTool: action);

        Assert.Equal(
            ["fake_extract", "fake_write"],
            harness.Tools.Select(t => t.Name));
        Assert.Contains(harness.Tools, t => t.MutatesExternalState);
    }

    [Fact]
    public async Task Narrator_states_review_backlog_and_the_reason_nothing_was_written()
    {
        using var tool = new FakeTool(Outline(Heading(1, review: true)));
        using var action = new FakeActionTool();
        var harness = Harness(tool, actionTool: action);

        var message = AgentRunNarrator.Describe(
            await harness.RunAsync(Request(target: _input + ".out.docx")));

        Assert.Contains("chờ người duyệt", message);
        Assert.Contains("Chưa ghi ra file", message);
    }

    // ─── Hạ tầng test ────────────────────────────────────────────────────────────────────────

    private DocumentAgentRequest Request(string target) =>
        new(_input) { WritebackTargetPath = target };

    private static DocumentAgentHarness Harness(
        IDocumentExtractionTool tool,
        IAgentRunSink? sink = null,
        IDocumentActionTool? actionTool = null,
        int repairAttempts = 1) =>
        new(tool,
            sink: sink,
            options: new AgentHarnessOptions { MaxRepairAttempts = repairAttempts },
            actionTool: actionTool,
            skill: Skill());

    private static AgentSkill Skill(
        IReadOnlyList<string>? validators = null,
        int maxRepairAttempts = 1) =>
        new("test-skill", "skill dùng cho test", "0.0.1", "deadbeef0000", "(memory)",
            new AgentSkillRequirements
            {
                Validators = validators ?? [],
                MaxRepairAttempts = maxRepairAttempts,
            },
            []);

    private static HeadingRecord Heading(int index, bool review = false) => new()
    {
        Index = index,
        Level = 1,
        Text = "Mục Alpha",
        Confidence = 0.9,
        DecisionStatus = review
            ? HeadingDecisionStatus.RequiresReview
            : HeadingDecisionStatus.AutoAcceptedEvidence,
    };

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

    /// <summary>Trả lần lượt từng outline đã kịch bản hoá; hết kịch bản thì lặp lại cái cuối.</summary>
    private sealed class FakeTool(params DocumentOutline[] outlines) : IDocumentExtractionTool
    {
        private readonly List<AgentToolInvocation> _invocations = [];
        private bool _sendsDataExternally;

        public FakeTool(DocumentOutline outline, bool sendsDataExternally)
            : this([outline]) => _sendsDataExternally = sendsDataExternally;

        public bool SupportsRepair { get; init; } = true;
        public IReadOnlyList<string> SideEffectPaths { get; init; } = [];
        public int Calls => _invocations.Count;
        public IReadOnlyList<AgentToolInvocation> Invocations => _invocations;

        public AgentToolDescriptor Descriptor => new(
            "fake_extract",
            "Synthetic test tool",
            _sendsDataExternally ? AgentToolRisk.Medium : AgentToolRisk.Low,
            _sendsDataExternally,
            MutatesExternalState: SideEffectPaths.Count > 0)
        {
            SupportsRepair = SupportsRepair,
            SideEffectPaths = SideEffectPaths,
        };

        public Task<DocumentOutline> ExecuteAsync(
            AgentToolInvocation invocation,
            CancellationToken ct = default)
        {
            _invocations.Add(invocation);
            return Task.FromResult(outlines[Math.Min(_invocations.Count - 1, outlines.Length - 1)]);
        }

        public void Dispose() { }
    }

    private sealed class FakeActionTool : IDocumentActionTool
    {
        public int Calls { get; private set; }

        public AgentToolDescriptor Descriptor { get; } = new(
            "fake_write", "Synthetic writeback", AgentToolRisk.High,
            SendsDataExternally: false, MutatesExternalState: true);

        public Task<AgentWritebackReport> ExecuteAsync(
            DocumentAgentRequest request,
            DocumentOutline outline,
            CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new AgentWritebackReport(
                request.WritebackTargetPath!, outline.Headings.Count, 0));
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
