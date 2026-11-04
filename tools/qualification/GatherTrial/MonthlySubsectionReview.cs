using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using DocxHeaderExtractor.App;
using DocxHeaderExtractor.DocumentProcessing.Gather;

/// <summary>Qualification-only semantic review of one unresolved scope from a frozen routing audit.</summary>
internal static class MonthlySubsectionReview
{
    sealed class Capture(IGatherModel inner, string folder) : IGatherModel
    {
        public async Task<string> CompleteAsync(string system, string user, CancellationToken ct)
        {
            File.WriteAllText(Path.Combine(folder, "request.json"), JsonSerializer.Serialize(new { system, user }));
            var response = await inner.CompleteAsync(system, user, ct);
            File.WriteAllText(Path.Combine(folder, "response.txt"), response);
            return response;
        }
    }
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 4 || !int.TryParse(args[2], out var index)) throw new ArgumentException("monthly-subsection-review <audit.json> <scope-index> <output-dir>");
        var folder = Path.GetFullPath(args[3]);
        if (Directory.Exists(folder)) throw new IOException("OUTPUT_DIRECTORY_ALREADY_EXISTS");
        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("OPENROUTER_API_KEY_MISSING");
        using var doc = JsonDocument.Parse(File.ReadAllText(args[1]));
        var scopes = doc.RootElement.GetProperty("results");
        if (index < 0 || index >= scopes.GetArrayLength()) throw new ArgumentOutOfRangeException(nameof(index));
        var scope = scopes[index];
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var targets = scope.GetProperty("targets").Deserialize<GatherSubsectionTarget[]>(opts) ?? [];
        var facts = scope.GetProperty("unresolved").Deserialize<GatheredFact[]>(opts) ?? [];
        if (targets.Length == 0 || facts.Length == 0) throw new InvalidDataException("NO_REVIEWABLE_ITEMS");
        var candidates = targets.Select(t => new GatherCandidate(t.Heading, t.Location, t.Heading, t.DirectContent)).ToArray();
        var topic = scope.GetProperty("topic").GetString()!;
        var items = facts.Select(f => new GatherUncertain("SUBSECTION_UNRESOLVED", f.Parts[0].Text,
            f.Parts[0].SourceId + " " + f.Parts[0].Location, topic, candidates, [f], "Không có nhãn khớp duy nhất với mục con")).ToArray();
        Directory.CreateDirectory(folder);
        var clock = Stopwatch.StartNew();
        using var model = new GatherOpenRouterModel(key, maxCalls: 2, ceilingUsd: 1, diagnosticsPath: Path.Combine(folder, "provider-events.jsonl"));
        var result = await GatherUncertainReview.ReviewAsync(items, new Capture(model, folder));
        File.WriteAllText(Path.Combine(folder, "result.json"), JsonSerializer.Serialize(new { scopeIndex = index, topic, seconds = clock.Elapsed.TotalSeconds, items, result, attempts = model.Attempts }, opts));
        Console.WriteLine("reviewed=" + items.Length + " error=" + result.Error + " seconds=" + clock.Elapsed.TotalSeconds.ToString("F1"));
        return result.Error is null ? 0 : 1;
    }
}
