using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Tests;

public sealed class InferenceFactoryOwnershipTests
{
    [Fact]
    public void Factory_contract_and_implementation_take_only_a_cancellation_token()
    {
        foreach (var owner in new[] { typeof(IInferenceTransportFactory), typeof(InferenceTransportFactory) })
        foreach (var name in new[] { "CreateAsync", "CreatePdfProductionAsync" })
        {
            var method = owner.GetMethod(name)!;
            var parameter = Assert.Single(method.GetParameters());
            Assert.Equal(typeof(CancellationToken), parameter.ParameterType);
            Assert.True(parameter.HasDefaultValue);
        }
    }

    [Fact]
    public void Inference_contract_and_factories_do_not_reference_pipeline_options()
    {
        foreach (var path in new[]
                 {
                     "src/DocxHeaderExtractor.DocumentProcessing/Inference/IInferenceTransport.cs",
                     "src/DocxHeaderExtractor.Infrastructure/AI/InferenceTransportFactory.cs",
                     "src/DocxHeaderExtractor.Web/WebInferenceTransportFactory.cs",
                 })
        {
            var source = File.ReadAllText(TestRepository.Path(path));
            Assert.DoesNotContain("PipelineOptions", source, StringComparison.Ordinal);
            Assert.DoesNotContain("DocxHeaderExtractor.DocumentProcessing.Pipeline", source, StringComparison.Ordinal);
        }
    }
}
