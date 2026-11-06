using DocxHeaderExtractor.App;
using DocxHeaderExtractor.DocumentProcessing.Harness;

/// <summary>Private diagnostic capture for one DOCX extraction; provider calls require explicit invocation.</summary>
static class CaptureDocxTrial
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 3)
            throw new ArgumentException("capture-docx <private-capture-directory> <docx>");
        var input = Path.GetFullPath(args[2]);
        if (!File.Exists(input)) throw new FileNotFoundException("DOCX_NOT_FOUND", input);
        // The same boundary as the web capture: outside the repository, or its ignored scratchpad/evidence directory.
        var settings = new DiagnosticCaptureSettings(args[1], Directory.GetCurrentDirectory());
        var provider = OpenRouterProvider.FromEnvironment();
        if (!provider.HasKey) throw new InvalidOperationException("OPENROUTER_API_KEY_MISSING");
        var capture = settings.Start(provider.CreateRoute().ApiKey) ?? throw new InvalidOperationException("CAPTURE_REQUIRED");
        var options = new PipelineOptions { DocxReuseSelection = false, MaxProviderCostUsd = 0.50, MaxProviderAttempts = 40 };
        try
        {
            var result = await new DocumentRunner(options, capture.Provider(provider, options, "doc-001")).ExtractAsync(input);
            capture.Record("doc-001.pipeline-audit.json", new { audit = result.HeadingPipeline?.Audit,
                result.ResourceBudget, auditAvailable = result.HeadingPipeline?.Audit is not null });
            Console.WriteLine($"Extraction returned {result.Outline.Headings.Count} headings; confirmed USD {result.ResourceBudget?.ConfirmedUsd}.");
        }
        finally
        {
            // Also runs after a failed extraction: Summary reports any missing response or placement evidence explicitly.
            capture.Summary();
            Console.WriteLine("Capture limitations: PLACE wire body is unavailable; inspect PLACE model evidence and failures, and verify selector protocol errors separately.");
        }
        return 0;
    }
}
