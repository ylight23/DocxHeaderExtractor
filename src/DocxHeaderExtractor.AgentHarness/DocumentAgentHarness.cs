using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.AgentHarness;

/// <summary>
/// Harness điều phối một workflow agent có giới hạn: guardrail → tool → human-review gate.
/// Model không được tự bỏ qua guardrail, tự ghi nhãn vàng hoặc tự thay đổi code/prompt.
/// </summary>
public sealed class DocumentAgentHarness
{
    private readonly IDocumentExtractionTool _tool;
    private readonly IReadOnlyList<IDocumentAgentGuardrail> _guardrails;
    private readonly IReadOnlyList<IDocumentAgentValidator> _validators;
    private readonly IAgentRunSink _sink;
    private readonly AgentHarnessOptions _options;

    public DocumentAgentHarness(
        IDocumentExtractionTool tool,
        IEnumerable<IDocumentAgentGuardrail>? guardrails = null,
        IEnumerable<IDocumentAgentValidator>? validators = null,
        IAgentRunSink? sink = null,
        AgentHarnessOptions? options = null)
    {
        _tool = tool ?? throw new ArgumentNullException(nameof(tool));
        _guardrails = (guardrails ?? DefaultGuardrails()).ToArray();
        _validators = (validators ?? DefaultValidators()).ToArray();
        _sink = sink ?? NullAgentRunSink.Instance;
        _options = options ?? new AgentHarnessOptions();
        _options.Validate();
    }

    public async Task<DocumentAgentRunResult> RunAsync(
        DocumentAgentRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var runId = Guid.NewGuid();
        var trace = new List<AgentRunEvent>();
        var sequence = 0;
        var steps = 0;

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
                $"Bắt đầu run với tool {_tool.Descriptor.Name}.");

            var guardrailContext = new DocumentAgentGuardrailContext(request, _tool.Descriptor);
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

            TakeStep($"tool.{_tool.Descriptor.Name}");
            await EmitAsync($"tool.{_tool.Descriptor.Name}", AgentRunEventKind.Started,
                "Bắt đầu phân tích tài liệu.");
            var outline = await _tool.ExecuteAsync(request, ct);
            await EmitAsync($"tool.{_tool.Descriptor.Name}", AgentRunEventKind.Completed,
                $"Tool trả {outline.Headings.Count} heading từ {outline.ParagraphCount} đoạn.");

            foreach (var validator in _validators)
            {
                TakeStep($"validator.{validator.Name}");
                var validation = await validator.ValidateAsync(outline, ct);
                var stage = $"validator.{validator.Name}";
                if (!validation.IsValid)
                {
                    await EmitAsync(stage, AgentRunEventKind.Blocked,
                        $"Chặn output vì {validation.Issues.Count} bất biến nguồn bị vi phạm.");
                    throw new AgentOutputValidationException(runId, validation.Issues, trace.ToArray());
                }
                await EmitAsync(stage, AgentRunEventKind.Passed,
                    "Index, cấp, thứ tự và source span đều hợp lệ.");
            }

            TakeStep("gate.human_review");
            var reviewCount = outline.Headings.Count(h =>
                h.DecisionStatus == HeadingDecisionStatus.RequiresReview || h.Disputed);
            var outcome = reviewCount > 0
                ? AgentRunOutcome.NeedsHumanReview
                : AgentRunOutcome.Completed;
            await EmitAsync("gate.human_review", AgentRunEventKind.Completed,
                reviewCount > 0
                    ? $"Chuyển {reviewCount} heading sang người duyệt."
                    : "Không còn heading bắt buộc người duyệt.");
            await EmitAsync("run", AgentRunEventKind.Completed, $"Kết thúc run: {outcome}.");

            return new DocumentAgentRunResult(runId, outcome, outline, steps, trace.ToArray());
        }
        catch (OperationCanceledException)
        {
            await EmitWithoutCancellationAsync("run", AgentRunEventKind.Cancelled, "Run đã bị hủy.", trace, runId, ++sequence);
            throw;
        }
        catch (AgentRunBlockedException)
        {
            throw;
        }
        catch (AgentOutputValidationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            await EmitWithoutCancellationAsync("run", AgentRunEventKind.Failed,
                $"Run thất bại: {ex.GetType().Name}.", trace, runId, ++sequence);
            throw;
        }
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

    private static IEnumerable<IDocumentAgentGuardrail> DefaultGuardrails()
    {
        yield return new InputDocumentGuardrail();
        yield return new ExternalDataTransferGuardrail();
    }

    private static IEnumerable<IDocumentAgentValidator> DefaultValidators()
    {
        yield return new OutlineGroundingValidator();
    }
}

public sealed class DocumentAgentHarnessFactory
{
    public DocumentAgentHarness Create(
        IDocumentExtractionTool tool,
        IAgentRunSink? sink = null,
        AgentHarnessOptions? options = null) =>
        new(tool, sink: sink, options: options);
}
