using System.Text.Json;

namespace DocxHeaderExtractor.Cli.Commands;

/// <summary>The scan commands: <c>scan-extract</c>, <c>scan-text-eval</c>, <c>scan-heading-eval</c> and <c>scan-evidence-json</c>.</summary>
internal static class ScanCommands
{
    // The heading route of a scanned DOCX: OCR and vision preflight into the evidence store, then the agent decides. Prints the result as JSON.
    public static async Task<int> ExtractAsync(CommandLineOptions o, CancellationToken ct)
    {
        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) { Console.Error.WriteLine("Chưa có OPENROUTER_API_KEY."); return 2; }
        var root = o.OutputPath ?? ".dhx-evidence";
        var selector = new DocxHeaderExtractor.HeadingAgent.Scan.ScanSelector(root, parallelism: o.Jobs, vision: o.Vision, reuseSelection: !o.FreshSelection, paragraphUnits: o.ParagraphUnits, segmentedLines: o.SegmentedLines,
            onModelReply: o.ShowRawOutput ? r => ModelReplyPrinter.Print(r) : null,
            transcriptionOptions: o.Jobs is { } jobs ? new DocxHeaderExtractor.HeadingAgent.Scan.ScanTranscriptionPreflight.Options(Parallelism: jobs) : null,
            visionOptions: o.Jobs is { } seeJobs ? new DocxHeaderExtractor.HeadingAgent.Scan.ScanVisionPreflight.Options(MaxReadingsPerPage: 0, Parallelism: seeJobs) : null);
        var incomplete = false;
        foreach (var file in Inputs.Expand(o.Inputs))
        {
            if (!o.Quiet) Console.Error.WriteLine($"» {Path.GetFileName(file)}");
            var result = await selector.ExtractAsync(file, key, ct);
            incomplete |= result.UnitsComplete != result.UnitsTotal || result.UnitFailures.Count > 0
                || result.TranscriptionFailures.Count > 0 || result.VisionFailures.Count > 0 || result.OcrGaps.Count > 0 || result.PartsRejected > 0;
            if (!o.Quiet)
                Console.Error.WriteLine($"  {result.Headings.Count} heading, {result.UnitsComplete}/{result.UnitsTotal} unit, {result.ProviderCalls} POST, " +
                    $"{result.ProviderCostUsd:0.0000} USD, " + (o.Vision ? $"VLM {result.VisionPagesObserved} trang ({result.VisionFailures.Count} lỗi), " : "kiểu chữ đo trên ảnh, ") +
                    $"chép {result.TranscriptionsAccepted}/{result.TranscribedLines} dòng dùng được ({result.TranscriptionFailures.Count} lỗi), " +
                    $"tiền xử lý {result.PreflightCalls} POST {result.PreflightCostUsd:0.0000} USD");
            if (!o.Quiet && result.StageSeconds.Count > 0)
                Console.Error.WriteLine("  thời gian: " + string.Join(", ", result.StageSeconds.Select(kv => $"{kv.Key} {kv.Value:0.0}s")));
            if (!o.Quiet && result.CallSeconds.Count > 0)
            {
                Console.Error.WriteLine($"  cắt ảnh để chép: {result.TranscriptionCropSeconds:0.0}s");
                foreach (var (stage, c) in result.CallSeconds)
                    Console.Error.WriteLine($"  gọi API {stage}: {c.Calls} lần, trung vị {c.P50Seconds:0.0}s, p90 {c.P90Seconds:0.0}s, lâu nhất {c.MaxSeconds:0.0}s");
                Console.Error.WriteLine("  đoạn hội thoại lâu nhất: " + string.Join(", ", result.SlowestUnits.Select(u => $"{u.UnitId} {u.Seconds:0}s/{u.Calls} lần")));
            }
            Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }
        return incomplete ? 1 : 0;
    }

    // Reading accuracy of a scan's lines against a gold transcription: the OCR views and the vision model transcribing blind. Prints the scores.
    public static int TextEval(CommandLineOptions o)
    {
        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) { Console.Error.WriteLine("Chưa có OPENROUTER_API_KEY."); return 2; }
        if (o.GoldPath is null || o.Inputs.Count != 1) { Console.Error.WriteLine("Cách dùng: dhx scan-text-eval <file.docx> --gold <gold.json>"); return 2; }
        var root = o.OutputPath ?? ".dhx-evidence";
        var gold = DocxHeaderExtractor.HeadingAgent.Scan.ScanTextEvaluation.ReadGold(o.GoldPath);
        var report = new DocxHeaderExtractor.HeadingAgent.Scan.ScanTextEvaluation(root).Run(o.Inputs[0], gold, key);
        Console.WriteLine($"{"Cách đọc",-28} {"Đúng hết dòng",14} {"CER",8}");
        foreach (var s in report.Scores)
            Console.WriteLine($"{s.Method,-28} {s.Exact,6}/{s.Lines,-3} ({100.0 * s.Exact / s.Lines,3:0}%) {100 * s.CharacterErrorRate,7:0.00}%");
        foreach (var (method, c) in report.MethodCosts ?? new Dictionary<string, DocxHeaderExtractor.HeadingAgent.Scan.ScanTextEvaluation.MethodCost>())
            Console.WriteLine($"  {method}: {c.Seconds:0.0}s, {c.Calls} POST, {c.CostUsd:0.0000} USD");
        foreach (var (method, bytes) in report.CropBytes)
            Console.WriteLine($"  ảnh cắt {method}: {bytes / 1024.0:0.0} KB cho {report.Lines.Count} dòng ({bytes / 1024.0 / report.Lines.Count:0.00} KB/dòng)");
        Console.WriteLine($"{report.ProviderCalls} POST, {report.ProviderCostUsd:0.0000} USD; lỗi: {(report.Failures.Count == 0 ? "không" : string.Join(", ", report.Failures))}");
        Console.WriteLine($"báo cáo: {Path.Combine(root, "eval", report.SourceSha256[..16] + "-scan-text-eval.json")}");
        return report.Scores.Any(s => s.Method.StartsWith("vlm", StringComparison.Ordinal) && s.Exact == 0 && report.Failures.Count > 0) ? 1 : 0;
    }

    // The evidence a scan stored (evidence.msgpack) as JSON on stdout, to read while debugging. Reads files only; sends nothing.
    public static int EvidenceJson(CommandLineOptions o)
    {
        if (o.Inputs.Count != 1) { Console.Error.WriteLine("Cách dùng: dhx scan-evidence-json <evidence.msgpack>  (JSON in ra stdout; ghi file bằng > tên-file.json)"); return 2; }
        Console.WriteLine(DocxHeaderExtractor.Sources.Scan.ScanEvidenceJson.Read(o.Inputs[0]));
        return 0;
    }

    // The headings of a scan result against a gold of which headings the document has. Reads files only; sends nothing.
    public static int HeadingEval(CommandLineOptions o)
    {
        if (o.GoldPath is null || o.Inputs.Count != 1) { Console.Error.WriteLine("Cách dùng: dhx scan-heading-eval <ket-qua.json> --gold <gold.json>"); return 2; }
        var gold = DocxHeaderExtractor.HeadingAgent.Scan.ScanHeadingEvaluation.ReadGold(o.GoldPath);
        var r = DocxHeaderExtractor.HeadingAgent.Scan.ScanHeadingEvaluation.Score(gold, DocxHeaderExtractor.HeadingAgent.Scan.ScanHeadingEvaluation.ReadResult(o.Inputs[0]));
        Console.WriteLine($"Đủ (recall):        {r.RequiredFound}/{r.Required} heading bắt buộc ({100 * r.Recall:0.0}%), thêm {r.AcceptableFound} heading chấp nhận được");
        Console.WriteLine($"Đúng (precision):   {r.SelectedMatched}/{r.Selected} heading chọn ra là heading ({100 * r.Precision:0.0}%), {r.FalsePositives} chọn thừa, {r.SplitParts} phần bị tách");
        Console.WriteLine($"Chữ đúng nguyên văn: {r.TextExact}/{r.RequiredFound + r.AcceptableFound}");
        foreach (var m in r.Missed) Console.WriteLine($"  thiếu  tr.{m.Page}: {m.Text}");
        foreach (var m in r.Extra) Console.WriteLine($"  thừa   tr.{m.Page} #{m.Index} ({m.Kind}): {m.Text}");
        foreach (var m in r.NotExact) Console.WriteLine($"  chữ khác tr.{m.Page} #{m.Index}: {m.Text}");
        if (o.OutputPath is { } output)
            File.WriteAllText(output, JsonSerializer.Serialize(r, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        return 0;
    }
}
