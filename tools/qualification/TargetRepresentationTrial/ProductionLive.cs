using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using DocxHeaderExtractor.App;
using DocxHeaderExtractor.Contracts.Provider;
using DocxHeaderExtractor.DocumentProcessing.Harness;
using DocxHeaderExtractor.HeadingAgent.Formats;
using DocxHeaderExtractor.HeadingAgent.Selectors;

/// <summary>The production PDF profile against the real provider (OPENROUTER_API_KEY), with or without cropped page images. Writes the run
/// result, the alias list for scoring and a freeze manifest before any Gold is read. The key is never written.</summary>
internal static class ProductionLive
{
    sealed class NoAgents : IModelProvider { public T? Create<T>(string role) where T : class => null; }
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    internal static async Task Run(string pdf, string output, bool crop)
    {
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new InvalidOperationException("REFUSE_OVERWRITING_TRIAL");
        Directory.CreateDirectory(output);
        var options = new PipelineOptions { PdfVisualCropMargins = crop };
        // The alias universe the selector sees, for scoring only; loading it makes no provider call.
        var selector = (PdfSelector)HeadingAgentSetup.Create(options, new NoAgents()).Pdf!;
        var load = typeof(PdfSelector).GetMethod("Load", BindingFlags.NonPublic | BindingFlags.Instance, [typeof(string), typeof(CancellationToken)])!;
        var source = (AgentSource)load.Invoke(selector, [pdf, CancellationToken.None])!;
        File.WriteAllText(Path.Combine(output, "aliases.json"), JsonSerializer.Serialize(source.Lines.Select(l => new { alias = l.Alias, text = l.Text }), Json));
        var provider = OpenRouterProvider.FromEnvironment();
        if (!provider.HasKey) throw new InvalidOperationException("OPENROUTER_API_KEY_MISSING");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var result = await new DocumentRunner(options, provider).ExtractAsync(pdf);
        clock.Stop();
        var resultPath = Path.Combine(output, "native-run-result.json");
        File.WriteAllText(resultPath, JsonSerializer.Serialize(result, Json));
        File.WriteAllText(Path.Combine(output, "run-identity.json"), JsonSerializer.Serialize(new
        {
            mode = "production-live", pdf = Path.GetFileName(pdf), crop, selector.SelectorId, model = OpenRouterProvider.Model,
            durationSeconds = clock.Elapsed.TotalSeconds, headings = result.Outline.Headings.Count, goldRead = false,
        }, Json));
        var files = Directory.EnumerateFiles(output).OrderBy(f => f, StringComparer.Ordinal)
            .ToDictionary(Path.GetFileName, f => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f))));
        File.WriteAllText(Path.Combine(output, "pre-gold-freeze.json"), JsonSerializer.Serialize(new { status = "FROZEN_BEFORE_GOLD", files }, Json));
        Console.WriteLine($"LIVE_COMPLETE crop={crop} seconds={clock.Elapsed.TotalSeconds:0.0} headings={result.Outline.Headings.Count}");
    }
}
