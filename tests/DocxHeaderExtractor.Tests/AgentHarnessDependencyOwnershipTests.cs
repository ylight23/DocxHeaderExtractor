using System.Xml.Linq;
using DocxHeaderExtractor.AgentHarness;

namespace DocxHeaderExtractor.Tests;

public sealed class AgentHarnessDependencyOwnershipTests
{
    [Fact]
    public void Harness_does_not_reference_infrastructure_or_construct_a_default_provider()
    {
        var directory = TestRepository.Path("src/DocxHeaderExtractor.AgentHarness");
        var project = XDocument.Load(Path.Combine(directory, "DocxHeaderExtractor.AgentHarness.csproj"));
        Assert.DoesNotContain(project.Descendants("ProjectReference"), reference =>
            reference.Attribute("Include")!.Value.Contains("Infrastructure", StringComparison.Ordinal));

        foreach (var path in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
                     .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin")))
            Assert.DoesNotContain("DocxHeaderExtractor.Infrastructure", File.ReadAllText(path), StringComparison.Ordinal);

        Assert.DoesNotContain(typeof(PipelineDocumentExtractionTool).GetConstructors(), constructor =>
            constructor.GetParameters().Length == 1);
    }
}
