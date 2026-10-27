using DocxHeaderExtractor.Agents.Budget;
using DocxHeaderExtractor.Contracts.Agents;
using DocxHeaderExtractor.HeadingAgent.Formats;

namespace DocxHeaderExtractor.HeadingAgent.Runtime;

internal static class ResourceAudit
{
    internal static ResourceStageObservation? Observe(AgentBudget budget) => budget.Calls == 0 ? null : new(budget.Policy.ProfileId, budget.ByteCap,
        budget.ActualUsd, budget.ReservedUsd, budget.Attempts.Select(a => new ResourceAttemptObservation(
            a.Id.ToString("N"), a.RequestSha256, a.Resources.WireBytes, a.Resources.InputTokenEstimate,
            a.Resources.Estimator, a.Resources.CompletionTokens, a.Usage?.PromptTokens, a.Usage?.CompletionTokens,
            a.Usage?.ReasoningTokens, a.ReserveUsd, a.CostUsd, a.Status)
        { Transport = a.Transport is { } t ? new(t.UnitId, t.Turn, t.RetryIndex, t.HttpStatus, t.Outcome,
            t.RequestSeconds, t.WaitSeconds, t.RetryAfterSeconds, t.RetryDelaySeconds, t.ProviderErrorCode) : null }).ToArray());

    internal static SelectionPlanObservation? Plan(IAgentSource source) => source is AgentSource { PlanId: { } id } pdf
        ? new(id, "ordered-page-budget-v1", pdf.Plan.Select(p => new SelectionScopeObservation(p.Segment,
            source.Lines[p.Start].Alias, source.Lines[p.End - 1].Alias, p.End - p.Start, p.Kind,
            p.FirstRequestBytes, p.RequestSha256)).ToArray()) : null;
}
