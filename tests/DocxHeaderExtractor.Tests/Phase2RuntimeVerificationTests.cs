using DocxHeaderExtractor.AgentHarness;
using DocxHeaderExtractor.Application.Capabilities;
using DocxHeaderExtractor.Application.Runtime;
using DocxHeaderExtractor.Application.Semantics;
using DocxHeaderExtractor.Application.Skills;
using DocxHeaderExtractor.Application.Tasks;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Small deterministic proofs for the Phase-2 activation seams. Provider calls and Accuracy-99
/// artifacts are deliberately out of scope; these tests verify composition and fail-closed rules.
/// </summary>
public sealed class Phase2RuntimeVerificationTests
{
    [Fact]
    public void Intent_producer_is_provider_neutral_and_returns_a_validated_shape()
    {
        var proposal = new DocumentIntentProposalProducer().Propose(
            new DocumentAgentRequest("fixture.docx"));

        var validation = IntentValidator.Validate(proposal);

        Assert.Equal("extract-document-structure", proposal.Operation);
        Assert.True(validation.IsExecutable);
        Assert.Equal(IntentState.Executable, validation.State);
    }

    [Fact]
    public void Intent_producer_preserves_approval_signal_for_mutation()
    {
        var proposal = new DocumentIntentProposalProducer().Propose(
            new DocumentAgentRequest("fixture.docx") { WritebackTargetPath = "copy.docx" });

        var validation = IntentValidator.Validate(proposal);

        Assert.True(proposal.ExternalMutationRequested);
        Assert.Equal(IntentState.NeedsApproval, validation.State);
    }

    [Fact]
    public void User_prompt_drives_intent_and_generic_adapter_without_fixed_sentence_matching()
    {
        var first = new DocumentAgentRequest("fixture.docx")
        {
            UserPrompt = "Extract the document structure as a tree.",
        };
        var second = new DocumentAgentRequest("fixture.docx")
        {
            UserPrompt = "Extract the document structure to two levels and return a tree.",
        };

        var firstProposal = new DocumentIntentProposalProducer().Propose(first);
        var secondProposal = new DocumentIntentProposalProducer().Propose(second);
        var firstGeneric = GenericTaskRequestAdapter.FromDocumentRequest(first);
        var secondGeneric = GenericTaskRequestAdapter.FromDocumentRequest(second);
        var capability = new CapabilityDescriptor("inspect", "test", CapabilityRisk.Low, false, false);
        var firstPlan = TaskPlanCompiler.Compile(
            firstGeneric, IntentValidator.Validate(firstProposal).Intent!, capability, "input", "output");
        var secondPlan = TaskPlanCompiler.Compile(
            secondGeneric, IntentValidator.Validate(secondProposal).Intent!, capability, "input", "output");

        Assert.Equal(first.UserPrompt, firstGeneric.UserPrompt);
        Assert.Equal(second.UserPrompt, secondGeneric.UserPrompt);
        Assert.Equal("outline", firstProposal.OutputShape);
        Assert.Null(firstProposal.StructuralDepth);
        Assert.Equal(2, secondProposal.StructuralDepth);
        Assert.NotEqual(firstPlan.Semantic.PlanId, secondPlan.Semantic.PlanId);
        Assert.Equal(2, secondPlan.Semantic.Intent.StructuralDepth);
    }

    [Fact]
    public void Unsupported_and_incomplete_prompts_fail_closed_with_distinct_intent_states()
    {
        var producer = new DocumentIntentProposalProducer();

        var unsupported = IntentValidator.Validate(producer.Propose(
            new DocumentAgentRequest("fixture.docx") { UserPrompt = "Translate the document into English." }));
        var incomplete = IntentValidator.Validate(producer.Propose(
            new DocumentAgentRequest("fixture.docx") { UserPrompt = "Please inspect this file." }));
        var invalid = IntentValidator.Validate(producer.Propose(
            new DocumentAgentRequest("fixture.docx")
            {
                UserPrompt = "Extract the document structure to -1 levels and return a tree.",
            }));

        Assert.Equal(IntentState.Unsupported, unsupported.State);
        Assert.Equal(IntentState.NeedsClarification, incomplete.State);
        Assert.Equal(IntentState.Rejected, invalid.State);
    }

    [Fact]
    public void Framework_adapter_is_an_outer_delegate_over_the_harness_contract()
    {
        Assert.True(typeof(IMicrosoftAgentFrameworkAdapter).IsAssignableFrom(
            typeof(MicrosoftAgentFrameworkAdapter)));
        Assert.DoesNotContain(
            typeof(MicrosoftAgentFrameworkAdapter).GetInterfaces(),
            type => type.Namespace == "DocxHeaderExtractor.Core");
    }

    [Fact]
    public void Skill_runtime_resolves_only_active_versioned_descriptors()
    {
        var catalog = new SkillCatalog([
            new SkillDescriptor("extract", "1.0.0", "sha256:extract", SkillLifecycle.Active,
                ["outline"], ["input"], ["grounding"], true, 1),
            new SkillDescriptor("draft-extract", "2.0.0", "sha256:draft", SkillLifecycle.Draft,
                [], [], [], true, 0),
        ]);

        Assert.True(catalog.Resolve("outline", "1.0.0").IsResolved);
        Assert.False(catalog.Resolve("extract", "2.0.0").IsResolved);
    }

    [Fact]
    public void Semantic_registry_is_explicit_and_fails_closed_on_wrong_kind()
    {
        var registry = SemanticRegistryDefaults.Create();

        Assert.True(registry.Resolve("document-structure", SemanticDefinitionKind.Concept).IsResolved);
        Assert.False(registry.Resolve("document-structure", SemanticDefinitionKind.Schema).IsResolved);
        Assert.False(registry.Resolve("model-invented").IsResolved);
    }

    [Fact]
    public void Capability_catalog_has_exact_ambiguous_and_missing_outcomes()
    {
        static CapabilityDescriptor Capability(string name) =>
            new(name, "test", CapabilityRisk.Low, false, false);
        var catalog = new CapabilityCatalog([Capability("inspect"), Capability("inspect"), Capability("extract")]);

        Assert.True(catalog.Resolve("extract").IsResolved);
        Assert.Equal("capability-ambiguous", catalog.Resolve("inspect").FailureReason);
        Assert.Equal("capability-not-found", catalog.Resolve("missing").FailureReason);
    }

    [Fact]
    public void Generic_task_plan_keeps_all_resources_and_stable_idempotency_identity()
    {
        var resources = new[]
        {
            new InputResource("a", InputResourceKind.Document, "a.docx", "application/docx", "opaque:a"),
            new InputResource("b", InputResourceKind.Image, "b.png", "image/png", "opaque:b"),
        };
        var request = new AgentTaskRequest("inspect", resources, new AgentTaskPermissions(),
            OutputPreference: "outline", IdempotencyKey: "same-request");
        var intent = new ValidatedIntent("extract-document-structure", ["document-structure"], [],
            "document", null, "outline", [], false);
        var capability = new CapabilityDescriptor("inspect", "test", CapabilityRisk.Low, false, false);

        var first = TaskPlanCompiler.Compile(request, intent, capability, "input", "output");
        var second = TaskPlanCompiler.Compile(request, intent, capability, "input", "output");

        Assert.Equal(2, request.Resources.Count);
        Assert.Equal(first.Semantic.PlanId, second.Semantic.PlanId);
        Assert.False(first.Execution.ExternalTransferRequired);
    }

    [Fact]
    public void Policy_denies_external_transfer_without_consent_and_defers_mutation()
    {
        var plan = new ExecutionPlan("plan", [], 1, 1) { ExternalTransferRequired = true };

        Assert.Equal(PolicyDecisionKind.Denied,
            PolicyEvaluator.Evaluate(plan, false, false, true).Kind);
        Assert.Equal(PolicyDecisionKind.DeferredToHumanReview,
            PolicyEvaluator.Evaluate(plan, true, true, true).Kind);
    }

    [Fact]
    public void Runtime_storage_contract_is_sanitized_and_lifecycle_typed()
    {
        var key = new RunStorageKey("run/opaque");
        key.Validate();
        var run = new PersistedTaskRun(key, "plan", PersistedRunLifecycle.Completed,
            TaskRunStatus.Completed, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            new TaskProvenance("sha256:source", "inspect", "local", "model", false,
                "ValidatedStructure"));

        Assert.Equal(PersistedRunLifecycle.Completed, run.Lifecycle);
        var redacted = new SecretRedactor().Redact("api-key=secret");
        Assert.DoesNotContain("secret", redacted);
        Assert.Contains("[REDACTED]", redacted);
    }
}
