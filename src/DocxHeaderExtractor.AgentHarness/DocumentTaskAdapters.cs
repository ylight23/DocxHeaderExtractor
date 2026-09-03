using DocxHeaderExtractor.Application.Tasks;

namespace DocxHeaderExtractor.AgentHarness;

/// <summary>
/// DOCX capability adapter. DocumentAgentRequest is deliberately kept outside Application; this
/// adapter translates the legacy host request into generic task contracts.
/// </summary>
internal static class DocumentTaskAdapters
{
    public static IntentProposal Propose(DocumentAgentRequest request) =>
        new(
            "extract-document-structure",
            ["document-structure"],
            [],
            "document",
            null,
            "outline",
            [],
            request.WantsAction);

    public static CompiledTaskPlan Compile(
        AgentTaskRequest request,
        ValidatedIntent intent,
        AgentToolSelection selection) =>
        TaskPlanCompiler.Compile(
            request,
            intent,
            selection.Extraction.Descriptor,
            "DocumentAgentRequest",
            "DocumentOutline");

    public static PolicyDecision EvaluatePolicy(
        ExecutionPlan plan,
        AgentToolSelection selection,
        DocumentAgentRequest request,
        AgentSkill skill) =>
        PolicyEvaluator.Evaluate(
            plan,
            request.AllowExternalDataTransfer,
            request.WantsAction,
            skill.Requires.HumanReviewBeforeWriteback);
}
