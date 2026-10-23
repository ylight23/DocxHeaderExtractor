using System.Text.Json;
using DocxHeaderExtractor.App;

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
        var extractor = new ScanExtractor(
            new ScanRunOptions(EvidenceRoot: root, Jobs: o.Jobs, Vision: o.Vision, ReuseSelection: !o.FreshSelection,
                ParagraphUnits: o.ParagraphUnits, SegmentedLines: o.SegmentedLines),
            o.ShowRawOutput ? r => ModelReplyPrinter.Print(r) : null);
        var incomplete = false;
        foreach (var file in Inputs.Expand(o.Inputs))
        {
            if (!o.Quiet) Console.Error.WriteLine($"» {Path.GetFileName(file)}");
            var result = await extractor.ExtractAsync(file, key, ct);
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
        var outcome = ScanEvaluations.TextEval(o.OutputPath ?? ".dhx-evidence", o.GoldPath, o.Inputs[0], key);
        foreach (var line in outcome.Lines) Console.WriteLine(line);
        return outcome.Failed ? 1 : 0;
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
        foreach (var line in ScanEvaluations.HeadingEval(o.GoldPath, o.Inputs[0], o.OutputPath)) Console.WriteLine(line);
        return 0;
    }
}
