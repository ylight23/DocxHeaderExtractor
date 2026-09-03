namespace DocxHeaderExtractor.Application.Tasks;

/// <summary>
/// Provider- and tool-independent application task contracts. They carry intent and policy facts,
/// not authority instances or model-generated source text.
/// </summary>
public sealed record IntentProposal(
    string Operation,
    IReadOnlyList<string> Concepts,
    IReadOnlyList<string> Predicates,
    string Granularity,
    int? StructuralDepth,
    string OutputShape,
    IReadOnlyList<string> Constraints,
    bool ExternalMutationRequested);

public sealed record ValidatedIntent(
    string Operation,
    IReadOnlyList<string> Concepts,
    IReadOnlyList<string> Predicates,
    string Granularity,
    int? StructuralDepth,
    string OutputShape,
    IReadOnlyList<string> Constraints,
    bool ExternalMutationRequested);

public enum IntentState
{
    Executable,
    NeedsClarification,
    NeedsApproval,
    Unsupported,
    Rejected,
}

public sealed record IntentValidationResult(
    IntentState State,
    ValidatedIntent? Intent,
    IReadOnlyList<string> Reasons)
{
    public bool IsExecutable => State == IntentState.Executable && Intent is not null;
}

public sealed record SemanticTaskPlan(
    string PlanId,
    int Version,
    string TaskName,
    ValidatedIntent Intent);

public sealed record ExecutionPlan(
    string PlanId,
    IReadOnlyList<ExecutionStep> Steps,
    int MaxSteps,
    int? MaxExternalCalls);

public sealed record ExecutionStep(
    string StepId,
    string CapabilityId,
    IReadOnlyList<string> DependsOn,
    string InputContract,
    string OutputContract);

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

public sealed record PromptDrivenProjection<T>(
    T Value,
    string Authority,
    IReadOnlyList<string> ValidationStages);

public sealed record GenericTaskResult<T>(
    Guid TaskId,
    string PlanId,
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
    public static IntentValidationResult Validate(IntentProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var reasons = new List<string>();
        if (string.IsNullOrWhiteSpace(proposal.Operation)) reasons.Add("operation-missing");
        if (string.IsNullOrWhiteSpace(proposal.Granularity)) reasons.Add("granularity-missing");
        if (string.IsNullOrWhiteSpace(proposal.OutputShape)) reasons.Add("output-shape-missing");
        if (proposal.StructuralDepth is < 0) reasons.Add("structural-depth-invalid");
        if (reasons.Count > 0)
            return new(IntentState.Rejected, null, reasons);

        if (!string.Equals(proposal.Operation, "extract-document-structure", StringComparison.Ordinal))
            return new(IntentState.Unsupported, null, ["operation-unsupported"]);

        return new(
            proposal.ExternalMutationRequested ? IntentState.NeedsApproval : IntentState.Executable,
            new ValidatedIntent(
                proposal.Operation,
                proposal.Concepts,
                proposal.Predicates,
                proposal.Granularity,
                proposal.StructuralDepth,
                proposal.OutputShape,
                proposal.Constraints,
                proposal.ExternalMutationRequested),
            []);
    }
}

public static class PolicyEvaluator
{
    public static PolicyDecision Evaluate(
        ExecutionPlan plan,
        bool externalConsentGranted,
        bool mutationRequested,
        bool humanReviewBeforeMutation)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.MaxExternalCalls is > 0 && !externalConsentGranted)
            return new(PolicyDecisionKind.Denied, "external-consent-required",
                "Capability cần consent trước khi gửi dữ liệu ra ngoài.");
        if (mutationRequested && humanReviewBeforeMutation)
            return new(PolicyDecisionKind.DeferredToHumanReview, "human-review-before-mutation",
                "Tác động ghi được giữ lại cho human-review gate.");
        return new(PolicyDecisionKind.Allowed, "policy-allowed", "Task được phép tiếp tục.");
    }
}
