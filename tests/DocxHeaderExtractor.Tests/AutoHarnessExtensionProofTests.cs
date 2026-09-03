using DocxHeaderExtractor.Application.Capabilities;
using DocxHeaderExtractor.Application.Feedback;
using DocxHeaderExtractor.Application.Semantics;
using DocxHeaderExtractor.Application.Tasks;
using DocxHeaderExtractor.Application.Skills;
using DocxHeaderExtractor.Core.Llm;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Pipeline;
using DocxHeaderExtractor.Core.Eval;
using DocxHeaderExtractor.Infrastructure.Learning;
using DocxHeaderExtractor.Infrastructure.Feedback;
using DocxHeaderExtractor.Infrastructure.Sources;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Extension proof: new capabilities, semantics, sources, tasks and provider adapters can join
/// the existing seams without adding a second authority implementation.
/// </summary>
public sealed class AutoHarnessExtensionProofTests
{
    [Fact]
    public void Skill_catalog_is_versioned_and_fail_closed()
    {
        var catalog = new SkillCatalog([
            new SkillDescriptor("outline", "1.0.0", "sha256:test", SkillLifecycle.Active,
                ["document-outline"], ["input"], ["grounding"], true, 1),
            new SkillDescriptor("draft", "1.0.0", "sha256:draft", SkillLifecycle.Draft,
                [], [], [], true, 0),
        ]);

        Assert.True(catalog.Resolve("document-outline", "1.0.0").IsResolved);
        Assert.False(catalog.Resolve("draft").IsResolved);
        Assert.False(catalog.Resolve("outline", "2.0.0").IsResolved);
    }

    [Fact]
    public void Trusted_semantic_extensions_are_registered_without_model_inference()
    {
        var registry = SemanticRegistryDefaults.Create([
            new SemanticDefinition("custom.schema", 1, SemanticDefinitionKind.Schema,
                SemanticDefinitionLifecycle.Active, ["custom-output"]),
        ]);

        Assert.True(registry.Resolve("custom-output", SemanticDefinitionKind.Schema).IsResolved);
        Assert.False(registry.Resolve("model-invented").IsResolved);
    }

    [Fact]
    public async Task Human_feedback_port_preserves_existing_append_only_storage_contract()
    {
        var path = Path.Combine(Path.GetTempPath(), "dhx-feedback-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            var memory = new CorrectionMemory(path);
            IHumanFeedbackStore feedback = new CorrectionMemoryFeedbackStore(memory);
            var emptyBundle = new ReviewBundle
            {
                SourceFile = "fixture.docx",
                Rows = [new ReviewRow { StableId = "p1", Index = 1, Text = "body", PredictedLevel = 0 }],
            };

            var saved = await feedback.SaveChangedAsync(new HumanFeedbackSubmission(emptyBundle.ToJson()));

            Assert.Equal(0, saved);
            Assert.Equal(0, feedback.Count);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            var decisions = Path.ChangeExtension(path, ".decisions.jsonl");
            if (File.Exists(decisions)) File.Delete(decisions);
        }
    }

    [Fact]
    public async Task Extension_points_compose_without_provider_calls()
    {
        var root = Path.Combine(Path.GetTempPath(), "dhx-extension-" + Guid.NewGuid().ToString("N"));
        var input = Path.Combine(root, "input.txt");
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(input, "source");

            var source = new FileInputResourceResolver([root]);
            var resource = new InputResource("source-1", InputResourceKind.Text, "input.txt",
                "text/plain", input);
            var resolved = await source.ResolveAsync(resource);
            await using (resolved.Content)
            {
                Assert.Equal("source-1", resolved.Resource.ResourceId);
            }

            var semantics = new SemanticRegistry();
            semantics.Register(new SemanticDefinition(
                "custom.document", 1, SemanticDefinitionKind.Concept,
                SemanticDefinitionLifecycle.Active, ["custom-doc"]));
            Assert.True(semantics.Resolve("custom-doc", SemanticDefinitionKind.Concept).IsResolved);

            var capability = new CapabilityDescriptor(
                "custom.inspect", "custom source inspection", CapabilityRisk.Low,
                SendsDataExternally: false, MutatesExternalState: false);
            var catalog = new CapabilityCatalog([capability]);
            Assert.Same(capability, catalog.Resolve("custom.inspect").Capability);

            var request = new AgentTaskRequest(
                "inspect", [resource], new AgentTaskPermissions(),
                OutputPreference: "outline");
            var intent = new ValidatedIntent(
                "extract-document-structure", ["custom-doc"], [], "document", null,
                "outline", [], false);
            var plan = TaskPlanCompiler.Compile(request, intent, capability, "text", "outline");
            Assert.Equal("custom.inspect", plan.Execution.Steps.Single().CapabilityId);

            using var provider = new FakeHeaderClassifier();
            Assert.Equal("test-provider", provider.ModelName);
            Assert.Equal(0, provider.Calls);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FakeHeaderClassifier : IHeaderClassifier
    {
        public string ModelName => "test-provider";
        public int ContextSize => 1_024;
        public string RuntimeDescription => "test";
        public int SharedPrefixTokens => 0;
        public int Calls { get; private set; }

        public Task<ChunkResult> ClassifyAsync(string chunkXml, IReadOnlyList<int> allowedIndexes,
            CancellationToken ct = default) => ResultAsync();

        public Task<ChunkResult> CritiqueAsync(string chunkXml, IReadOnlyList<int> allowedIndexes,
            CancellationToken ct = default) => ResultAsync();

        public Task<ChunkResult> ClassifyHierarchyAsync(IReadOnlyList<HierarchyItem> context,
            IReadOnlyList<HierarchyItem> headings, CancellationToken ct = default) => ResultAsync();

        public Task<string> BoundaryCutAsync(string systemPrompt, string userMessage,
            CancellationToken ct = default) => Task.FromResult(string.Empty);

        public void Dispose() { }

        private Task<ChunkResult> ResultAsync()
        {
            Calls++;
            return Task.FromResult(new ChunkResult([], "", 0, 0, new HashSet<int>()));
        }
    }
}
