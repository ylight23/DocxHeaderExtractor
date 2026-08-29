using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;

namespace DocxHeaderExtractor.Tests;

public sealed class NormalSlimApiRetirementTests
{
    [Fact]
    public void Legacy_slim_extraction_remains_only_as_legacy_surface()
    {
        var method = typeof(DocxSlimExtractor).GetMethod("Extract", [typeof(string)])
            ?? throw new InvalidOperationException("Legacy extraction API not found.");

        Assert.Equal("SlimDocument", method.ReturnType.Name);
        Assert.True(method.IsDefined(typeof(ObsoleteAttribute), inherit: false));
    }

    [Fact]
    public void Legacy_source_facts_api_remains_available_for_migration_lineage()
    {
        var method = typeof(DocxSlimExtractor).GetMethod("ExtractWithSourceFacts", [typeof(string)])
            ?? throw new InvalidOperationException("Legacy source-facts extraction API not found.");

        Assert.True(method.IsDefined(typeof(ObsoleteAttribute), inherit: false));
        Assert.Equal("DocxSourceExtractionResult", method.ReturnType.Name);
        Assert.False(typeof(SlimDocument).IsDefined(typeof(ObsoleteAttribute), inherit: false));
        Assert.False(typeof(SlimParagraph).IsDefined(typeof(ObsoleteAttribute), inherit: false));
    }

    [Fact]
    public void Removed_authority_compatibility_api_is_absent()
    {
        Assert.Null(typeof(DocxSlimExtractor).GetMethod("ExtractForAuthority",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic));
        Assert.Null(Type.GetType(
            "DocxHeaderExtractor.Core.OpenXmlLayer.AuthoritySourceExtractionResult, DocxHeaderExtractor.Core"));
        Assert.Null(Type.GetType(
            "DocxHeaderExtractor.Core.OpenXmlLayer.SlimCompatibilityContext, DocxHeaderExtractor.Core"));
    }

    [Fact]
    public void Normal_authority_pipeline_uses_native_source_policy_entry_point()
    {
        var pipelineType = typeof(DocxHeaderExtractor.Core.Pipeline.AuthorityExtractionPipeline);
        var run = pipelineType.GetMethod("RunAsync", [typeof(string), typeof(CancellationToken)])
            ?? throw new InvalidOperationException("Native authority entry point not found.");

        Assert.DoesNotContain(pipelineType.GetMethods(), method =>
            method.GetParameters().Any(parameter => parameter.ParameterType.Name is
                "SlimDocument" or "SlimParagraph" or "SlimCompatibilityContext"));
        Assert.Equal(typeof(Task<DocumentOutline>), run.ReturnType);
    }
}
