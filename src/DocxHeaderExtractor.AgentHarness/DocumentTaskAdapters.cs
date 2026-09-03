using DocxHeaderExtractor.Application.Tasks;
using System.Security.Cryptography;
using System.Text;

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

    public static SemanticTaskPlan CreateSemanticPlan(
        ValidatedIntent intent,
        AgentToolSelection selection) =>
        new(
            "plan-" + Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(intent.Operation + "\n" + selection.Extraction.Descriptor.Name)))
                .ToLowerInvariant()[..16],
            1,
            "document-extraction",
            intent);

    public static ExecutionPlan CreateExecutionPlan(
        SemanticTaskPlan plan,
        AgentToolSelection selection,
        AgentSkill skill) =>
        new(
            plan.PlanId,
            [new ExecutionStep(
                "extract-structure",
                selection.Extraction.Descriptor.Name,
                [],
                "DocumentAgentRequest",
                "DocumentOutline")],
            1,
            selection.Extraction.Descriptor.SendsDataExternally ? 1 : 0);

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
