using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using DocxHeaderExtractor.Cli;
using DocxHeaderExtractor.V6Runner;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Projection;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Infrastructure.AI;

Console.OutputEncoding = Encoding.UTF8;

CommandLineOptions options;
try
{
    options = CommandLineOptions.Parse(args);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Lỗi tham số: {ex.Message}");
    Console.Error.WriteLine("Chạy `dhx help` để xem hướng dẫn.");
    return 2;
}
if (options.ShowHelp)
{
    Console.WriteLine(CommandLineOptions.HelpText);
    return 0;
}
// `info` tự dò mô hình – không cần đầu vào.
if (options.Inputs.Count == 0 && options.Command is not "info")
{
    Console.Error.WriteLine("Chưa chỉ định file đầu vào.");
    return 2;
}
if (!options.Quiet) options.Pipeline.Log = m => Console.Error.WriteLine($"  {m}");
// Explicit raw diagnostics remain available with --quiet; never mix them into result stdout.
options.ConfigureProviderDiagnostics(m => Console.Error.WriteLine($"  {m}"));

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    return options.Command switch
    {
        "info" => RunModelInfo(options),
        "scan-ocr" => RunScanOcr(options),
        "scan-extract" => await RunScanExtractAsync(options, cts.Token),
        "scan-text-eval" => RunScanTextEval(options),
        _ => await RunExtractAsync(options, cts.Token),
    };
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Đã huỷ.");
    return 130;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Lỗi: {ex.Message}");
    if (Environment.GetEnvironmentVariable("DHX_DEBUG") == "1")
        Console.Error.WriteLine(ex);
    return 1;
}
// OCR preflight: writes the evidence store of each scanned DOCX and prints what it holds. It changes no document and decides nothing.
static int RunScanOcr(CommandLineOptions o)
{
    // The same store scan-extract reads (<root>/ocr), so its OCR is not done twice.
    var root = Path.Combine(o.OutputPath ?? ".dhx-evidence", DocxHeaderExtractor.HeadingAgent.Scan.ScanHeadingAgentSelector.OcrFolder);
    var preflight = new DocxHeaderExtractor.DocumentProcessing.Source.Scan.ScanOcrPreflight(root);
    foreach (var file in ExpandInputs(o.Inputs))
    {
        var integrity = DocxHeaderExtractor.DocumentProcessing.Source.Docx.DocxSourceIntegrity.Read(file);
        Console.WriteLine($"» {Path.GetFileName(file)}: {integrity.Kind}, {integrity.PageImages} ảnh trang, {integrity.TextChars} ký tự OOXML");
        if (integrity.PageImages == 0) continue;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var evidence = preflight.Run(file);
        foreach (var view in evidence.Views)
        {
            var lines = view.Pages.SelectMany(p => p.Lines).ToArray();
            var words = lines.SelectMany(l => l.Words).ToArray();
            Console.WriteLine($"  {view.Spec.ViewId}: {view.Pages.Count} trang, {lines.Length} dòng, {words.Length} từ, confidence TB {words.Average(w => w.Confidence):0.0}, " +
                $"từ <60: {words.Count(w => w.Confidence < 60)}, dòng chồng dòng khác: {lines.Count(l => l.OverlapsOtherLine)}");
        }
        foreach (var gap in evidence.Gaps) Console.WriteLine($"  thiếu view {gap.ViewId}: {gap.Reason}");
        if (evidence.Views.Count == 2)
        {
            var aligned = DocxHeaderExtractor.DocumentProcessing.Source.Scan.OcrViewComparer.Compare(evidence.Views[0], evidence.Views[1]);
            Console.WriteLine($"  so sánh 2 view: {aligned.Count} dòng, {aligned.Count(a => a.LineB is null)} không ghép được, {aligned.Count(a => a.LineB is not null && !a.Identical)} đọc khác nhau ({aligned.Sum(a => a.Differences.Count)} chỗ, đều UNVERIFIED)");
        }
        Console.WriteLine($"  evidence: {root} ({watch.Elapsed.TotalSeconds:0.0}s)");
    }
    return 0;
}

// The heading route of a scanned DOCX: OCR and vision preflight into the evidence store, then the agent decides. Prints the result as JSON.
static async Task<int> RunScanExtractAsync(CommandLineOptions o, CancellationToken ct)
{
    var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
    if (string.IsNullOrWhiteSpace(key)) { Console.Error.WriteLine("Chưa có OPENROUTER_API_KEY."); return 2; }
    var root = o.OutputPath ?? ".dhx-evidence";
    var selector = new DocxHeaderExtractor.HeadingAgent.Scan.ScanHeadingAgentSelector(root, parallelism: o.Jobs,
        onModelReply: o.ShowRawOutput ? r => PrintModelReply(r) : null,
        transcriptionOptions: o.Jobs is { } jobs ? new DocxHeaderExtractor.HeadingAgent.Scan.ScanTranscriptionPreflight.Options(Parallelism: jobs) : null,
        visionOptions: o.Jobs is { } seeJobs ? new DocxHeaderExtractor.HeadingAgent.Scan.ScanVisionPreflight.Options(MaxReadingsPerPage: 0, Parallelism: seeJobs) : null);
    foreach (var file in ExpandInputs(o.Inputs))
    {
        if (!o.Quiet) Console.Error.WriteLine($"» {Path.GetFileName(file)}");
        var result = await selector.ExtractAsync(file, key, ct);
        if (!o.Quiet)
            Console.Error.WriteLine($"  {result.Headings.Count} heading, {result.UnitsComplete}/{result.UnitsTotal} unit, {result.ProviderCalls} POST, " +
                $"{result.ProviderCostUsd:0.0000} USD, VLM {result.VisionPagesObserved} trang ({result.VisionFailures.Count} lỗi), " +
                $"chép {result.TranscriptionsAccepted}/{result.TranscribedLines} dòng dùng được ({result.TranscriptionFailures.Count} lỗi), " +
                $"tiền xử lý {result.PreflightCalls} POST {result.PreflightCostUsd:0.0000} USD");
        if (!o.Quiet && result.StageSeconds.Count > 0)
            Console.Error.WriteLine("  thời gian: " + string.Join(", ", result.StageSeconds.Select(kv => $"{kv.Key} {kv.Value:0.0}s")));
        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }
    return 0;
}

// Reading accuracy of a scan's lines against a gold transcription: the OCR views and the vision model transcribing blind. Prints the scores.
static int RunScanTextEval(CommandLineOptions o)
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

// Keep the top-level CLI source terminated by a single newline.
static async Task<int> RunExtractAsync(CommandLineOptions o, CancellationToken ct)
{
    if (!o.Pipeline.DisableLlm && o.Provider.Backend == InferenceBackend.Local &&
        string.IsNullOrWhiteSpace(o.Provider.LocalModel.ModelPath))
    {
        var found = ModelLocator.Locate();
        if (found is null)
        {
            Console.Error.WriteLine(
                """
                Chưa có mô hình. Chỉ định bằng --model <đường-dẫn.gguf>, đặt biến môi trường DHX_MODEL,
                hoặc đặt file .gguf vào thư mục ./models. Xem models/README.md để tải
                Llama-3.2-3B-Instruct-Q4_K_M.gguf. Dùng --no-llm nếu chỉ muốn chạy bằng luật OpenXML.
                """);
            return 2;
        }
        o.Provider.LocalModel.ModelPath = found;
    }

    var files = ExpandInputs(o.Inputs);
    if (files.Count == 0)
    {
        Console.Error.WriteLine("Không tìm thấy file nào khớp đầu vào.");
        return 2;
    }
    if (files.Count > 1 && o.WritebackPath is not null)
    {
        Console.Error.WriteLine("Chỉ dùng --write-docx khi xử lý đúng một tài liệu.");
        return 2;
    }

    // Chạy thẳng tuyến V6 (PDF/DOCX). Không qua harness: không có consent gửi dữ liệu ra ngoài,
    // không có validator, không có guardrail đích ghi (chủ dự án chấp nhận, 2026-10-15).
    using var runner = new V6DocumentRunner(o.Pipeline, new InferenceTransportFactory(o.Provider),
        o.ShowRawOutput ? r => PrintModelReply(r) : null);
    var outputs = new List<string>();
    int failed = 0;

    foreach (var file in files)
    {
        if (!o.Quiet) Console.Error.WriteLine($"» {Path.GetFileName(file)}");
        try
        {
            var execution = await runner.ExtractAsync(file, ct: ct);
            var outline = execution.Outline;
            if (!o.Quiet)
                Console.Error.WriteLine($"  {outline.Headings.Count} heading từ {outline.ParagraphCount} đoạn.");

            if (o.WritebackPath is not null)
            {
                var writeback = V6DocumentRunner.WriteOutline(
                    file, outline, o.WritebackPath, o.Pipeline.Extraction,
                    applyHeadingStyles: o.WritebackHeadingStyles, overwrite: o.WritebackOverwrite);
                if (!o.Quiet)
                    Console.Error.WriteLine($"  Đã ghi {writeback.Applied} heading, bỏ qua {writeback.Skipped} mục: {Path.GetFileName(writeback.OutputPath)}");
            }
            outputs.Add(OutlineFormatter.Format(outline, o.Format));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"  {ex.Message}");
            if (o.Verbose) Console.Error.WriteLine(ex.ToString());
            failed++;
        }
    }

    var text = string.Join(Environment.NewLine, outputs);
    if (o.OutputPath is { } path)
    {
        // Không tài liệu nào qua được thì không ghi. Mã thoát đã khác 0 từ trước, nhưng lần chạy
        // hỏng vẫn tạo file rỗng — và nếu đường dẫn đó đang giữ kết quả lần trước thì nó vừa bị
        // xoá. Script đọc file thay vì đọc mã thoát sẽ hiểu thành "tài liệu không có heading nào".
        if (outputs.Count == 0)
            Console.Error.WriteLine(
                $"Không ghi {path}: cả {failed} tài liệu đều lỗi nên không có kết quả nào để ghi.");
        else
        {
            await File.WriteAllTextAsync(path, text, new UTF8Encoding(false), ct);
            if (!o.Quiet)
                Console.Error.WriteLine(failed == 0
                    ? $"Đã ghi: {path}"
                    : $"Đã ghi {outputs.Count}/{files.Count} tài liệu vào {path} — {failed} tài liệu lỗi và KHÔNG có trong file.");
        }
    }
    else if (outputs.Count > 0)
    {
        Console.WriteLine(text);
    }

    return failed == 0 ? 0 : 1;
}

/// <summary>
/// Yêu cầu gửi harness cho một tài liệu. Chỉ đồng ý gửi dữ liệu ra ngoài khi backend là dịch vụ
/// từ xa và người dùng không chạy <c>--no-llm</c>.
/// </summary>
// --show-raw: what the model streamed and wrote for one reply. Reasoning and content are the model's own text.
static void PrintModelReply(DocxHeaderExtractor.HeadingAgent.ModelReply r)
{
    Console.Error.WriteLine($"--- [{r.Unit}] lượt {r.Turn} ({r.FinishReason ?? "—"}) ---");
    if (!string.IsNullOrEmpty(r.Reasoning)) Console.Error.WriteLine($"[suy luận]\n{r.Reasoning}");
    foreach (var c in r.ToolCalls) Console.Error.WriteLine($"[gọi tool] {c.Name} {c.Arguments}");
    foreach (var x in r.ToolResults) Console.Error.WriteLine($"[kết quả tool trước lượt này] {x.Content}");
    if (!string.IsNullOrEmpty(r.Content)) Console.Error.WriteLine($"[nội dung]\n{r.Content}");
}

static int RunModelInfo(CommandLineOptions o)
{
    var path = o.Inputs.FirstOrDefault() ?? o.Provider.LocalModel.ModelPath;
    if (string.IsNullOrWhiteSpace(path)) path = ModelLocator.Locate() ?? "";
    if (!File.Exists(path))
    {
        Console.Error.WriteLine("Không tìm thấy file .gguf.");
        return 2;
    }

    DocxHeaderExtractor.Infrastructure.AI.LlamaInferenceTransport.ConfigureNativeLogging(o.Provider.LocalModel.VerboseNativeLog);

    var fi = new FileInfo(path);
    Console.WriteLine($"File   : {fi.FullName}");
    Console.WriteLine($"Kích cỡ: {fi.Length / 1024.0 / 1024.0:0.0} MB");

    using var weights = LLama.LLamaWeights.LoadFromFile(
        new LLama.Common.ModelParams(path) { ContextSize = 256, GpuLayerCount = 0, VocabOnly = true });

    Console.WriteLine($"Tham số: {weights.ParameterCount / 1_000_000_000.0:0.00} B");

    // VocabOnly = true nên weights.ContextSize là 0; giá trị thật nằm trong metadata.
    var trainCtx = weights.Metadata.FirstOrDefault(k => k.Key.EndsWith(".context_length")).Value;
    Console.WriteLine($"Ngữ cảnh tối đa: {trainCtx ?? "?"}");
    Console.WriteLine($"Chat template  : {(weights.Metadata.ContainsKey("tokenizer.chat_template") ? "có" : "không (dùng template Llama 3 dựng tay)")}");
    Console.WriteLine();
    foreach (var kv in weights.Metadata.OrderBy(k => k.Key))
    {
        var v = kv.Value.Length > 120 ? kv.Value[..120] + "…" : kv.Value;
        Console.WriteLine($"  {kv.Key} = {v.ReplaceLineEndings(" ")}");
    }
    return 0;
}

static List<string> ExpandInputs(IEnumerable<string> inputs)
{
    var files = new List<string>();
    foreach (var input in inputs)
    {
        if (File.Exists(input)) { files.Add(Path.GetFullPath(input)); continue; }

        if (Directory.Exists(input))
        {
            files.AddRange(Directory.EnumerateFiles(input, "*.*", SearchOption.TopDirectoryOnly)
                .Where(IsSupported));
            continue;
        }

        var dir = Path.GetDirectoryName(input);
        var pattern = Path.GetFileName(input);
        dir = string.IsNullOrEmpty(dir) ? Directory.GetCurrentDirectory() : dir;

        if (Directory.Exists(dir) && (pattern.Contains('*') || pattern.Contains('?')))
            files.AddRange(Directory.EnumerateFiles(dir, pattern).Where(IsSupported));
        else
            Console.Error.WriteLine($"Bỏ qua (không tồn tại): {input}");
    }
    return files.Distinct().OrderBy(f => f).ToList();

    // A glob filter, not a routing decision: the lane is still chosen from the bytes. This only
    // decides which files a wildcard expands to, and leaving .pdf out of it meant the PDF lane was
    // unreachable from the CLI however the file was named.
    static bool IsSupported(string f) =>
        Path.GetExtension(f).ToLowerInvariant()
            is ".docx" or ".docm" or ".doc" or ".rtf" or ".odt" or ".pdf";
}

static class ModelLocator
{
    public static string? Locate()
    {
        var env = Environment.GetEnvironmentVariable("DHX_MODEL");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;

        foreach (var settings in SettingsCandidates())
        {
            if (!File.Exists(settings)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(settings));
                if (doc.RootElement.TryGetProperty("modelPath", out var mp))
                {
                    var p = mp.GetString();
                    if (!string.IsNullOrWhiteSpace(p))
                    {
                        var full = Path.IsPathRooted(p)
                            ? p
                            : Path.Combine(Path.GetDirectoryName(settings)!, p);
                        if (File.Exists(full)) return Path.GetFullPath(full);
                    }
                }
            }
            catch (JsonException) { /* file cấu hình hỏng – bỏ qua */ }
        }

        foreach (var dir in ModelDirCandidates())
        {
            if (!Directory.Exists(dir)) continue;
            var ggufs = Directory.GetFiles(dir, "*.gguf");
            var preferred = ggufs.FirstOrDefault(f =>
                Path.GetFileName(f).Contains("llama-3.2-3b", StringComparison.OrdinalIgnoreCase));
            if (preferred is not null) return preferred;
            if (ggufs.Length >= 1) return ggufs[0];
        }

        return null;
    }

    private static IEnumerable<string> SettingsCandidates()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        yield return Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json");
    }

    private static IEnumerable<string> ModelDirCandidates()
    {
        yield return Path.Combine(Directory.GetCurrentDirectory(), "models");
        yield return Path.Combine(AppContext.BaseDirectory, "models");
        // bin/Debug/net9.0 → lên gốc repo
        yield return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "models"));
    }
}
// EOF
