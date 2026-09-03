namespace DocxHeaderExtractor.AgentHarness;

/// <summary>
/// Stable application contracts between a host prompt and a capability execution. These records
/// contain intent and policy facts only; they never contain model-generated authority or a second
/// document representation.
/// </summary>
public sealed record IntentProposal(
    string Operation,
    string InputPath,
    bool ExternalDataTransferRequested,
    bool ExternalMutationRequested)
{
    public static IntentProposal From(DocumentAgentRequest request) =>
        new(
            "extract-document-structure",
            request.InputPath,
            request.AllowExternalDataTransfer,
            request.WantsAction);
}

public sealed record ValidatedIntent(
    string Operation,
    string InputPath,
    bool ExternalDataTransferRequested,
    bool ExternalMutationRequested);

public sealed record SemanticTaskPlan(
    string TaskName,
    string CapabilityName,
    ValidatedIntent Intent);

public sealed record ExecutionPlan(
    string CapabilityName,
    AgentToolDescriptor Capability,
    bool RequiresConsent,
    bool RequiresHumanReviewBeforeMutation);

public enum PolicyDecisionKind
{
    Allowed,
    DeferredToHumanReview,
    Denied,
}

public sealed record PolicyDecision(
    PolicyDecisionKind Kind,
    string Code,
    string Message);

/// <summary>
/// Projection of a capability result after deterministic source/authority validation. It does
/// not create or replace authority.
/// </summary>
public sealed record PromptDrivenProjection<T>(
    T Value,
    string Authority,
    IReadOnlyList<string> ValidationStages);

public sealed record GenericTaskResult<T>(
    Guid TaskId,
    string Status,
    PromptDrivenProjection<T> Projection,
    IReadOnlyList<string> CompletedStages,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt)
{
    public T Value => Projection.Value;
}

public static class IntentValidator
{
    public static ValidatedIntent Validate(IntentProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        if (!string.Equals(proposal.Operation, "extract-document-structure", StringComparison.Ordinal))
            throw new InvalidOperationException($"Unsupported intent operation: {proposal.Operation}.");
        if (string.IsNullOrWhiteSpace(proposal.InputPath))
            throw new ArgumentException("Intent thiếu đường dẫn tài liệu.", nameof(proposal));

        return new ValidatedIntent(
            proposal.Operation,
            proposal.InputPath,
            proposal.ExternalDataTransferRequested,
            proposal.ExternalMutationRequested);
    }
}

public static class SemanticTaskPlanner
{
    public static SemanticTaskPlan Create(ValidatedIntent intent, AgentToolSelection selection) =>
        new("document-extraction", selection.Extraction.Descriptor.Name, intent);
}

public static class ExecutionPlanner
{
    public static ExecutionPlan Create(SemanticTaskPlan plan, AgentToolSelection selection,
        AgentSkill skill) =>
        new(
            plan.CapabilityName,
            selection.Extraction.Descriptor,
            selection.Extraction.Descriptor.SendsDataExternally,
            selection.Extraction.Descriptor.MutatesExternalState && skill.Requires.HumanReviewBeforeWriteback);
}

public static class PolicyEvaluator
{
    public static PolicyDecision Evaluate(ExecutionPlan plan, DocumentAgentRequest request)
    {
        if (plan.RequiresConsent && !request.AllowExternalDataTransfer)
            return new(PolicyDecisionKind.Denied, "external-consent-required",
                "Capability cần consent trước khi gửi dữ liệu ra ngoài.");
        if (plan.RequiresHumanReviewBeforeMutation && request.WantsAction)
            return new(PolicyDecisionKind.DeferredToHumanReview, "human-review-before-mutation",
                "Tác động ghi được giữ lại cho human-review gate.");
        return new(PolicyDecisionKind.Allowed, "policy-allowed", "Task được phép tiếp tục.");
    }
}
