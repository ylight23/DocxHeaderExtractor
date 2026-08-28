using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

public sealed class SlimCompatibilityIsolationTests
{
    [Fact]
    public void Normal_authority_pipeline_has_no_concrete_slim_signature()
    {
        var pipelineType = Type.GetType(
            "DocxHeaderExtractor.Core.Pipeline.DocxAuthorityPipeline, DocxHeaderExtractor.Core")
            ?? throw new InvalidOperationException("Authority pipeline type not found.");
        var compatibilityType = Type.GetType(
            "DocxHeaderExtractor.Core.OpenXmlLayer.SlimCompatibilityContext, DocxHeaderExtractor.Core")
            ?? throw new InvalidOperationException("Compatibility boundary type not found.");
        var normal = pipelineType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Single(method => method.Name == "RunAsync" && method.GetParameters().First().ParameterType == typeof(DocxHeaderExtractor.Core.Models.SourceDocument));

        Assert.Equal(compatibilityType, normal.GetParameters()[1].ParameterType);
        Assert.DoesNotContain(normal.GetParameters(), parameter => parameter.ParameterType.Name is "SlimDocument" or "SlimParagraph");
    }

    [Fact]
    public void Boundary_artifact_records_zero_demotion_and_source_deltas()
    {
        var root = LoadArtifact().RootElement;
        Assert.True(root.GetProperty("slimCompatibilityBoundaryIntroduced").GetBoolean());
        Assert.Equal(0, root.GetProperty("demotionOperationsMoved").GetInt32());
        Assert.Equal(0, root.GetProperty("demotionBehaviorDelta").GetInt32());
        Assert.Equal(0, root.GetProperty("sourceFactDelta").GetInt32());
        Assert.Equal(0, root.GetProperty("candidateDelta").GetInt32());
        Assert.Equal(0, root.GetProperty("roleDelta").GetInt32());
        Assert.Equal(0, root.GetProperty("scoreDelta").GetInt32());
    }

    [Fact]
    public void Boundary_does_not_expose_source_fact_properties()
    {
        var type = Type.GetType(
            "DocxHeaderExtractor.Core.OpenXmlLayer.SlimCompatibilityContext, DocxHeaderExtractor.Core")
            ?? throw new InvalidOperationException("Compatibility boundary type not found.");
        var names = type.GetProperties()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("Text", names);
        Assert.DoesNotContain("StyleId", names);
        Assert.DoesNotContain("SourceSegments", names);
    }

    private static JsonDocument LoadArtifact() => JsonDocument.Parse(File.ReadAllText(Path.Combine(
        FindRepositoryRoot(), "eval", "architecture", "slim-compatibility-isolation.v1.json")));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DocxHeaderExtractor.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
