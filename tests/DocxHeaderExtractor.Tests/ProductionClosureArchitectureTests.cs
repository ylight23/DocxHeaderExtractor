using System.Reflection;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Routing;

namespace DocxHeaderExtractor.Tests;

public sealed class ProductionClosureArchitectureTests
{
    [Fact]
    public void Production_cannot_depend_on_qualification_but_keeps_its_internal_replay_seam()
    {
        var root = TestRepository.Path("src");
        foreach (var projectDirectory in Directory.EnumerateDirectories(root)
                     .Where(path => Path.GetFileName(path) != "DocxHeaderExtractor.V5Qualification"))
        {
            foreach (var projectPath in Directory.EnumerateFiles(projectDirectory, "*.csproj"))
            {
                var project = XDocument.Load(projectPath);
                Assert.DoesNotContain(project.Descendants("ProjectReference"), reference =>
                    reference.Attribute("Include")!.Value.Contains("V5Qualification", StringComparison.Ordinal));
            }
            foreach (var path in Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories)
                         .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin")))
                Assert.DoesNotContain("DocxHeaderExtractor.V5Qualification", File.ReadAllText(path), StringComparison.Ordinal);
        }
        Assert.True(Directory.Exists(Path.Combine(root, "DocxHeaderExtractor.V5Qualification", "LegacyCore")));
        Assert.True(Directory.Exists(Path.Combine(root, "DocxHeaderExtractor.V5Qualification", "LegacyPdf")));
        Assert.Contains(typeof(PipelineOptions).Assembly.GetCustomAttributes<System.Runtime.CompilerServices.InternalsVisibleToAttribute>(),
            attribute => attribute.AssemblyName == "dhx-v5-qualify");
    }

    [Fact]
    public void Private_transport_ownership_has_no_analyst_vocabulary()
    {
        foreach (var type in new[] { typeof(DocxExtractionPipeline), typeof(PdfExtractionPipeline), typeof(PdfExtractionHandler) })
        {
            Assert.DoesNotContain(type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static),
                field => field.Name.Contains("analyst", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(type.GetMethods(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static).Where(method => method.IsPrivate),
                method => method.Name.Contains("analyst", StringComparison.OrdinalIgnoreCase) ||
                          method.GetParameters().Any(p => p.Name!.Contains("analyst", StringComparison.OrdinalIgnoreCase)));
        }
    }

    [Fact]
    public void Compatibility_wire_fields_and_public_named_parameters_are_retained()
    {
        Assert.Equal("rawAnalystResponses", typeof(PipelineExecutionAudit).GetProperty("RawAnalystResponses")!
            .GetCustomAttribute<JsonPropertyNameAttribute>()!.Name);
        Assert.Equal("route", typeof(PipelineExecutionAudit).GetProperty("PipelineId")!
            .GetCustomAttribute<JsonPropertyNameAttribute>()!.Name);
        Assert.NotNull(typeof(DocumentOutline).GetProperty("RouteAudit"));
        Assert.NotNull(typeof(DocumentOutline).GetProperty("DeterministicRoute"));
        var method = typeof(PdfExtractionPipeline).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(value => value.Name == "RunExecutionAsync");
        Assert.Equal(new[] { "file", "options", "analyst", "quarantinedIndexes", "analystSendsDataExternally", "ct" },
            method.GetParameters().Select(parameter => parameter.Name));
        Assert.Contains(typeof(DocxExtractionPipeline).GetConstructors(), constructor =>
            constructor.GetParameters().Any(parameter => parameter.Name == "analystFactory"));
    }
}
