using System.Security.Cryptography;
using System.Text.Json;
using DocxHeaderExtractor.App;
using DocxHeaderExtractor.DocumentProcessing.Gather;

// Qualification: retest only outstanding quote proposals against frozen source snapshots.
internal static class MonthlyQuoteRepairTrial
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 8) throw new ArgumentException("monthly-quote-repair <previous.json> <source8> <source9> <outline8> <outline9> <output-dir> <concurrency>");
        var output = Path.GetFullPath(args[6]);
        if (Directory.Exists(output)) throw new IOException("OUTPUT_DIRECTORY_ALREADY_EXISTS");
        var concurrency = int.Parse(args[7]);
        if (concurrency is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(concurrency));
        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("OPENROUTER_API_KEY_MISSING");
        using var previous = JsonDocument.Parse(File.ReadAllText(args[1]));
        var root = previous.RootElement;
        static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        var paths = new Dictionary<int, string> { [8] = Path.GetFullPath(args[2]), [9] = Path.GetFullPath(args[3]) };
        foreach (var (n, path) in paths)
            if (root.GetProperty("hashes").GetProperty(n.ToString()).GetString() != Hash(path)) throw new InvalidDataException("SOURCE_HASH_MISMATCH:" + n);
        var sources = new Dictionary<int, GatherSource>
        {
            [8] = GatherUnits.Read(paths[8], "T8", DocumentTrial.Outline(args[4])),
            [9] = GatherUnits.Read(paths[9], "T9", DocumentTrial.Outline(args[5])),
        };
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var jobs = root.GetProperty("results").EnumerateArray().Select(r => new Job(r.GetProperty("Index").GetInt32(),
            r.GetProperty("Month").GetInt32(), r.GetProperty("Phase").GetString()!, r.GetProperty("Topic").GetString()!,
            r.GetProperty("Goal").GetString()!, r.GetProperty("Scope").Deserialize<GatherScopeResult>(opts)!))
            .Where(j => j.Scope.Repair.Remaining.Count > 0).ToArray();
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(Path.Combine(output, "calls"));
        using var provider = new GatherOpenRouterModel(key, maxCalls: 24, ceilingUsd: 1.0, diagnosticsPath: Path.Combine(output, "provider-events.jsonl"));
        using var model = new MonthlyQwenTrial.RecordedModel(provider, Path.Combine(output, "calls"), concurrency, opts);
        var results = new Result[jobs.Length];
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await Parallel.ForEachAsync(Enumerable.Range(0, jobs.Length), new ParallelOptions { MaxDegreeOfParallelism = concurrency }, async (i, ct) =>
        {
            var job = jobs[i];
            var repair = await GatherQuoteRepair.RepairAsync(job.Goal, job.Scope.Repair.Remaining, [sources[job.Month]], model, ct);
            var facts = repair.Decisions.Where(d => d.Fact is not null).Select(d => d.Fact!).ToArray();
            var run = new GatherRun(facts, [], [], [], [], sources[job.Month].Gaps, 1);
            var review = await GatherBatchedReview.ReviewAsync(job.Goal, run, [sources[job.Month]], model, 10, 2, ct);
            results[i] = new Result(job.Index, job.Month, job.Phase, job.Topic, repair, review);
            await File.WriteAllTextAsync(Path.Combine(output, $"group-{job.Index:00}.json"), JsonSerializer.Serialize(results[i], opts), ct);
        });
        foreach (var (n, path) in paths)
            if (root.GetProperty("hashes").GetProperty(n.ToString()).GetString() != Hash(path)) throw new InvalidDataException("SOURCE_CHANGED:" + n);
        await File.WriteAllTextAsync(Path.Combine(output, "repair.json"), JsonSerializer.Serialize(new
        {
            previousSha256 = Hash(args[1]), sourceHashes = paths.ToDictionary(x => x.Key, x => Hash(x.Value)), model = provider.CheckpointIdentity,
            repairVersion = GatherQuoteRepair.Version, elapsedSeconds = clock.Elapsed.TotalSeconds, results, attempts = provider.Attempts,
        }, opts));
        Console.WriteLine($"groups={results.Length} original={jobs.Sum(j => j.Scope.Repair.Remaining.Count)} repaired={results.Sum(r => r.Repair.Decisions.Count(d => d.Status == "REPAIRED"))} " +
            $"remaining={results.Sum(r => r.Repair.Remaining.Count)} calls={provider.Attempts.Count} seconds={clock.Elapsed.TotalSeconds:F1}");
        return results.All(r => r.Repair.Remaining.Count == 0 && r.Review.Failures.Count == 0 && r.Review.Review.Verdicts.All(v => v.Decision != "UNSURE")) ? 0 : 1;
    }
    sealed record Job(int Index, int Month, string Phase, string Topic, string Goal, GatherScopeResult Scope);
    sealed record Result(int Index, int Month, string Phase, string Topic, GatherQuoteRepairResult Repair, GatherBatchedReviewResult Review);
}
