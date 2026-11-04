using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using DocxHeaderExtractor.App;
using DocxHeaderExtractor.DocumentProcessing.Harness;

/// <summary>
/// Re-decides only the part of a DOCX that changed, with the live model: the original is extracted first to fill the reuse cache, then the edited
/// version is extracted with the cache (only the requests that changed are asked again), and once more without it as the control. Compares the
/// headings of the two edited runs. Never changes its inputs.
/// Usage: GatherTrial docx-region &lt;output-directory&gt; &lt;original.docx&gt; &lt;edited.docx&gt;
/// </summary>
static class DocxRegionTrial
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 4) throw new ArgumentException("docx-region <output-directory> <original.docx> <edited.docx>");
        var output = Path.GetFullPath(args[1]);
        if (Directory.Exists(output)) throw new IOException("OUTPUT_DIRECTORY_ALREADY_EXISTS");
        var original = Path.GetFullPath(args[2]);
        var edited = Path.GetFullPath(args[3]);
        static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        var hashes = new[] { original, edited }.ToDictionary(p => p, Hash);
        var provider = OpenRouterProvider.FromEnvironment();
        if (!provider.HasKey) throw new InvalidOperationException("OPENROUTER_API_KEY_MISSING");
        Directory.CreateDirectory(output);
        var cache = Path.Combine(output, "cache");
        var json = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var phases = new List<object>();
        var headings = new Dictionary<string, List<string>>();

        async Task<object> Phase(string name, string file, bool reuse)
        {
            var options = new PipelineOptions { DocxReuseSelection = reuse, DocxSelectionCacheRoot = cache, MaxProviderCostUsd = 1, MaxProviderAttempts = 80 };
            options.Log = _ => { };
            var clock = Stopwatch.StartNew();
            var result = await new DocumentRunner(options, provider).ExtractAsync(file);
            var audit = result.HeadingPipeline?.Audit;
            headings[name] = result.Outline.Headings.OrderBy(h => h.Index).Select(h => $"{h.Level} {h.StableId} {h.Text}").ToList();
            var row = new { phase = name, seconds = Math.Round(clock.Elapsed.TotalSeconds, 1), selectionCalls = audit?.SelectionProviderCalls,
                placementCalls = audit?.PlacementExecution?.LogicalCalls, headings = result.Outline.Headings.Count,
                confirmedUsd = result.ResourceBudget.ConfirmedUsd, attempts = result.ResourceBudget.Attempts };
            Console.WriteLine($"{name}: {row.seconds}s selection={row.selectionCalls} placement={row.placementCalls} headings={row.headings} usd={row.confirmedUsd:0.000000}");
            phases.Add(row);
            return row;
        }

        await Phase("base-original", original, reuse: true);
        await Phase("edited-region-cache", edited, reuse: true);
        await Phase("edited-full-control", edited, reuse: false);

        foreach (var (path, hash) in hashes)
            if (Hash(path) != hash) throw new InvalidDataException("INPUT_CHANGED:" + path);
        var region = headings["edited-region-cache"];
        var full = headings["edited-full-control"];
        var sameAsControl = region.SequenceEqual(full);
        File.WriteAllText(Path.Combine(output, "docx-region.json"), JsonSerializer.Serialize(new
        {
            original = Path.GetFileName(original), edited = Path.GetFileName(edited), hashes, phases,
            regionEqualsControl = sameAsControl, onlyInRegion = region.Except(full).ToArray(), onlyInControl = full.Except(region).ToArray(),
        }, json));
        Console.WriteLine($"region run equals control run: {sameAsControl}");
        return 0;
    }
}
