using DocxHeaderExtractor.AgentHarness;

namespace DocxHeaderExtractor.Tests;

public sealed class AutoHarnessArchitectureContractTests
{
    [Fact]
    public void Intent_is_validated_before_it_becomes_a_task_plan()
    {
        var request = new DocumentAgentRequest("sample.docx", AllowExternalDataTransfer: true);

        var proposal = IntentProposal.From(request);
        var intent = IntentValidator.Validate(proposal);

        Assert.Equal("extract-document-structure", intent.Operation);
        Assert.Equal(request.InputPath, intent.InputPath);
        Assert.True(intent.ExternalDataTransferRequested);
        Assert.True(intent is ValidatedIntent);
    }

    [Fact]
    public void Unsupported_intent_fails_closed()
    {
        var proposal = new IntentProposal("write-arbitrary-file", "sample.docx", false, true);

        var error = Assert.Throws<InvalidOperationException>(() => IntentValidator.Validate(proposal));

        Assert.Contains("Unsupported intent operation", error.Message);
    }

    [Fact]
    public void Policy_does_not_silently_approve_external_transfer_or_mutation()
    {
        var descriptor = new AgentToolDescriptor(
            "remote", "remote capability", AgentToolRisk.Medium,
            SendsDataExternally: true, MutatesExternalState: true);
        var plan = new ExecutionPlan("remote", descriptor, true, true);

        var denied = PolicyEvaluator.Evaluate(
            plan, new DocumentAgentRequest("sample.docx"));
        var deferred = PolicyEvaluator.Evaluate(
            plan, new DocumentAgentRequest("sample.docx", AllowExternalDataTransfer: true)
            {
                WritebackTargetPath = "copy.docx",
            });

        Assert.Equal(PolicyDecisionKind.Denied, denied.Kind);
        Assert.Equal(PolicyDecisionKind.DeferredToHumanReview, deferred.Kind);
    }
}
