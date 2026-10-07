using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Semantics.Canonical;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Semantics.Canonical;
using DocxHeaderExtractor.Infrastructure.AI.QualifiedInference;

namespace DocxHeaderExtractor.Tests;

public sealed class CoreOwnershipArchitectureTests
{
    [Fact]
    public void Models_cannot_reown_domain_services_after_the_contract_implementation_split()
    {
        var groups = new Dictionary<string, Type[]>
        {
            ["Binding"] = [typeof(SemanticSourceAliasCatalog), typeof(CanonicalSemanticExactBinder),
                typeof(SemanticSourcePartBinder), typeof(SemanticSourceProjection),
                typeof(SemanticCoordinateBinding), typeof(CanonicalSemanticHardBindingValidator),
                typeof(DocxHeaderExtractor.Core.Semantics.Binding.SourceTextBoundaryMap)],
            ["Validation"] = [typeof(CanonicalSemanticContractValidator), typeof(StructuralRelationProposalValidator),
                typeof(StructuralProposalValidator), typeof(ValidatedStructureFactory)],
            ["Parsing"] = [typeof(CanonicalSemanticProposalParser), typeof(SemanticProposalDecoder)],
            ["Identity"] = [typeof(CanonicalSemanticIdentityResolver)],
        };
        var root = TestRepository.Path("src/DocxHeaderExtractor.Core");
        var models = string.Join("\n", Directory.EnumerateFiles(Path.Combine(root, "Models"), "*.cs",
            SearchOption.AllDirectories).Select(File.ReadAllText));
        foreach (var (area, types) in groups)
        foreach (var type in types)
        {
            Assert.Equal(typeof(CanonicalSemanticProposal).Assembly, type.Assembly);
            Assert.Equal("DocxHeaderExtractor.Core.Semantics." + area, type.Namespace);
            var source = File.ReadAllText(Path.Combine(root, "Semantics", area, type.Name + ".cs"));
            Assert.DoesNotContain("File.Open", source, StringComparison.Ordinal);
            Assert.DoesNotContain("HttpClient", source, StringComparison.Ordinal);
            Assert.DoesNotContain("ICanonicalSemanticTextModel", source, StringComparison.Ordinal);
            Assert.False(System.Text.RegularExpressions.Regex.IsMatch(models,
                @"\b(?:class|record)\s+" + type.Name + @"\b"), type.Name);
        }
        Assert.DoesNotContain("class CanonicalSemanticSourceHash", models, StringComparison.Ordinal);
        Assert.Equal("DocxHeaderExtractor.DocumentProcessing.Provenance", typeof(CanonicalSemanticSourceHash).Namespace);
    }

    [Fact]
    public void Contract_and_value_object_namespaces_remain_stable()
    {
        foreach (var type in new[] { typeof(CanonicalSemanticProposal), typeof(SemanticSourceAlias),
                     typeof(SemanticCoordinateBindingRequest), typeof(SemanticCoordinateBindingOutcome),
                     typeof(SemanticContractIssue), typeof(SemanticProposalValidationSummary),
                     typeof(CanonicalSemanticBindingValidation), typeof(SemanticProposalDecodeResult),
                     typeof(SemanticProposalDecodeFailure), typeof(ValidatedStructure),
                     typeof(StructuralRelationValidation), typeof(CanonicalSemanticGraph) })
            Assert.Equal("DocxHeaderExtractor.Core.Models", type.Namespace);
    }

    [Fact]
    public void Core_contains_no_concrete_provider_or_reverse_project_dependency()
    {
        var root = TestRepository.Path("src/DocxHeaderExtractor.Core");
        var forbidden = new[] { "OpenRouter", "qwen", "alibaba", "V5ProviderEnvelope", "V5ProviderRequestBodyV2_1",
            "DocxHeaderExtractor.DocumentProcessing", "DocxHeaderExtractor.Infrastructure" };
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                     .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                                    !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            var source = File.ReadAllText(file);
            foreach (var token in forbidden)
                Assert.False(source.Contains(token, StringComparison.OrdinalIgnoreCase), $"{token} in {file}");
        }
        var project = File.ReadAllText(Path.Combine(root, "DocxHeaderExtractor.Core.csproj"));
        Assert.DoesNotContain("ProjectReference", project, StringComparison.Ordinal);
        Assert.DoesNotContain("PackageReference", project, StringComparison.Ordinal);
    }

    [Fact]
    public void Domain_algorithms_and_inference_contracts_stay_in_Core_while_execution_and_carrier_do_not()
    {
        var core = typeof(CanonicalSemanticProposal).Assembly;
        foreach (var type in new[] { typeof(CanonicalSemanticPipeline), typeof(SemanticConflictNormalizer),
                     typeof(CanonicalSemanticGlobalConflictDetector), typeof(CanonicalSemanticPipelineResult),
                     typeof(SemanticConflictNormalizationResult), typeof(CanonicalSemanticContractValidator),
                     typeof(CanonicalSemanticHardBindingValidator), typeof(CanonicalSemanticBindingValidation),
                     typeof(CanonicalSemanticTextInferenceInput), typeof(SemanticContextPacket), typeof(QualifiedPromptText) })
            Assert.Equal(core, type.Assembly);

        var inference = typeof(ICanonicalSemanticTextModel).GetMethod(nameof(ICanonicalSemanticTextModel.InferAsync))!;
        Assert.Equal(typeof(CanonicalSemanticTextInferenceInput), inference.GetParameters()[0].ParameterType);
        Assert.Equal(core, inference.GetParameters()[0].ParameterType.Assembly);

        var processing = typeof(CanonicalSemanticTextProductionEntryPoint).Assembly;
        Assert.NotEqual(core, processing);
        Assert.Equal(processing, typeof(CanonicalSemanticRequestComposer).Assembly);
        Assert.Equal(processing, typeof(SemanticContextPacker).Assembly);
        Assert.NotEqual(core, typeof(OpenRouterQwen37JsonObjectCarrierV2_1).Assembly);
        Assert.Equal(typeof(OpenRouterQwen37JsonObjectCarrierV2_1).Assembly, typeof(V5ProviderEnvelope).Assembly);
        Assert.Equal(typeof(V5ProviderEnvelope).Assembly, typeof(V5ProviderRequestBodyV2_1).Assembly);
    }

    [Fact]
    public void Mixed_runtime_file_is_retired_and_replacement_owners_are_explicit()
    {
        var core = TestRepository.Path("src/DocxHeaderExtractor.Core");
        foreach (var oldFile in new[] { "CanonicalSemanticVnextRuntime.cs", "CanonicalSemanticTextProductionEntryPoint.cs",
                     "CanonicalSemanticPipeline.cs", "SemanticConflictNormalizer.cs", "CanonicalSemanticGlobalConflictDetector.cs",
                     "CanonicalSemanticRequestComposer.cs", "CanonicalSemanticTextInferenceContracts.cs" })
            Assert.False(File.Exists(Path.Combine(core, "Models", oldFile)), oldFile);

        foreach (var name in new[] { "CanonicalSemanticPipeline", "SemanticConflictNormalizer", "CanonicalSemanticGlobalConflictDetector" })
        {
            var source = File.ReadAllText(Path.Combine(core, "Semantics", "Canonical", name + ".cs"));
            Assert.Contains("namespace DocxHeaderExtractor.Core.Semantics.Canonical;", source, StringComparison.Ordinal);
            Assert.DoesNotContain("ICanonicalSemanticTextModel", source, StringComparison.Ordinal);
            Assert.DoesNotContain("HttpClient", source, StringComparison.Ordinal);
            Assert.DoesNotContain("File.Read", source, StringComparison.Ordinal);
        }
        var runtime = TestRepository.Path("src/DocxHeaderExtractor.DocumentProcessing/Semantics/Canonical");
        foreach (var name in new[] { "CanonicalSemanticTextProductionEntryPoint", "CanonicalSemanticRequestComposer", "SemanticContextPacker" })
            Assert.Contains("namespace DocxHeaderExtractor.DocumentProcessing.Semantics.Canonical;",
                File.ReadAllText(Path.Combine(runtime, name + ".cs")), StringComparison.Ordinal);
    }
}
