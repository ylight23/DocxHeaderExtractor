using DocxHeaderExtractor.AgentHarness;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class AgentKeyPackageActionTests
{
    [Fact]
    public async Task AgentHarnessCanCreatePartialKeyPackageAsGuardedAction()
    {
        var temp = Path.Combine(Path.GetTempPath(), "dhx-agent-key-package-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var input = Path.Combine(temp, "sample.docx");
        var output = Path.Combine(temp, "packages");
        SampleDocumentFactory.Create(input);

        var options = new PipelineOptions
        {
            DisableLlm = true,
        };
        using var extraction = new PipelineDocumentExtractionTool(options);
        using var keyPackage = new PartialKeyPackageActionTool(options);
        var harness = new DocumentAgentHarness(
            new AgentToolRegistry([extraction], [keyPackage]));

        var run = await harness.RunAsync(new DocumentAgentRequest(input)
        {
            KeyPackageOutputDirectory = output,
            KeyPackageLimit = 2,
        });

        Assert.NotNull(run.Writeback);
        Assert.True(Directory.Exists(run.Writeback.OutputPath));
        Assert.InRange(run.Writeback.Applied, 1, 2);
        Assert.Contains(run.Trace, e => e.Stage == "guardrail.key_package_target" && e.Kind == AgentRunEventKind.Passed);
        Assert.Contains(run.Trace, e => e.Stage == "action.create_partial_key_package" && e.Kind == AgentRunEventKind.Completed);
        Assert.True(Directory.EnumerateFiles(run.Writeback.OutputPath, "*.partial.key").Any());
        Assert.True(Directory.EnumerateFiles(run.Writeback.OutputPath, "*.partial-review.csv").Any());
    }
}
