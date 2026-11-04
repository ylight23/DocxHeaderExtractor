using System.Security.Cryptography;
using System.Text.Json;
using DocxHeaderExtractor.App;
using DocxHeaderExtractor.DocumentProcessing.Gather;

// Qualification only: review name-matched candidates, record decisions, and never write the target document.
internal static class HeadingReviewTrial
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 6) throw new ArgumentException("headings-review <target.docx> <target-outline.json> <source.docx> <source-outline.json> <output-directory>");
        var target = Path.GetFullPath(args[1]);
        var targetOutline = DocumentTrial.Outline(args[2]);
        var source = Path.GetFullPath(args[3]);
        var sourceOutline = DocumentTrial.Outline(args[4]);
        var output = Path.GetFullPath(args[5]);
        if (Directory.Exists(output)) throw new IOException("OUTPUT_DIRECTORY_ALREADY_EXISTS");
        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("OPENROUTER_API_KEY_MISSING");
        Directory.CreateDirectory(output);
        static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        var targetHash = Hash(target);
        var sourceHash = Hash(source);
        var sourceId = "sha256:" + sourceHash;
        var (sections, excluded) = GatherByHeading.Build(target, targetOutline, [new(sourceId, source, sourceOutline)]);
        var units = GatherUnits.Read(source, sourceId, sourceOutline);
        if (sourceHash != Hash(source) || targetHash != Hash(target)) throw new IOException("DOCUMENT_CHANGED_DURING_READ");
        using var provider = new GatherOpenRouterModel(key, maxCalls: 4, ceilingUsd: 1.0,
            diagnosticsPath: Path.Combine(output, "provider-events.jsonl"));
        var options = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var reviews = new List<object>();
        foreach (var section in sections.Where(s => s.Run.Facts.Count != 0))
        {
            // Outline omissions can contaminate section.Goal; send the heading only for this diagnostic review.
            var (accepted, rejected, audits) = await GatherByHeading.ReviewAsync([section], [units], provider);
            var review = audits.Single().Result;
            reviews.Add(new { section.Heading, section.TargetLocation, section.NextHeadingLocation, section.Run.Complete, section.Run.Gaps,
                candidates = section.Run.Facts.Select((f, i) => new { id = i, text = f.Render(), f.Parts, f.Flags }), review,
                accepted = accepted.Single().Run.Facts, rejected });
            File.WriteAllText(Path.Combine(output, "review.json"), JsonSerializer.Serialize(new
            { targetHash, sourceHash, model = provider.CheckpointIdentity, reviews, excluded, attempts = provider.Attempts }, options));
        }
        Console.WriteLine($"reviewed_sections={reviews.Count} attempts={provider.Attempts.Count} output={output}");
        return 0;
    }
}
