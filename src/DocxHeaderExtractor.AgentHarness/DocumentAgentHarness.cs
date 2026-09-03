using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;

using DocxHeaderExtractor.Application.Tasks;
using DocxHeaderExtractor.Application.Semantics;
using DocxHeaderExtractor.Application.Runtime;
using DocxHeaderExtractor.Application.Skills;

namespace DocxHeaderExtractor.AgentHarness;

/// <summary>
/// Harness điều phối một workflow agent có giới hạn:
/// skill contract → guardrail → tool (± lượt sửa) → validator → human-review gate → hành động ghi.
/// <para>
/// Thứ tự và điều kiện dừng do code quyết định, không do model chọn. Model không được bỏ qua
/// guardrail, tự ghi nhãn vàng, tự thay đổi code/prompt hay tự nâng quyền tool.
/// </para>
/// </summary>
public sealed class DocumentAgentHarness
{
    private readonly IAgentToolRegistry _registry;
    private readonly IReadOnlyList<IDocumentAgentGuardrail> _guardrails;
    private readonly IReadOnlyList<IDocumentAgentValidator> _validators;
    private readonly IAgentRunSink _sink;
    private readonly AgentHarnessOptions _options;
    private readonly AgentSkill _skill;
    private readonly IInputResourceResolver? _inputResourceResolver;
    private readonly SemanticRegistry? _semanticRegistry;
    private readonly ITaskRunStore? _runStore;
    private readonly ITaskTelemetrySink? _telemetrySink;

    public DocumentAgentHarness(
        IDocumentExtractionTool tool,
        IEnumerable<IDocumentAgentGuardrail>? guardrails = null,
        IEnumerable<IDocumentAgentValidator>? validators = null,
        IAgentRunSink? sink = null,
        AgentHarnessOptions? options = null,
        IDocumentActionTool? actionTool = null,
        AgentSkill? skill = null,
        IInputResourceResolver? inputResourceResolver = null,
        SemanticRegistry? semanticRegistry = null,
        ITaskRunStore? runStore = null,
        ITaskTelemetrySink? telemetrySink = null)
        : this(new AgentToolRegistry(tool, actionTool), guardrails, validators, sink, options, skill,
            inputResourceResolver, semanticRegistry, runStore, telemetrySink)
    {
    }

    public DocumentAgentHarness(
        IAgentToolRegistry registry,
        IEnumerable<IDocumentAgentGuardrail>? guardrails = null,
        IEnumerable<IDocumentAgentValidator>? validators = null,
        IAgentRunSink? sink = null,
        AgentHarnessOptions? options = null,
        AgentSkill? skill = null,
        IInputResourceResolver? inputResourceResolver = null,
        SemanticRegistry? semanticRegistry = null,
        ITaskRunStore? runStore = null,
        ITaskTelemetrySink? telemetrySink = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _guardrails = (guardrails ?? DefaultGuardrails()).ToArray();
        _validators = (validators ?? DefaultValidators()).ToArray();
        _sink = sink ?? NullAgentRunSink.Instance;
        _options = options ?? new AgentHarnessOptions();
        _options.Validate();
        _skill = skill ?? AgentSkillLoader.LoadDefault();
        _inputResourceResolver = inputResourceResolver;
        _semanticRegistry = semanticRegistry;
        _runStore = runStore;
        _telemetrySink = telemetrySink;
    }

    public AgentSkill Skill => _skill;

    /// <summary>Bề mặt quyền của harness này: mọi tool có thể được chọn, kèm rủi ro đã khai báo.</summary>
    public IReadOnlyList<DocxHeaderExtractor.Application.Capabilities.CapabilityDescriptor> Tools => _registry.Descriptors;

    public async Task<DocumentAgentRunResult> RunAsync(
        DocumentAgentRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var runId = Guid.NewGuid();
        var trace = new List<AgentRunEvent>();
        var sequence = 0;
        var steps = 0;
        var startedAt = DateTimeOffset.UtcNow;
        string? planId = null;
        DocxHeaderExtractor.Application.Capabilities.CapabilityDescriptor? capability = null;

        async ValueTask EmitAsync(string stage, AgentRunEventKind kind, string message)
        {
            var evt = new AgentRunEvent(runId, ++sequence, DateTimeOffset.UtcNow, stage, kind, message);
            trace.Add(evt);
            await _sink.WriteAsync(evt, ct);
        }

        void TakeStep(string stage)
        {
            steps++;
            if (steps > _options.MaxSteps)
                throw new InvalidOperationException(
                    $"Agent run vượt giới hạn {_options.MaxSteps} bước tại stage {stage}.");
        }

        try
        {
            await EmitAsync("run", AgentRunEventKind.Started,
                $"Bắt đầu run trên {_registry.Descriptors.Count} tool đã đăng ký.");

            // 1. Hợp đồng skill. Chạy trước mọi thứ khác: nếu cấu hình harness không thoả policy
            // đã version-control thì không có lý do gì để tiêu một giây suy luận nào.
            TakeStep("skill.contract");
            var contractIssue = CheckSkillContract();
            if (contractIssue is not null)
            {
                await EmitAsync("skill.contract", AgentRunEventKind.Blocked, contractIssue);
                throw new AgentSkillContractException(runId, _skill, contractIssue, trace.ToArray());
            }
            await EmitAsync("skill.contract", AgentRunEventKind.Passed,
                $"Cấu hình thoả policy {_skill.Name}@{_skill.Version} ({_skill.Digest}).");

            // 2. Dựng và kiểm tra intent trước khi chọn capability. Đây là application contract;
            // input guardrail vẫn là nơi duy nhất quyết định file có thực sự tồn tại/hợp lệ.
            var proposal = DocumentTaskAdapters.Propose(request);
            var genericRequest = GenericTaskRequestAdapter.FromDocumentRequest(request);
            await EmitAsync("intent.proposal", AgentRunEventKind.Completed,
                $"Intent {proposal.Operation} cho {genericRequest.Resources.Count} resource.");
            var intentValidation = IntentValidator.Validate(proposal);
            if (!intentValidation.IsExecutable && intentValidation.Intent is null)
                throw new InvalidOperationException(
                    $"Intent không thực thi được: {string.Join(", ", intentValidation.Reasons)}.");
            var intent = intentValidation.Intent!;
            await EmitAsync("intent.validation", AgentRunEventKind.Passed,
                "Intent hợp lệ; chưa cấp quyền thực thi hay tạo authority.");

            if (_semanticRegistry is not null)
            {
                TakeStep("semantic.resolve");
                foreach (var concept in proposal.Concepts)
                {
                    if (!_semanticRegistry.Resolve(concept, SemanticDefinitionKind.Concept).IsResolved)
                        throw new InvalidOperationException($"Semantic concept không được đăng ký: {concept}.");
                }
                if (!_semanticRegistry.Resolve(proposal.OutputShape, SemanticDefinitionKind.Schema).IsResolved)
                    throw new InvalidOperationException(
                        $"Semantic output schema không được đăng ký: {proposal.OutputShape}.");
                await EmitAsync("semantic.resolve", AgentRunEventKind.Passed,
                    "Concept và output schema đã resolve qua trusted semantic registry.");
            }

            if (_inputResourceResolver is not null)
            {
                TakeStep("source.resolve");
                foreach (var resource in genericRequest.Resources)
                {
                    var resolved = await _inputResourceResolver.ResolveAsync(resource, ct);
                    if (!resolved.LeaveOpen)
                        await resolved.Content.DisposeAsync();
                }
                await EmitAsync("source.resolve", AgentRunEventKind.Passed,
                    $"Đã resolve {genericRequest.Resources.Count} resource qua host source boundary.");
            }

            // 3. Chọn capability bằng luật của code, rồi ghi lựa chọn kèm lý do vào trace.
            TakeStep("plan.tools");
            var selection = _registry.Select(request);
            var tool = selection.Extraction;
            var actionTool = selection.Action;
            var compiledPlan = DocumentTaskAdapters.Compile(genericRequest, intent, selection);
            var semanticPlan = compiledPlan.Semantic;
            await EmitAsync("plan.semantic", AgentRunEventKind.Completed,
                $"SemanticTaskPlan={semanticPlan.TaskName}.");
            var executionPlan = compiledPlan.Execution;
            planId = semanticPlan.PlanId;
            capability = tool.Descriptor;
            await EmitAsync("plan.execution", AgentRunEventKind.Completed,
                $"ExecutionPlan dùng capability {executionPlan.Steps[0].CapabilityId}.");
            await EmitAsync("plan.tools", AgentRunEventKind.Passed, $"Chọn {selection.Rationale}");
            await EmitAsync("capability.resolve", AgentRunEventKind.Passed,
                $"Resolved capability {tool.Descriptor.Name}.");
            await RecordLifecycleAsync(
                runId,
                startedAt,
                PersistedRunLifecycle.Running,
                TaskRunStatus.Started,
                planId,
                request,
                capability,
                outline: null,
                failure: null,
                ct);

            // PolicyEvaluator chỉ mô tả quyết định ở application boundary. Các guardrail hiện hữu
            // tiếp tục là enforcement point để không tạo một policy implementation song song.
            var policy = DocumentTaskAdapters.EvaluatePolicy(executionPlan, selection, request, _skill);
            await EmitAsync("policy.approval", policy.Kind == PolicyDecisionKind.Denied
                    ? AgentRunEventKind.Skipped
                    : AgentRunEventKind.Completed,
                $"{policy.Code}: {policy.Message}");

            // 4. Guardrail.
            var guardrailContext = new DocumentAgentGuardrailContext(
                request, tool.Descriptor, actionTool?.Descriptor);
            foreach (var guardrail in _guardrails)
            {
                TakeStep($"guardrail.{guardrail.Name}");
                var decision = await guardrail.EvaluateAsync(guardrailContext, ct);
                var stage = $"guardrail.{guardrail.Name}";
                if (!decision.Allowed)
                {
                    await EmitAsync(stage, AgentRunEventKind.Blocked, decision.Message);
                    throw new AgentRunBlockedException(runId, guardrail.Name, decision, trace.ToArray());
                }
                await EmitAsync(stage, AgentRunEventKind.Passed, decision.Message);
            }

            // 5. Capability execution + validator, có tối đa MaxRepairAttempts lượt dựng lại.
            var toolStage = $"tool.{tool.Descriptor.Name}";
            DocumentOutline outline;
            AgentRepairFeedback? feedback = null;
            var attempt = 0;

            while (true)
            {
                attempt++;
                TakeStep(toolStage);
                await EmitAsync(toolStage, AgentRunEventKind.Started,
                    feedback is null
                        ? "Bắt đầu phân tích tài liệu."
                        : $"Dựng lại lượt {attempt} sau khi cách ly {feedback.QuarantineIndexes.Count} đoạn.");

                outline = await tool.ExecuteAsync(new AgentToolInvocation(request, attempt, feedback), ct);
                await EmitAsync(toolStage, AgentRunEventKind.Completed,
                    $"Tool trả {outline.Headings.Count} heading từ {outline.ParagraphCount} đoạn.");
                await EmitAsync("capability.execute", AgentRunEventKind.Completed,
                    $"Capability hoàn tất lượt {attempt}.");

                var validationContext = new DocumentAgentValidationContext(request, tool.Descriptor);
                var issues = new List<AgentValidationIssue>();
                foreach (var validator in _validators)
                {
                    TakeStep($"validator.{validator.Name}");
                    var validation = await validator.ValidateAsync(outline, validationContext, ct);
                    var stage = $"validator.{validator.Name}";
                    if (validation.IsValid)
                    {
                        await EmitAsync(stage, AgentRunEventKind.Passed,
                            "Index, cấp, thứ tự và source span đều hợp lệ.");
                        continue;
                    }

                    issues.AddRange(validation.Issues);
                    await EmitAsync(stage, AgentRunEventKind.Blocked,
                        $"Chặn output vì {validation.Issues.Count} bất biến nguồn bị vi phạm.");
                }

                if (issues.Count == 0) break;

                var quarantine = issues
                    .Select(i => i.Index)
                    .Where(i => i is not null)
                    .Select(i => i!.Value)
                    .Distinct()
                    .OrderBy(i => i)
                    .ToArray();

                // Không sửa được thì fail-closed. Ba trường hợp: hết lượt, tool không biết dựng
                // lại, hoặc lỗi không quy được về đoạn nào (cách ly mù không phải là sửa).
                if (attempt > _options.MaxRepairAttempts ||
                    !tool.Descriptor.SupportsRepair ||
                    quarantine.Length == 0)
                    throw new AgentOutputValidationException(runId, issues, trace.ToArray());

                // Nói RÕ vì sao bác. Bản cũ chỉ báo "cách ly N đoạn": mỗi lần validator bác là
                // một lượt dựng lại — chi phí GẤP ĐÔI — mà không ai chẩn đoán được nguyên nhân.
                // Đo được cái giá: ở §132 một mục do mô hình bù bị nuốt mất vì danh sách sai thứ
                // tự, và phải cắm mốc in chỉ số thủ công mới truy ra; ở 063_Advanced_Linear_Algebra
                // lượt dựng lại đẩy 11 khối context thành 17 khối, tiêu 2.983 giây.
                var lyDo = string.Join("; ", issues.Take(3).Select(x => x.Message));
                await EmitAsync("repair", AgentRunEventKind.Repairing,
                    $"Cách ly {quarantine.Length} đoạn vi phạm rồi dựng lại " +
                    $"(lượt {attempt}/{_options.MaxRepairAttempts}). Lý do: {lyDo}" +
                    (issues.Count > 3 ? $" (+{issues.Count - 3} lỗi nữa)" : string.Empty));
                feedback = new AgentRepairFeedback(issues, quarantine);
            }

            await EmitAsync("validation.source-authority", AgentRunEventKind.Passed,
                $"Đã qua {_validators.Count} validator nguồn/authority.");

            // 6. Human-review gate.
            TakeStep("gate.human_review");
            // Cổng này là cổng chống ẢO GIÁC, nên nó chỉ đếm mục do MÔ HÌNH dựng (§109 tầng 2).
            //
            // Bản cũ đếm mọi mục thiếu bằng chứng bất kể nguồn, và vì một tài liệu đi trọn MỘT
            // nhánh route nên nó chặn toàn-bộ-hoặc-không-gì: đo trên corpus, 063 chặn 25/25,
            // 030 chặn 12/12, 020 chặn 48/48, còn 019 chặn 0/165. Chạy --no-llm không có mô hình
            // nào tham gia mà vẫn bị chặn — cổng chống ảo giác chặn nhầm đường suy luận cấu trúc.
            //
            // Mục do luật deterministic hoặc heuristic dựng vẫn GIỮ NGUYÊN DecisionStatus và tự
            // tin thấp của chúng — người đọc vẫn thấy "chưa đủ bằng chứng" — nhưng chúng không
            // còn chặn writeback. Đánh đổi đã được nêu rõ trước khi chọn: mục heuristic đoán sai
            // (165 mục số thứ tự văn xuôi của 019) giờ đi thẳng ra ngoài.
            var reviewCount = outline.Headings.Count(h =>
                h.Source == HeadingSource.Model &&
                (h.DecisionStatus == HeadingDecisionStatus.RequiresReview || h.Disputed));
            var outcome = reviewCount > 0
                ? AgentRunOutcome.NeedsHumanReview
                : AgentRunOutcome.Completed;
            await EmitAsync("gate.human_review", AgentRunEventKind.Completed,
                reviewCount > 0
                    ? $"Chuyển {reviewCount} heading sang người duyệt."
                    : "Không còn heading bắt buộc người duyệt.");

            await EmitAsync("projection.prompt-driven", AgentRunEventKind.Completed,
                "Projection lấy từ capability result sau validation; không tạo authority mới.");

            // 7. Hành động ghi — chỉ sau khi output đã qua validator VÀ qua gate.
            AgentWritebackReport? writeback = null;
            if (actionTool is not null && request.WantsAction)
            {
                var actionStage = $"action.{actionTool.Descriptor.Name}";
                if (request.WantsWriteback &&
                    outcome == AgentRunOutcome.NeedsHumanReview &&
                    _skill.Requires.HumanReviewBeforeWriteback)
                {
                    TakeStep(actionStage);
                    await EmitAsync(actionStage, AgentRunEventKind.Skipped,
                        $"Policy {_skill.Name}@{_skill.Version} yêu cầu duyệt xong {reviewCount} mục " +
                        "trước khi tác động ra ngoài.");
                }
                else
                {
                    TakeStep(actionStage);
                    await EmitAsync(actionStage, AgentRunEventKind.Started,
                        request.WantsKeyPackage ? "Ghi partial key package." : "Ghi outline vào bản sao.");
                    writeback = await actionTool.ExecuteAsync(request, outline, ct);
                    await EmitAsync(actionStage, AgentRunEventKind.Completed,
                        request.WantsKeyPackage
                            ? $"Đã ghi package {writeback.Applied} heading, còn {writeback.Skipped} mục ngoài slice: " +
                              Path.GetFileName(writeback.OutputPath)
                            : $"Đã ghi {writeback.Applied} heading, bỏ qua {writeback.Skipped} mục: " +
                        Path.GetFileName(writeback.OutputPath));
                }
            }

            await EmitAsync("run", AgentRunEventKind.Completed, $"Kết thúc run: {outcome}.");

            var completedAt = DateTimeOffset.UtcNow;
            var stageNames = trace.Select(e => e.Stage).Distinct(StringComparer.Ordinal).ToArray();
            var finalProvenance = CreateProvenance(request, tool.Descriptor, outline);
            await RecordLifecycleAsync(
                runId,
                startedAt,
                outcome == AgentRunOutcome.NeedsHumanReview
                    ? PersistedRunLifecycle.NeedsHumanReview
                    : PersistedRunLifecycle.Completed,
                outcome == AgentRunOutcome.NeedsHumanReview
                    ? TaskRunStatus.NeedsHumanReview
                    : TaskRunStatus.Completed,
                semanticPlan.PlanId,
                request,
                tool.Descriptor,
                outline,
                failure: null,
                ct);
            return new DocumentAgentRunResult(runId, outcome, outline, steps, trace.ToArray())
            {
                TaskResult = new GenericTaskResult<DocumentOutline>(
                    runId,
                    semanticPlan.PlanId,
                    outcome.ToString(),
                    new PromptDrivenProjection<DocumentOutline>(
                        outline,
                        "ValidatedStructure",
                        trace.Where(e => e.Stage == "validation.source-authority")
                            .Select(e => e.Stage).Distinct(StringComparer.Ordinal).ToArray()),
                    stageNames,
                    startedAt,
                    completedAt)
                {
                    Provenance = finalProvenance,
                },
                Skill = _skill,
                RepairAttempts = attempt - 1,
                Writeback = writeback,
            };
        }
        catch (OperationCanceledException)
        {
            await RecordLifecycleAsync(
                runId,
                startedAt,
                PersistedRunLifecycle.Cancelled,
                TaskRunStatus.Cancelled,
                planId,
                request,
                capability,
                outline: null,
                new TaskFailure(TaskFailureKind.Cancelled, "cancelled", "Run bị hủy.", "run"),
                CancellationToken.None);
            await EmitWithoutCancellationAsync("run", AgentRunEventKind.Cancelled, "Run đã bị hủy.", trace, runId, ++sequence);
            throw;
        }
        catch (AgentRunBlockedException)
        {
            await RecordLifecycleAsync(
                runId,
                startedAt,
                PersistedRunLifecycle.Blocked,
                TaskRunStatus.Blocked,
                planId,
                request,
                capability,
                outline: null,
                new TaskFailure(TaskFailureKind.PolicyDenied, "agent-run-blocked", "Run bị chặn bởi policy/guardrail.", "run"),
                CancellationToken.None);
            throw;
        }
        catch (AgentSkillContractException)
        {
            await RecordLifecycleAsync(
                runId,
                startedAt,
                PersistedRunLifecycle.Failed,
                TaskRunStatus.Failed,
                planId,
                request,
                capability,
                outline: null,
                new TaskFailure(TaskFailureKind.Validation, "skill-contract-failed", "Skill contract không hợp lệ.", "skill.contract"),
                CancellationToken.None);
            throw;
        }
        catch (AgentOutputValidationException)
        {
            await RecordLifecycleAsync(
                runId,
                startedAt,
                PersistedRunLifecycle.Failed,
                TaskRunStatus.Failed,
                planId,
                request,
                capability,
                outline: null,
                new TaskFailure(TaskFailureKind.Validation, "output-validation-failed", "Output không qua validation.", "validation"),
                CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            await RecordLifecycleAsync(
                runId,
                startedAt,
                PersistedRunLifecycle.Failed,
                TaskRunStatus.Failed,
                planId,
                request,
                capability,
                outline: null,
                new TaskFailure(TaskFailureKind.Unknown, ex.GetType().Name, "Run thất bại.", "run"),
                CancellationToken.None);
            await EmitWithoutCancellationAsync("run", AgentRunEventKind.Failed,
                $"Run thất bại: {ex.GetType().Name}.", trace, runId, ++sequence);
            throw;
        }
    }

    /// <summary>
    /// Đối chiếu cấu hình thực tế với ràng buộc skill. Đây là chỗ SKILL.md có hiệu lực: bỏ một
    /// validator, hạ một guardrail hay nới số lượt sửa quá trần đều làm run dừng trước khi chạy.
    /// </summary>
    private string? CheckSkillContract()
    {
        var guardrails = _guardrails.Select(g => g.Name).ToHashSet(StringComparer.Ordinal);
        var missingGuardrails = _skill.Requires.Guardrails.Where(g => !guardrails.Contains(g)).ToArray();
        if (missingGuardrails.Length > 0)
            return $"Thiếu guardrail mà skill yêu cầu: {string.Join(", ", missingGuardrails)}.";

        var validators = _validators.Select(v => v.Name).ToHashSet(StringComparer.Ordinal);
        var missingValidators = _skill.Requires.Validators.Where(v => !validators.Contains(v)).ToArray();
        if (missingValidators.Length > 0)
            return $"Thiếu validator mà skill yêu cầu: {string.Join(", ", missingValidators)}.";

        if (_options.MaxRepairAttempts > _skill.Requires.MaxRepairAttempts)
            return $"MaxRepairAttempts={_options.MaxRepairAttempts} vượt trần " +
                   $"{_skill.Requires.MaxRepairAttempts} của skill.";

        // Việc "run muốn ghi nhưng không có tool ghi" thuộc về writeback_target guardrail: nó đã
        // giữ toàn bộ luật về đích ghi, tách ra hai nơi thì sớm muộn hai nơi sẽ lệch nhau.
        return null;
    }

    private async ValueTask EmitWithoutCancellationAsync(
        string stage,
        AgentRunEventKind kind,
        string message,
        List<AgentRunEvent> trace,
        Guid runId,
        int sequence)
    {
        var evt = new AgentRunEvent(runId, sequence, DateTimeOffset.UtcNow, stage, kind, message);
        trace.Add(evt);
        await _sink.WriteAsync(evt, CancellationToken.None);
    }

    private async ValueTask RecordLifecycleAsync(
        Guid runId,
        DateTimeOffset startedAt,
        PersistedRunLifecycle lifecycle,
        TaskRunStatus status,
        string? planId,
        DocumentAgentRequest request,
        DocxHeaderExtractor.Application.Capabilities.CapabilityDescriptor? capability,
        DocumentOutline? outline,
        TaskFailure? failure,
        CancellationToken ct)
    {
        if (_runStore is null && _telemetrySink is null) return;
        if (string.IsNullOrWhiteSpace(planId)) return;

        var provenance = CreateProvenance(request, capability, outline);
        var run = new PersistedTaskRun(
            new RunStorageKey(runId.ToString("N")), planId, lifecycle,
            status, startedAt, CompletedAt: lifecycle is PersistedRunLifecycle.Completed
                or PersistedRunLifecycle.NeedsHumanReview or PersistedRunLifecycle.Failed
                or PersistedRunLifecycle.Blocked or PersistedRunLifecycle.Cancelled
                ? DateTimeOffset.UtcNow : null,
            provenance, failure);

        // Runtime state is diagnostic/non-authoritative. A storage outage must not change the
        // validated extraction result or turn a successful authority run into a false failure.
        try
        {
            if (_runStore is not null)
                await _runStore.SaveAsync(run, ct).ConfigureAwait(false);
        }
        catch
        {
            // Host telemetry may be unavailable during shutdown or filesystem failure.
        }

        try
        {
            if (_telemetrySink is not null)
                await _telemetrySink.RecordAsync(new TaskTelemetryEvent(
                    run.Key.RunId, "run.lifecycle", lifecycle.ToString(), DateTimeOffset.UtcNow,
                    new Dictionary<string, string>
                    {
                        ["planId"] = planId,
                        ["status"] = status.ToString(),
                        ["capability"] = capability?.Name ?? "unknown",
                    }), ct).ConfigureAwait(false);
        }
        catch
        {
            // Runtime telemetry is deliberately best-effort and never an authority path.
        }
    }

    private static TaskProvenance CreateProvenance(
        DocumentAgentRequest request,
        DocxHeaderExtractor.Application.Capabilities.CapabilityDescriptor? capability,
        DocumentOutline? outline) =>
        new(
            Path.GetFileName(request.InputPath),
            capability?.Name,
            outline?.Provenance?.Backend,
            outline?.Model,
            outline?.Provenance?.SentDataExternally ?? capability?.SendsDataExternally ?? false,
            "ValidatedStructure");

    private static IEnumerable<IDocumentAgentGuardrail> DefaultGuardrails()
    {
        yield return new InputDocumentGuardrail();
        yield return new ExternalDataTransferGuardrail();
        yield return new WritebackTargetGuardrail();
        yield return new KeyPackageTargetGuardrail();
        yield return new ToolSideEffectPathGuardrail();
    }

    private static IEnumerable<IDocumentAgentValidator> DefaultValidators()
    {
        yield return new OutlineGroundingValidator();
        yield return new RunProvenanceValidator();
    }
}

public sealed class AgentSkillContractException(
    Guid runId,
    AgentSkill skill,
    string message,
    IReadOnlyList<AgentRunEvent> trace)
    : InvalidOperationException($"Cấu hình harness không thoả skill {skill.Name}@{skill.Version}: {message}")
{
    public Guid RunId { get; } = runId;
    public AgentSkill Skill { get; } = skill;
    public IReadOnlyList<AgentRunEvent> Trace { get; } = trace;
}

/// <summary>
/// Composition root cho host: nạp policy skill đúng MỘT lần rồi tái dùng, để mọi run trong cùng
/// tiến trình chạy trên cùng một phiên bản policy thay vì đọc lại file ở mỗi request.
/// </summary>
public sealed class DocumentAgentHarnessFactory
{
    private readonly Lazy<AgentSkill> _skill = new(AgentSkillLoader.LoadDefault, isThreadSafe: true);
    private readonly Lazy<ISkillCatalog> _skillCatalog = new(
        () => new SkillCatalog([AgentSkillLoader.LoadDefault().ToDescriptor()]), isThreadSafe: true);
    private readonly IInputResourceResolver? _inputResourceResolver;
    private readonly SemanticRegistry? _semanticRegistry;
    private readonly ITaskRunStore? _runStore;
    private readonly ITaskTelemetrySink? _telemetrySink;

    public DocumentAgentHarnessFactory(
        IInputResourceResolver? inputResourceResolver = null,
        SemanticRegistry? semanticRegistry = null,
        ITaskRunStore? runStore = null,
        ITaskTelemetrySink? telemetrySink = null)
    {
        _inputResourceResolver = inputResourceResolver;
        _semanticRegistry = semanticRegistry;
        _runStore = runStore;
        _telemetrySink = telemetrySink;
    }

    public AgentSkill Skill => _skill.Value;

    public ISkillCatalog Skills => _skillCatalog.Value;

    public DocumentAgentHarness Create(
        IDocumentExtractionTool tool,
        IAgentRunSink? sink = null,
        AgentHarnessOptions? options = null,
        IDocumentActionTool? actionTool = null) =>
        new(tool, sink: sink, options: options, actionTool: actionTool, skill: ResolveSkill(),
            inputResourceResolver: _inputResourceResolver, semanticRegistry: _semanticRegistry,
            runStore: _runStore, telemetrySink: _telemetrySink);

    public DocumentAgentHarness Create(
        IDocumentExtractionTool tool,
        IEnumerable<IDocumentActionTool> actionTools,
        IAgentRunSink? sink = null,
        AgentHarnessOptions? options = null) =>
        new(new AgentToolRegistry([tool], actionTools), sink: sink, options: options, skill: ResolveSkill(),
            inputResourceResolver: _inputResourceResolver, semanticRegistry: _semanticRegistry,
            runStore: _runStore, telemetrySink: _telemetrySink);

    public DocumentAgentHarness Create(
        IAgentToolRegistry registry,
        IAgentRunSink? sink = null,
        AgentHarnessOptions? options = null) =>
        new(registry, sink: sink, options: options, skill: ResolveSkill(),
            inputResourceResolver: _inputResourceResolver, semanticRegistry: _semanticRegistry,
            runStore: _runStore, telemetrySink: _telemetrySink);

    private AgentSkill ResolveSkill()
    {
        var skill = _skill.Value;
        var resolved = _skillCatalog.Value.Resolve(skill.Name, skill.Version);
        if (!resolved.IsResolved)
            throw new AgentSkillException($"Skill không resolve được qua catalog: {resolved.FailureReason}.");
        return skill;
    }
}
