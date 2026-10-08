using DocxHeaderExtractor.DocumentProcessing.Materialization;

namespace DocxHeaderExtractor.Tests;

public sealed class ExtractionMaterializationOwnershipTests
{
    [Fact]
    public void Retired_materialization_wrapper_has_no_live_type_or_pipeline_consumer()
    {
        Assert.Null(typeof(HeadingStructureMaterializer).Assembly.GetType(
            "DocxHeaderExtractor.DocumentProcessing.Materialization.StructuralMaterializationResult"));
        var source = File.ReadAllText(TestRepository.Path(
            "src/DocxHeaderExtractor.DocumentProcessing/Pipeline/DocxExtractionPipeline.cs"));
        Assert.DoesNotContain("StructuralMaterializationResult", source, StringComparison.Ordinal);
        // The old wrapper carried an empty projection graph unless audit was available.
        // Retirement must not silently grant an unaudited graph output authority.
        var emptyGraph = source.IndexOf("var structure = ValidatedStructureFactory.Create([]);", StringComparison.Ordinal);
        var auditGate = source.IndexOf("if (audit is not null)", emptyGraph, StringComparison.Ordinal);
        var acceptedGraph = source.IndexOf("structure = authority.Structure;", auditGate, StringComparison.Ordinal);
        var output = source.IndexOf("HeadingOutlineProjection.Project(", acceptedGraph, StringComparison.Ordinal);
        Assert.True(emptyGraph >= 0 && auditGate > emptyGraph && acceptedGraph > auditGate && output > acceptedGraph);
        Assert.Contains("IReadOnlySet<string> emittedIds = new HashSet<string>(StringComparer.Ordinal);", source);
        Assert.Contains("emittedIds = authority.EmittedElementIds ?? structure.Elements", source);
        Assert.Contains("structure, emittedIds, authority.ProjectionContext", source);
    }
}
