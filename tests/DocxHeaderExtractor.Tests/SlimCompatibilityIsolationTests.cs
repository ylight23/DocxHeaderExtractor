using System.Reflection;

namespace DocxHeaderExtractor.Tests;

public sealed class SlimCompatibilityIsolationTests
{
    [Fact]
    public void Docx_authority_pipeline_exposes_only_native_run_contracts()
    {
        var type = Type.GetType(
            "DocxHeaderExtractor.Core.Pipeline.DocxAuthorityPipeline, DocxHeaderExtractor.Core")
            ?? throw new InvalidOperationException("Authority pipeline type not found.");
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

        Assert.DoesNotContain(methods, method => method.GetParameters().Any(parameter =>
            parameter.ParameterType.Name.Contains("Slim", StringComparison.Ordinal) ||
            parameter.ParameterType.Name.Contains("Compatibility", StringComparison.Ordinal)));
        Assert.Contains(methods, method => method.Name == "RunAsync" &&
            method.GetParameters().FirstOrDefault()?.ParameterType.Name == "DocxPolicyState");
    }

    [Fact]
    public void Removed_boundary_source_files_are_not_referenced_by_normal_pipeline()
    {
        var root = FindRepositoryRoot();
        var authority = File.ReadAllText(Path.Combine(root, "src", "DocxHeaderExtractor.Core", "Pipeline",
            "AuthorityExtractionPipeline.cs"));
        var docxAuthority = File.ReadAllText(Path.Combine(root, "src", "DocxHeaderExtractor.Core", "Pipeline",
            "DocxAuthorityPipeline.cs"));

        Assert.DoesNotContain("SlimCompatibility", authority, StringComparison.Ordinal);
        Assert.DoesNotContain("SlimCompatibility", docxAuthority, StringComparison.Ordinal);
        Assert.DoesNotContain("ExtractForAuthority", authority, StringComparison.Ordinal);
        Assert.DoesNotContain("ExtractForAuthority", docxAuthority, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DocxHeaderExtractor.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
