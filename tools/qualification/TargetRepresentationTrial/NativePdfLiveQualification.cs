using System.Diagnostics;
using System.Text.Json;
using DocxHeaderExtractor.App;
using DocxHeaderExtractor.Agents.Budget;
using DocxHeaderExtractor.Contracts.Agents;
using DocxHeaderExtractor.Contracts.Models;
using DocxHeaderExtractor.DocumentProcessing.Harness;
using DocxHeaderExtractor.DocumentProcessing.Intake;
using DocxHeaderExtractor.HeadingAgent.Placement;
using DocxHeaderExtractor.HeadingAgent.Protocol;
using DocxHeaderExtractor.HeadingAgent.Selectors;

/// <summary>Runs the public native PDF route against the configured OpenRouter provider. This is a qualification runner; it never changes defaults.</summary>
internal static class NativePdfLiveQualification
{
    internal static async Task Run(string pdfPath, string output, int jobs)
    {
        if (jobs is not (6 or 8)) throw new ArgumentOutOfRangeException(nameof(jobs));
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new InvalidOperationException("REFUSE_OVERWRITING_TRIAL");
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(OpenRouterProvider.ApiKeyVariable)))
            throw new InvalidOperationException("API_KEY_MISSING");
        Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("DHX_OPENROUTER_MAX_CONCURRENCY", jobs.ToString());
        var options = new PipelineOptions
        {
            SourceCacheRoot = Path.Combine(output, "source-cache"),
            PdfSourceBoundSelection = true,
            PdfRasterGeometryEvidence = true,
            PdfAdaptiveVisualSelection = true,
            PdfVisualSelectionMaxRequestBytes = 786432,
            PdfMembershipInstructionVersion = 14,
            PdfClarifyMembership = true,
            PdfSeparateGroupingInstruction = true,
            PdfPairwiseGrouping = true,
            PdfRepairParentChains = true,
            PdfCompactGeometry = true,
            PdfVisualMaxWidth = 960,
            PdfVisualQuality = 50,
            PdfVisualGrayscale = true,
            PdfDynamicCompletionBudget = true,
            PdfJobs = jobs,
        };
        var selector = new PdfSelector(parallelism: jobs, sourceBound: true, clarifyMembership: true,
            membershipInstructionVersion: 14, separateGroupingInstruction: true, pairwiseGrouping: true,
            generalBoundaryPrompt: true, rasterGeometry: true, adaptiveVisualSelection: true,
            resourcePolicy: new RequestPolicy { MaxRequestBytes = 786432 },
            compactGeometry: true, visualMaxWidth: 960, visualQuality: 50, visualGrayscale: true,
            dynamicCompletionBudget: true);
        var placer = new HeadingPlacer(repairParentChains: true);
        var provider = OpenRouterProvider.FromEnvironment();
        var file = UploadedFile.FromLocalPath(pdfPath);
        var watch = Stopwatch.StartNew();
        var result = await new PdfHandler(options, provider, new HeadingAgents(Pdf: selector, Placer: placer)).ExtractAsync(file);
        watch.Stop();
        File.WriteAllText(Path.Combine(output, "native-run-result.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(output, "run-identity.json"), JsonSerializer.Serialize(new
        {
            mode = "public-PdfHandler-ExtractAsync-openrouter-qwen-qualification",
            sourceSha256 = file.Sha256,
            goldInRequests = false,
            selector = new { selector.SelectorId, membershipVersion = 14, selectionInstructionVersion = 12,
                structuralUnitProtocol = PdfUnitSelectionProtocol.Version, compactGeometry = true,
                imageWidth = 960, imageQuality = 50, grayscale = true, dynamicCompletionBudget = true, concurrency = jobs },
            durationSeconds = watch.Elapsed.TotalSeconds,
            headingProviderCalls = result.HeadingPipeline?.Audit?.HeadingProviderCalls,
            headings = result.Outline.Headings.Count,
            sourceCount = result.Outline.SourceCount,
            provider = new { model = OpenRouterProvider.Model, route = "Alibaba", externalProviderUsed = true }
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
