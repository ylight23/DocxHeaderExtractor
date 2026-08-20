using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Pipeline;
using DocxHeaderExtractor.Core.Repair;

namespace DocxHeaderExtractor.Tests;

public sealed class AutoRepairWorkflowTests
{
    [Fact]
    public async Task RepairWorkflowWritesEvidencePromptAndRuntimePolicy()
    {
        var temp = Path.Combine(Path.GetTempPath(), "dhx-auto-repair-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var input = Path.Combine(temp, "sample.docx");
        SampleDocumentFactory.Create(input);

        var workflow = new AutoRepairWorkflow(new PipelineOptions
        {
            DisableLlm = true,
        });

        var result = await workflow.RunAsync(
            input,
            new AutoRepairOptions(temp, AlwaysWriteCase: true));

        Assert.True(Directory.Exists(result.CaseDirectory));
        Assert.Contains(result.WrittenFiles, f => Path.GetFileName(f) == "failure-case.json");
        Assert.Contains(result.WrittenFiles, f => Path.GetFileName(f) == "probe-report.json");
        Assert.Contains(result.WrittenFiles, f => Path.GetFileName(f) == "candidate-report.json");
        Assert.Contains(result.WrittenFiles, f => Path.GetFileName(f) == "validation-report.json");
        Assert.Contains(result.WrittenFiles, f => Path.GetFileName(f) == "llm-analysis-prompt.md");
        Assert.Contains(result.WrittenFiles, f => Path.GetFileName(f) == "runtime-self-fix-plan.md");
        Assert.Contains(result.WrittenFiles, f => Path.GetFileName(f) == "repair-learning-log.jsonl");

        var prompt = await File.ReadAllTextAsync(Path.Combine(result.CaseDirectory, "llm-analysis-prompt.md"));
        Assert.Contains("You are an analyst, not the final judge", prompt);
        Assert.Contains("deterministic signals", prompt);

        var policy = await File.ReadAllTextAsync(Path.Combine(result.CaseDirectory, "runtime-self-fix-plan.md"));
        Assert.Contains("Allow in-process assembly mutation: `False`", policy);
        Assert.Contains("sidecar", policy);

        var candidates = await File.ReadAllTextAsync(Path.Combine(result.CaseDirectory, "candidate-report.json"));
        Assert.Contains("BestRoute", candidates);
        Assert.Contains("PatchCandidateNeeded", candidates);
        Assert.Contains("ScoreCalibrationStatus", candidates);
        Assert.Contains("RouteMetrics", candidates);

        var validation = await File.ReadAllTextAsync(Path.Combine(result.CaseDirectory, "validation-report.json"));
        Assert.Contains("candidate_exists", validation);
        Assert.Contains("title_pollution", validation);
    }
}
