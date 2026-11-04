using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxHeaderExtractor.App;
using DocxHeaderExtractor.DocumentProcessing.Gather;
using DocxHeaderExtractor.DocumentProcessing.Harness;
using DocxHeaderExtractor.DocumentProcessing.Output;

/// <summary>
/// How much one source's headings, and the gather built on them, vary from run to run. The target's and the other sources' headings are fixed
/// (stored outlines given as files), so only the measured source is extracted again. Each round extracts the original source and a copy with
/// one heading paragraph edited at a fixed location, both fresh (no cache), and gathers each into a copy of the target. Never changes its inputs.
/// Usage: GatherTrial variance &lt;output-directory&gt; &lt;rounds&gt; &lt;paragraph-location&gt; &lt;appended-text&gt;
///        &lt;target.docx=stored-outline.json&gt; &lt;measured-source.docx&gt; &lt;other-source.docx=stored-outline.json&gt;...
/// </summary>
static class VarianceTrial
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 7) throw new ArgumentException("variance <output> <rounds> <location> <appended-text> <target.docx=outline.json> <source.docx> <other.docx=outline.json>...");
        var output = Path.GetFullPath(args[1]);
        if (Directory.Exists(output)) throw new IOException("OUTPUT_DIRECTORY_ALREADY_EXISTS");
        var rounds = int.Parse(args[2]);
        var location = args[3];
        var appended = args[4];
        var provider = OpenRouterProvider.FromEnvironment();
        if (!provider.HasKey) throw new InvalidOperationException("OPENROUTER_API_KEY_MISSING");
        static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        static IReadOnlyList<HeadingRecord> Stored(string path) =>
            JsonSerializer.Deserialize<StoredOutline>(File.ReadAllText(path))!.Headings;
        var (targetPath, targetOutline) = (Path.GetFullPath(args[5].Split('=')[0]), Stored(args[5].Split('=')[1]));
        var measured = Path.GetFullPath(args[6]);
        var others = args[7..].Select((a, i) => new GatherSourceFile("O" + (i + 1), Path.GetFullPath(a.Split('=')[0]), Stored(a.Split('=')[1]))).ToList();
        var inputs = new[] { targetPath, measured }.Concat(others.Select(o => o.Path)).ToDictionary(p => p, Hash);

        Directory.CreateDirectory(output);
        var edited = Path.Combine(output, "edited-" + Path.GetFileName(measured));
        File.Copy(measured, edited);
        string editedFrom, editedTo;
        using (var doc = WordprocessingDocument.Open(edited, true))
        {
            var m = System.Text.RegularExpressions.Regex.Match(location, @"^body\[1\]/p\[(\d+)\]$");
            if (!m.Success) throw new ArgumentException("LOCATION_NOT_A_BODY_PARAGRAPH");
            var paragraph = doc.MainDocumentPart!.Document!.Body!.Elements<Paragraph>().ElementAt(int.Parse(m.Groups[1].Value) - 1);
            editedFrom = paragraph.InnerText;
            var last = paragraph.Descendants<Text>().Last();
            last.Text += appended;
            last.Space = DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve;
            editedTo = paragraph.InnerText;
            doc.MainDocumentPart.Document.Save();
        }

        var options = new PipelineOptions { MaxProviderCostUsd = 1, MaxProviderAttempts = 80 };
        options.Log = _ => { };
        var json = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var runs = new List<object>();
        for (var round = 1; round <= rounds; round++)
        {
            var results = await Task.WhenAll(new[] { ("original", measured), ("edited", edited) }.Select(async v =>
            {
                var clock = Stopwatch.StartNew();
                try
                {
                    var run = await new DocumentRunner(options, provider).ExtractAsync(v.Item2);
                    var seconds = clock.Elapsed.TotalSeconds;
                    var audit = run.HeadingPipeline?.Audit;
                    var headings = run.Outline.Headings;
                    var plan = GatherByHeading.Build(targetPath, targetOutline, [new GatherSourceFile("M", v.Item2, headings), .. others], addMissingHeadings: true);
                    var decided = GatherByHeading.DecidedByChain(plan.Sections);
                    var content = string.Join("\n", decided.SelectMany(s => s.Run.Facts.Select(f => s.Heading + "␟" + f.Render())));
                    return (object)new
                    {
                        round, variant = v.Item1, seconds = Math.Round(seconds, 1),
                        calls = (audit?.SelectionProviderCalls ?? 0) + (audit?.PlacementExecution?.LogicalCalls ?? 0), budget = run.ResourceBudget,
                        headings = headings.OrderBy(h => h.Index).Select(h => new { h.StableId, h.Level, h.Text }),
                        facts = decided.Sum(s => s.Run.Facts.Count), newHeadings = decided.Count(s => s.NewHeadingDepth is not null),
                        uncertain = plan.Uncertain.Count, excluded = plan.Excluded.Count,
                        contentHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)))[..16],
                    };
                }
                catch (Exception ex) { return new { round, variant = v.Item1, error = ex.GetType().Name + ": " + ex.Message }; }
            }));
            runs.AddRange(results);
            foreach (var r in results) Console.WriteLine(JsonSerializer.Serialize(r, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping })[..Math.Min(220, JsonSerializer.Serialize(r).Length)]);
            File.WriteAllText(Path.Combine(output, "variance.json"), JsonSerializer.Serialize(new { location, editedFrom, editedTo, inputs, runs }, json));
        }
        foreach (var (path, hash) in inputs)
            if (Hash(path) != hash) throw new InvalidDataException("INPUT_CHANGED:" + path);
        return 0;
    }

    /// <summary>
    /// The gather of every measured outline in a variance.json again, offline (no model): the target and the other sources keep their stored
    /// outlines. Shows how a change to the gather alone moves the results.
    /// Usage: GatherTrial variance-replay &lt;variance.json&gt; &lt;original.docx&gt; &lt;edited.docx&gt; &lt;target.docx=outline.json&gt; &lt;other.docx=outline.json&gt;...
    /// </summary>
    public static int Replay(string[] args)
    {
        static IReadOnlyList<HeadingRecord> Stored(string path) => JsonSerializer.Deserialize<StoredOutline>(File.ReadAllText(path))!.Headings;
        using var doc = JsonDocument.Parse(File.ReadAllText(args[1]));
        var files = new Dictionary<string, string> { ["original"] = Path.GetFullPath(args[2]), ["edited"] = Path.GetFullPath(args[3]) };
        var (targetPath, targetOutline) = (Path.GetFullPath(args[4].Split('=')[0]), Stored(args[4].Split('=')[1]));
        var others = args[5..].Select((a, i) => new GatherSourceFile("O" + (i + 1), Path.GetFullPath(a.Split('=')[0]), Stored(a.Split('=')[1]))).ToList();
        foreach (var run in doc.RootElement.GetProperty("runs").EnumerateArray())
        {
            if (!run.TryGetProperty("headings", out var hs)) continue;
            var headings = hs.EnumerateArray().Select(h =>
            {
                var id = h.GetProperty("StableId").GetString()!;
                var n = int.Parse(System.Text.RegularExpressions.Regex.Match(id, @"p\[(\d+)\]$").Groups[1].Value);
                return new HeadingRecord { Index = n - 1, StableId = id, Text = h.GetProperty("Text").GetString()!,
                    Level = h.GetProperty("Level").ValueKind == JsonValueKind.Number ? h.GetProperty("Level").GetInt32() : null };
            }).ToList();
            var variant = run.GetProperty("variant").GetString()!;
            // DHX_MATCH=order replays the same outlines with matching by heading order (no model).
            var matching = Environment.GetEnvironmentVariable("DHX_MATCH") == "order" ? GatherMatching.Order : GatherMatching.Chain;
            var plan = GatherByHeading.Build(targetPath, targetOutline, [new GatherSourceFile("M", files[variant], headings), .. others], addMissingHeadings: true,
                matching: matching);
            var decided = GatherByHeading.DecidedByChain(plan.Sections);
            Console.WriteLine($"r{run.GetProperty("round").GetInt32()} {variant,-8} before: facts={run.GetProperty("facts").GetInt32(),3} uncertain={run.GetProperty("uncertain").GetInt32(),2}" +
                $"   after: facts={decided.Sum(s => s.Run.Facts.Count),3} new={decided.Count(s => s.NewHeadingDepth is not null)} uncertain={plan.Uncertain.Count,2} " +
                $"title-excluded={plan.Excluded.Count(e => e.Reason.StartsWith("DOCUMENT_TITLE", StringComparison.Ordinal))}");
        }
        return 0;
    }
}
