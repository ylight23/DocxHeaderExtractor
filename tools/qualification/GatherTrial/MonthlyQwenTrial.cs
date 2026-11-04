using System.Security.Cryptography;
using System.Text.Json;
using DocxHeaderExtractor.App;
using DocxHeaderExtractor.DocumentProcessing.Gather;

// Qualification adapter: it consumes the already recorded section map, not a new production DOCX prompt.
internal static class MonthlyQwenTrial
{
    public static async Task<int> RunAsync(string[] args)
    {
        var runPipeline = args.Length > 0 && args[0] == "monthly-qwen-pipeline";
        if (args.Length is not (9 or 10) || (args.Length == 10 && !runPipeline))
            throw new ArgumentException("monthly-qwen-review|monthly-qwen-pipeline <manifest> <target> <source8> <source9> <outline8> <outline9> <output-dir> <concurrency> [seed-checkpoints]");
        var output = Path.GetFullPath(args[7]);
        if (Directory.Exists(output)) throw new IOException("OUTPUT_DIRECTORY_ALREADY_EXISTS");
        var concurrency = int.Parse(args[8]);
        if (concurrency is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(concurrency));
        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("OPENROUTER_API_KEY_MISSING");
        using var doc = JsonDocument.Parse(File.ReadAllText(args[1]));
        var manifest = doc.RootElement;
        static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        var paths = new Dictionary<int, string> { [8] = Path.GetFullPath(args[3]), [9] = Path.GetFullPath(args[4]), [10] = Path.GetFullPath(args[2]) };
        var hashes = paths.ToDictionary(x => x.Key, x => Hash(x.Value));
        foreach (var (month, hash) in hashes)
            if (manifest.GetProperty("source_sha256").GetProperty(month.ToString()).GetString() != hash)
                throw new InvalidDataException("SOURCE_HASH_MISMATCH:" + month);
        var sources = new Dictionary<int, GatherSource>
        {
            [8] = GatherUnits.Read(paths[8], "T8", DocumentTrial.Outline(args[5])),
            [9] = GatherUnits.Read(paths[9], "T9", DocumentTrial.Outline(args[6])),
        };
        foreach (var (month, path) in paths)
            if (Hash(path) != hashes[month]) throw new InvalidDataException("SOURCE_CHANGED_DURING_READ");
        var units = sources.ToDictionary(x => x.Key, x => x.Value.Units.ToDictionary(u => u.Location, StringComparer.Ordinal));
        var jobs = new List<MonthlyJob>();
        foreach (var group in manifest.GetProperty("groups").EnumerateArray())
        {
            var month = group.GetProperty("source_month").GetInt32();
            var phase = group.GetProperty("target_phase").GetString()!;
            var topic = group.GetProperty("target_topic").GetString()!;
            var parts = new List<GatheredFact>();
            foreach (var p in group.GetProperty("source_paragraphs").EnumerateArray())
            {
                var loc = $"body[1]/p[{p.GetInt32()}]";
                if (!units[month].TryGetValue(loc, out var unit)) throw new InvalidDataException("SOURCE_UNIT_MISSING:" + month + ":" + loc);
                var text = unit.Text.Trim();
                var start = unit.Text.IndexOf(text, StringComparison.Ordinal);
                if (text.Length == 0 || start < 0) throw new InvalidDataException("SOURCE_TEXT_INVALID:" + loc);
                parts.Add(new GatheredFact([new SourcedPart(unit.Id, unit.SourceId, unit.Location, "text", text, start, start + text.Length)], []));
            }
            jobs.Add(new MonthlyJob(jobs.Count, month, phase, topic, parts));
        }
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(Path.Combine(output, "calls"));
        var options = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        using var provider = new GatherOpenRouterModel(apiKey, maxCalls: 100, ceilingUsd: 2.0,
            diagnosticsPath: Path.Combine(output, "provider-events.jsonl"));
        using var recorded = new RecordedModel(provider, Path.Combine(output, "calls"), concurrency, options);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var results = new MonthlyResult[jobs.Count];
        var pipelineResults = new MonthlyPipelineResult[jobs.Count];
        await Parallel.ForEachAsync(jobs, new ParallelOptions { MaxDegreeOfParallelism = concurrency }, async (job, ct) =>
        {
            var period = job.Phase == "activities" ? $"hoạt động đã triển khai tháng {job.Month}/2026" : $"kế hoạch tháng {job.Month + 1}/2026 trong báo cáo tháng {job.Month}/2026";
            var goal = $"Tổng hợp đầy đủ thông tin liên quan đến mục {job.Topic}. Nguồn là {period}; " +
                "giữ riêng thời điểm của nguồn, không chuyển thông tin quá khứ thành dữ liệu hoặc kế hoạch của tháng hiện tại. " +
                "Những dòng giới thiệu nhóm nội dung liên quan cũng có thể giữ để bảo toàn ngữ cảnh.";
            if (runPipeline)
            {
                var selectedIds = job.Facts.SelectMany(f => f.Parts).Select(p => p.UnitId).ToHashSet(StringComparer.Ordinal);
                var source = sources[job.Month] with { Units = sources[job.Month].Units.Where(u => selectedIds.Contains(u.Id)).ToArray() };
                var checkpointDir = Path.Combine(output, "checkpoints", job.Index.ToString("00"));
                Directory.CreateDirectory(checkpointDir);
                if (args.Length == 10)
                {
                    var seedDir = Path.Combine(Path.GetFullPath(args[9]), job.Index.ToString("00"));
                    if (Directory.Exists(seedDir))
                        foreach (var seed in Directory.EnumerateFiles(seedDir, "*.json"))
                            File.Copy(seed, Path.Combine(checkpointDir, Path.GetFileName(seed)), overwrite: false);
                }
                var checkpoint = new GatherCheckpointStore(checkpointDir, provider.CheckpointIdentity);
                var scope = await GatherScopePipeline.RunAsync(goal, job.Topic, "", null, [source], recorded, recorded,
                    checkpoint, reviewBatchSize: 10, reviewConcurrency: 2, ct);
                var pipelineResult = new MonthlyPipelineResult(job.Index, job.Month, job.Phase, job.Topic, goal, job.Facts, scope, sources[job.Month].Gaps);
                pipelineResults[job.Index] = pipelineResult;
                await File.WriteAllTextAsync(Path.Combine(output, $"group-{job.Index:00}.json"), JsonSerializer.Serialize(pipelineResult, options), ct);
                return;
            }
            var run = new GatherRun(job.Facts, [], [], [], [], sources[job.Month].Gaps, 1);
            var review = await GatherBatchedReview.ReviewAsync(goal, run, [sources[job.Month]], recorded, batchSize: 10, concurrency: 2, ct);
            var result = new MonthlyResult(job.Index, job.Month, job.Phase, job.Topic, goal, job.Facts, review, sources[job.Month].Gaps);
            results[job.Index] = result;
            await File.WriteAllTextAsync(Path.Combine(output, $"group-{job.Index:00}.json"), JsonSerializer.Serialize(result, options), ct);
        });
        foreach (var (month, path) in paths)
            if (Hash(path) != hashes[month]) throw new InvalidDataException("SOURCE_CHANGED_DURING_REVIEW");
        var resultPath = Path.Combine(output, "review.json");
        if (runPipeline)
        {
            await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(new
            {
                hashes, manifestSha256 = Hash(args[1]), model = provider.CheckpointIdentity, elapsedSeconds = clock.Elapsed.TotalSeconds,
                candidateCount = jobs.Sum(j => j.Facts.Count), results = pipelineResults, attempts = provider.Attempts,
            }, options));
            Console.WriteLine($"groups={pipelineResults.Length} inputs={jobs.Sum(j => j.Facts.Count)} proposed={pipelineResults.Sum(r => r.Scope.Selection.Facts.Count)} " +
                $"kept={pipelineResults.Sum(r => r.Scope.ReviewedSection.Run.Facts.Count)} unproposed={pipelineResults.Sum(r => r.Scope.UnproposedUnitIds.Count)} " +
                $"calls={provider.Attempts.Count} seconds={clock.Elapsed.TotalSeconds:F1} result={resultPath}");
            return pipelineResults.All(r => r.Scope.Complete) ? 0 : 1;
        }
        await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(new
        {
            hashes, manifestSha256 = Hash(args[1]), model = provider.CheckpointIdentity, elapsedSeconds = clock.Elapsed.TotalSeconds,
            candidateCount = jobs.Sum(j => j.Facts.Count), results, attempts = provider.Attempts,
        }, options));
        Console.WriteLine($"groups={results.Length} facts={jobs.Sum(j => j.Facts.Count)} keep={results.Sum(r => r.Review.Review.Verdicts.Count(v => v.Decision == "KEEP"))} " +
            $"exclude={results.Sum(r => r.Review.Review.Verdicts.Count(v => v.Decision == "EXCLUDE"))} unsure={results.Sum(r => r.Review.Review.Verdicts.Count(v => v.Decision == "UNSURE"))} " +
            $"calls={provider.Attempts.Count} seconds={clock.Elapsed.TotalSeconds:F1} result={resultPath}");
        return results.All(r => r.Review.Failures.Count == 0 && r.Review.Review.Verdicts.All(v => v.Decision != "UNSURE")) ? 0 : 1;
    }

    sealed record MonthlyJob(int Index, int Month, string Phase, string Topic, IReadOnlyList<GatheredFact> Facts);
    sealed record MonthlyResult(int Index, int Month, string Phase, string Topic, string Goal, IReadOnlyList<GatheredFact> Facts,
        GatherBatchedReviewResult Review, IReadOnlyList<GatherGap> SourceGaps);
    sealed record MonthlyPipelineResult(int Index, int Month, string Phase, string Topic, string Goal, IReadOnlyList<GatheredFact> InputFacts,
        GatherScopeResult Scope, IReadOnlyList<GatherGap> SourceGaps);

    internal sealed class RecordedModel(IGatherModel inner, string output, int concurrency, JsonSerializerOptions options) : IGatherModel, IDisposable
    {
        readonly SemaphoreSlim gate = new(concurrency);
        public async Task<string> CompleteAsync(string system, string user, CancellationToken ct)
        {
            await gate.WaitAsync(ct);
            var id = Guid.NewGuid().ToString("N");
            try
            {
                await File.WriteAllTextAsync(Path.Combine(output, id + ".request.json"), JsonSerializer.Serialize(new { system, user }, options), ct);
                var response = await inner.CompleteAsync(system, user, ct);
                await File.WriteAllTextAsync(Path.Combine(output, id + ".response.json"), JsonSerializer.Serialize(new { response }, options), ct);
                return response;
            }
            catch (Exception ex)
            {
                await File.WriteAllTextAsync(Path.Combine(output, id + ".failure.json"), JsonSerializer.Serialize(new { error = ex.GetType().Name, ex.Message }, options), CancellationToken.None);
                throw;
            }
            finally { gate.Release(); }
        }
        public void Dispose() => gate.Dispose();
    }
}
