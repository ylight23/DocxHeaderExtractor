using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;

namespace DocxHeaderExtractor.Tests;

public sealed class NormalSlimApiRetirementTests
{
    [Fact]
    public void Normal_extraction_api_returns_source_and_compatibility_only()
    {
        var extractor = typeof(DocxSlimExtractor);
        var method = extractor.GetMethod("ExtractForAuthority",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Normal authority extraction API not found.");
        var resultType = method.ReturnType;

        Assert.Equal("AuthoritySourceExtractionResult", resultType.Name);
        Assert.Contains(resultType.GetProperties(), property => property.PropertyType == typeof(SourceDocument));
        Assert.DoesNotContain(resultType.GetProperties(), property =>
            property.PropertyType.Name is "SlimDocument" or "SlimParagraph");
        Assert.DoesNotContain(resultType.GetProperties(), property => property.Name == "Slim");
    }

    [Fact]
    public void Legacy_extract_api_remains_available()
    {
        var method = typeof(DocxSlimExtractor).GetMethod("Extract", [typeof(string)])
            ?? throw new InvalidOperationException("Legacy extraction API not found.");

        Assert.Equal("SlimDocument", method.ReturnType.Name);
        Assert.True(method.IsDefined(typeof(ObsoleteAttribute), inherit: false));
    }

    [Fact]
    public void Source_facts_legacy_api_is_deprecated_without_deprecating_types()
    {
        var method = typeof(DocxSlimExtractor).GetMethod("ExtractWithSourceFacts", [typeof(string)])
            ?? throw new InvalidOperationException("Legacy source-facts extraction API not found.");

        Assert.True(method.IsDefined(typeof(ObsoleteAttribute), inherit: false));
        Assert.Equal("DocxSourceExtractionResult", method.ReturnType.Name);
        Assert.False(typeof(SlimDocument).IsDefined(typeof(ObsoleteAttribute), inherit: false));
        Assert.False(typeof(SlimParagraph).IsDefined(typeof(ObsoleteAttribute), inherit: false));
    }

    [Fact]
    public void Normal_pipeline_uses_the_new_extraction_entry_point()
    {
        var path = Path.Combine(FindRepositoryRoot(), "src", "DocxHeaderExtractor.Core", "Pipeline",
            "AuthorityExtractionPipeline.cs");
        var text = File.ReadAllText(path);

        Assert.Contains("ExtractForAuthority", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ExtractWithSourceFacts(conversion.Path)", text, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DocxHeaderExtractor.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
