using System.Text.Json;
using System.Text.Encodings.Web;
using DocxHeaderExtractor.HeadingAgent.Scan;

namespace DocxHeaderExtractor.App;

/// <summary>What a scan run needs from its host: where the evidence goes and the preflight and selection settings.</summary>
public sealed record ScanRunOptions(string EvidenceRoot, int? Jobs, bool Vision, bool ReuseSelection, bool ParagraphUnits, bool SegmentedLines);

/// <summary>The scan heading route as a host runs it: the agent's selector with the composition root's preflight settings.</summary>
public sealed class ScanExtractor(ScanRunOptions options, Action<ModelReplyView>? onModelReply = null)
{
    readonly ScanSelector selector = new(options.EvidenceRoot,
        parallelism: options.Jobs,
        vision: options.Vision,
        reuseSelection: options.ReuseSelection,
        paragraphUnits: options.ParagraphUnits,
        segmentedLines: options.SegmentedLines,
        onModelReply: ModelReplyViews.Adapt(onModelReply),
        transcriptionOptions: options.Jobs is { } jobs ? new ScanTranscriptionPreflight.Options(Parallelism: jobs) : null,
        visionOptions: options.Jobs is { } seeJobs ? new ScanVisionPreflight.Options(MaxReadingsPerPage: 0, Parallelism: seeJobs) : null);

    public Task<ScanHeadingResult> ExtractAsync(string file, string key, CancellationToken ct) => selector.ExtractAsync(file, key, ct);
}

/// <summary>The lines a scan evaluation prints, and whether the run counts as failed.</summary>
public sealed record ScanEvaluationOutcome(IReadOnlyList<string> Lines, bool Failed);

/// <summary>The scan evaluations a host runs: the reading accuracy of a scan's lines, and the headings of a result against a gold.</summary>
public static class ScanEvaluations
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static ScanEvaluationOutcome TextEval(string root, string goldPath, string file, string key)
    {
        var gold = ScanTextEvaluation.ReadGold(goldPath);
        var report = new ScanTextEvaluation(root).Run(file, gold, key);
        var lines = new List<string> { $"{"Cách đọc",-28} {"Đúng hết dòng",14} {"CER",8}" };
        foreach (var s in report.Scores)
            lines.Add($"{s.Method,-28} {s.Exact,6}/{s.Lines,-3} ({100.0 * s.Exact / s.Lines,3:0}%) {100 * s.CharacterErrorRate,7:0.00}%");
        foreach (var (method, c) in report.MethodCosts ?? new Dictionary<string, ScanTextEvaluation.MethodCost>())
            lines.Add($"  {method}: {c.Seconds:0.0}s, {c.Calls} POST, {c.CostUsd:0.0000} USD");
        foreach (var (method, bytes) in report.CropBytes)
            lines.Add($"  ảnh cắt {method}: {bytes / 1024.0:0.0} KB cho {report.Lines.Count} dòng ({bytes / 1024.0 / report.Lines.Count:0.00} KB/dòng)");
        lines.Add($"{report.ProviderCalls} POST, {report.ProviderCostUsd:0.0000} USD; lỗi: {(report.Failures.Count == 0 ? "không" : string.Join(", ", report.Failures))}");
        lines.Add($"báo cáo: {Path.Combine(root, "eval", report.SourceSha256[..16] + "-scan-text-eval.json")}");
        var failed = report.Scores.Any(s => s.Method.StartsWith("vlm", StringComparison.Ordinal) && s.Exact == 0 && report.Failures.Count > 0);
        return new(lines, failed);
    }

    /// <summary>Scores a scan result against the gold; the report is also written to <paramref name="outputPath"/> when one is given.</summary>
    public static IReadOnlyList<string> HeadingEval(string goldPath, string resultPath, string? outputPath)
    {
        var gold = ScanHeadingEvaluation.ReadGold(goldPath);
        var r = ScanHeadingEvaluation.Score(gold, ScanHeadingEvaluation.ReadResult(resultPath));
        var lines = new List<string>
        {
            $"Đủ (recall):        {r.RequiredFound}/{r.Required} heading bắt buộc ({100 * r.Recall:0.0}%), thêm {r.AcceptableFound} heading chấp nhận được",
            $"Đúng (precision):   {r.SelectedMatched}/{r.Selected} heading chọn ra là heading ({100 * r.Precision:0.0}%), {r.FalsePositives} chọn thừa, {r.SplitParts} phần bị tách",
            $"Chữ đúng nguyên văn: {r.TextExact}/{r.RequiredFound + r.AcceptableFound}",
        };
        lines.AddRange(r.Missed.Select(m => $"  thiếu  tr.{m.Page}: {m.Text}"));
        lines.AddRange(r.Extra.Select(m => $"  thừa   tr.{m.Page} #{m.Index} ({m.Kind}): {m.Text}"));
        lines.AddRange(r.NotExact.Select(m => $"  chữ khác tr.{m.Page} #{m.Index}: {m.Text}"));
        if (outputPath is not null)
            File.WriteAllText(outputPath, JsonSerializer.Serialize(r, Json));
        return lines;
    }
}
