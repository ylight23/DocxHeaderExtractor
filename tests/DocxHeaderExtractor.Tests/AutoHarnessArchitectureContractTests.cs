using DocxHeaderExtractor.AgentHarness;
using DocxHeaderExtractor.Application.Tasks;
using DocxHeaderExtractor.Application.Runtime;
using DocxHeaderExtractor.Infrastructure.Runtime;
using DocxHeaderExtractor.Infrastructure.Sources;
using System.Text.Json;

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
        Assert.False(first.Execution.ExternalTransferRequired);
        Assert.Equal("extract", first.Execution.Steps[0].CapabilityId);
        Assert.Equal(TaskRunStatus.Failed, new GenericTaskResult<string>(
            Guid.NewGuid(), first.Semantic.PlanId, "not-a-status",
            new PromptDrivenProjection<string>("value", "test", []), [],
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow).StatusCode);
        Assert.True(first.Execution.CancellationSupported);
        Assert.Equal(1, first.Execution.Retry.MaxAttempts);
    }

    [Fact]
    public void Remote_capability_with_zero_provider_budget_is_denied()
    {
        var request = new AgentTaskRequest(
            "inspect",
            [new InputResource("doc-1", InputResourceKind.Document, "a.docx", "application/docx", "opaque:a")],
            new AgentTaskPermissions(AllowExternalDataTransfer: true),
            Budget: new TaskBudget(MaxProviderCalls: 0));
        var intent = new ValidatedIntent(
            "extract-document-structure", ["document-structure"], [], "document", null,
            "outline", [], false);
        var capability = new DocxHeaderExtractor.Application.Capabilities.CapabilityDescriptor(
            "remote", "test", DocxHeaderExtractor.Application.Capabilities.CapabilityRisk.Medium,
            SendsDataExternally: true, MutatesExternalState: false);

        var plan = TaskPlanCompiler.Compile(request, intent, capability, "input", "output");
        var decision = PolicyEvaluator.Evaluate(plan.Execution, true, false, false);

        Assert.Equal(PolicyDecisionKind.Denied, decision.Kind);
        Assert.Equal("external-call-budget-exhausted", decision.Code);
    }

    [Fact]
    public void Generic_capability_catalog_resolves_exactly_and_fails_closed_on_ambiguity()
    {
        static DocxHeaderExtractor.Application.Capabilities.CapabilityDescriptor Capability(string name) =>
            new(name, "test capability", DocxHeaderExtractor.Application.Capabilities.CapabilityRisk.Low,
                SendsDataExternally: false, MutatesExternalState: false);

        var catalog = new DocxHeaderExtractor.Application.Capabilities.CapabilityCatalog(
            [Capability("extract"), Capability("extract"), Capability("inspect")]);

        Assert.Equal("capability-ambiguous", catalog.Resolve("extract").FailureReason);
        Assert.True(catalog.Resolve("inspect").IsResolved);
        Assert.Equal("capability-not-found", catalog.Resolve("missing").FailureReason);
    }

    [Fact]
    public void Semantic_registry_resolves_active_aliases_and_fails_closed()
    {
        var registry = new DocxHeaderExtractor.Application.Semantics.SemanticRegistry();
        registry.Register(new DocxHeaderExtractor.Application.Semantics.SemanticDefinition(
            "outline.heading", 1,
            DocxHeaderExtractor.Application.Semantics.SemanticDefinitionKind.Schema,
            DocxHeaderExtractor.Application.Semantics.SemanticDefinitionLifecycle.Active,
            ["heading-v1"]));
        registry.Register(new DocxHeaderExtractor.Application.Semantics.SemanticDefinition(
            "draft.heading", 1,
            DocxHeaderExtractor.Application.Semantics.SemanticDefinitionKind.Schema,
            DocxHeaderExtractor.Application.Semantics.SemanticDefinitionLifecycle.Draft,
            []));

        var resolved = registry.Resolve("heading-v1",
            DocxHeaderExtractor.Application.Semantics.SemanticDefinitionKind.Schema);
        var draft = registry.Resolve("draft.heading");
        var missing = registry.Resolve("heading-v2");

        Assert.True(resolved.IsResolved);
        Assert.Equal("outline.heading", resolved.Definition!.Key);
        Assert.False(draft.IsResolved);
        Assert.False(missing.IsResolved);
    }

    [Fact]
    public void Composition_defaults_register_only_trusted_generic_semantics()
    {
        var registry = DocxHeaderExtractor.Application.Semantics.SemanticRegistryDefaults.Create();

        Assert.True(registry.Resolve("document-structure",
            DocxHeaderExtractor.Application.Semantics.SemanticDefinitionKind.Concept).IsResolved);
        Assert.True(registry.Resolve("outline",
            DocxHeaderExtractor.Application.Semantics.SemanticDefinitionKind.Schema).IsResolved);
        Assert.False(registry.Resolve("document-structure",
            DocxHeaderExtractor.Application.Semantics.SemanticDefinitionKind.Schema).IsResolved);
    }

    [Fact]
    public void Runtime_contracts_redact_secrets_and_validate_storage_identity()
    {
        var key = new DocxHeaderExtractor.Application.Runtime.RunStorageKey("run-1");
        key.Validate();

        var redacted = new DocxHeaderExtractor.Application.Runtime.SecretRedactor().Redact(
            "Authorization: Bearer abc.def api_key='sk-test' token=xyz");

        Assert.DoesNotContain("abc.def", redacted);
        Assert.DoesNotContain("sk-test", redacted);
        Assert.DoesNotContain("xyz", redacted);
        Assert.Contains("[REDACTED]", redacted);
    }

    [Fact]
    public async Task Infrastructure_runtime_adapters_round_trip_runs_and_redact_telemetry()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dhx-phase1-runtime-" + Guid.NewGuid().ToString("N"));
        var telemetryPath = Path.Combine(directory, "telemetry.jsonl");
        try
        {
            var run = new PersistedTaskRun(
                new RunStorageKey("run/with\u005cpath"), "plan-1", PersistedRunLifecycle.Completed,
                TaskRunStatus.Completed, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                new TaskProvenance("sha256:source", "extract", "local", "model", false, "ValidatedStructure"));

            await using (var store = new JsonFileTaskRunStore(directory))
            {
                await store.SaveAsync(run);
                var loaded = await store.LoadAsync(run.Key);
                Assert.Equal(run, loaded);
            }

            await using (var sink = new JsonLinesTaskTelemetrySink(telemetryPath))
            {
                await sink.RecordAsync(new TaskTelemetryEvent(
                    run.Key.RunId, "provider", "completed", DateTimeOffset.UtcNow,
                    new Dictionary<string, string> { ["message"] = "api_key=secret-value" }));
            }

            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(telemetryPath));
            Assert.Equal("api_key=[REDACTED]", json.RootElement.GetProperty("dimensions")
                .GetProperty("message").GetString());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task File_resource_resolver_requires_an_allowlisted_source_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "dhx-phase1-source-" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(Path.GetTempPath(), "dhx-phase1-outside-" + Guid.NewGuid().ToString("N") + ".txt");
        var inside = Path.Combine(root, "input.txt");
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(inside, "source");
            await File.WriteAllTextAsync(outside, "outside");
            var resolver = new FileInputResourceResolver([root]);
            var resource = new InputResource("r1", InputResourceKind.Text, "input.txt", "text/plain", inside);

            var resolved = await resolver.ResolveAsync(resource);
            await using (resolved.Content)
            {
                using var reader = new StreamReader(resolved.Content);
                Assert.Equal("source", await reader.ReadToEndAsync());
            }

            await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
                await resolver.ResolveAsync(resource with { Locator = outside }).AsTask());
        }
        finally
        {
            if (File.Exists(outside)) File.Delete(outside);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Typed_retry_retries_only_transient_provider_failures_when_enabled()
    {
        var attempts = 0;
        var result = await TaskRetryExecutor.ExecuteAsync(
            _ =>
            {
                attempts++;
                if (attempts < 3)
                    throw new ProviderCallException("503", "temporary", isTransient: true);
                return Task.FromResult("ok");
            },
            new RetryPolicy(MaxAttempts: 3, RetryProviderFaults: true));

        Assert.Equal("ok", result);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task Typed_retry_does_not_retry_non_transient_or_untyped_failures()
    {
        var typedAttempts = 0;
        await Assert.ThrowsAsync<ProviderCallException>(() => TaskRetryExecutor.ExecuteAsync<string>(
            _ =>
            {
                typedAttempts++;
                throw new ProviderCallException("400", "permanent", isTransient: false);
            },
            new RetryPolicy(MaxAttempts: 4, RetryProviderFaults: true)));
        Assert.Equal(1, typedAttempts);

        var untypedAttempts = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => TaskRetryExecutor.ExecuteAsync<string>(
            _ =>
            {
                untypedAttempts++;
                throw new InvalidOperationException("not a provider failure");
            },
            new RetryPolicy(MaxAttempts: 4, RetryProviderFaults: true)));
        Assert.Equal(1, untypedAttempts);
    }
}
