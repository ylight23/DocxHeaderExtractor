using DocxHeaderExtractor.AgentHarness;
using DocxHeaderExtractor.Application.Tasks;

namespace DocxHeaderExtractor.Tests;

public sealed class AutoHarnessArchitectureContractTests
{
    [Fact]
    public void Intent_is_validated_before_it_becomes_a_task_plan()
    {
        var proposal = new IntentProposal(
            "extract-document-structure", ["document-structure"], [], "document", null,
            "outline", [], false);
        var validation = IntentValidator.Validate(proposal);

        Assert.True(validation.IsExecutable);
        Assert.Equal("extract-document-structure", validation.Intent!.Operation);
        Assert.True(validation.Intent is ValidatedIntent);
    }

    [Fact]
    public void Unsupported_intent_fails_closed()
    {
        var proposal = new IntentProposal(
            "write-arbitrary-file", [], [], "document", null, "outline", [], true);
        var result = IntentValidator.Validate(proposal);

        Assert.Equal(IntentState.Unsupported, result.State);
        Assert.Contains("operation-unsupported", result.Reasons);
    }

    [Fact]
    public void Incomplete_intent_requests_clarification_without_creating_a_plan()
    {
        var result = IntentValidator.Validate(new IntentProposal(
            "", [], [], "", null, "", [], false));

        Assert.Equal(IntentState.NeedsClarification, result.State);
        Assert.Null(result.Intent);
    }

    [Fact]
    public void Policy_does_not_silently_approve_external_transfer_or_mutation()
    {
        var plan = new ExecutionPlan("plan", [new ExecutionStep(
            "step", "remote", [], "input", "output")], 1, 1);

        var denied = PolicyEvaluator.Evaluate(plan, externalConsentGranted: false,
            mutationRequested: false, humanReviewBeforeMutation: true);
        var deferred = PolicyEvaluator.Evaluate(plan, externalConsentGranted: true,
            mutationRequested: true, humanReviewBeforeMutation: true);

        Assert.Equal(PolicyDecisionKind.Denied, denied.Kind);
        Assert.Equal(PolicyDecisionKind.DeferredToHumanReview, deferred.Kind);
    }

    [Fact]
    public void Generic_task_request_supports_multiple_opaque_resources_and_budget()
    {
        var request = new AgentTaskRequest(
            "inspect these resources",
            [
                new InputResource("doc", InputResourceKind.Document, "a.docx", "application/docx", "opaque:a"),
                new InputResource("image", InputResourceKind.Image, "page.png", "image/png", "opaque:b"),
            ],
            new AgentTaskPermissions(),
            OutputPreference: "grouped",
            Budget: new TaskBudget(MaxSteps: 8, MaxInputBytes: 1_000_000));

        Assert.Equal(2, request.Resources.Count);
        Assert.Equal("opaque:a", request.Resources[0].Locator);
        Assert.Equal(8, request.Budget!.MaxSteps);
    }

    [Fact]
    public void Task_plan_compiler_produces_stable_identity_and_applies_budget()
    {
        var request = new AgentTaskRequest(
            "inspect",
            [new InputResource("doc-1", InputResourceKind.Document, "a.docx", "application/docx", "opaque:a")],
            new AgentTaskPermissions(),
            Budget: new TaskBudget(MaxSteps: 8, MaxProviderCalls: 2));
        var intent = new ValidatedIntent(
            "extract-document-structure", ["document-structure"], [], "document", null,
            "outline", [], false);
        var capability = new DocxHeaderExtractor.Application.Capabilities.CapabilityDescriptor(
            "extract", "test", DocxHeaderExtractor.Application.Capabilities.CapabilityRisk.Low,
            SendsDataExternally: false, MutatesExternalState: false);

        var first = TaskPlanCompiler.Compile(request, intent, capability, "input", "output");
        var second = TaskPlanCompiler.Compile(request, intent, capability, "input", "output");

        Assert.Equal(first.Semantic.PlanId, second.Semantic.PlanId);
        Assert.Equal(8, first.Execution.MaxSteps);
        Assert.Equal(0, first.Execution.MaxExternalCalls);
        Assert.Equal("extract", first.Execution.Steps[0].CapabilityId);
    }
}
