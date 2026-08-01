using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Cli;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Eval;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Output;
using DocxHeaderExtractor.Core.Pipeline;

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

// `sample`/`bench`/`eval` có đích mặc định, `info` tự dò mô hình – không cần đầu vào.
if (options.Inputs.Count == 0 && options.Command is not ("sample" or "info" or "bench" or "eval"))
{
    Console.Error.WriteLine("Chưa chỉ định file đầu vào.");
    return 2;
}

if (!options.Quiet) options.Pipeline.Log = m => Console.Error.WriteLine($"  {m}");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    return options.Command switch
    {
        "xml" => RunDumpXml(options),
        "info" => RunModelInfo(options),
        "sample" => RunSample(options),
        "bench" => RunBench(options),
        "eval" => await RunEvalAsync(options, cts.Token),
        "review" => await RunReviewAsync(options, cts.Token),
        "review-key" => RunReviewKey(options),
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

static async Task<int> RunExtractAsync(CommandLineOptions o, CancellationToken ct)
{
    if (!o.Pipeline.DisableLlm && o.Pipeline.Backend == InferenceBackend.Local &&
        string.IsNullOrWhiteSpace(o.Pipeline.Llama.ModelPath))
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
        o.Pipeline.Llama.ModelPath = found;
    }

    var files = ExpandInputs(o.Inputs);
    if (files.Count == 0)
    {
        Console.Error.WriteLine("Không tìm thấy file nào khớp đầu vào.");
        return 2;
    }

    using var pipeline = new HeaderExtractionPipeline(o.Pipeline);
    var outputs = new List<string>();
    int failed = 0;

    foreach (var file in files)
    {
        if (!o.Quiet) Console.Error.WriteLine($"» {Path.GetFileName(file)}");
        try
        {
            var outline = await pipeline.RunAsync(file, ct);
            if (!o.Quiet)
                Console.Error.WriteLine(
                    $"  Xong: {outline.Headings.Count} tiêu đề / {outline.ParagraphCount} đoạn ({outline.ElapsedMs} ms)");
            outputs.Add(OutlineFormatter.Format(outline, o.Format));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"  Thất bại: {ex.Message}");
            failed++;
        }
    }

    var text = string.Join(Environment.NewLine, outputs);
    if (o.OutputPath is { } path)
    {
        await File.WriteAllTextAsync(path, text, new UTF8Encoding(false), ct);
        if (!o.Quiet) Console.Error.WriteLine($"Đã ghi: {path}");
    }
    else
    {
        Console.WriteLine(text);
    }

    return failed == 0 ? 0 : 1;
}

static int RunDumpXml(CommandLineOptions o)
{
    foreach (var file in ExpandInputs(o.Inputs))
    {
        var conversion = LegacyDocConverter.EnsureDocx(file);
        try
        {
            var slim = new DocxSlimExtractor(o.Pipeline.Extraction).Extract(conversion.Path);
            var candidates = slim.Candidates.ToList();

            if (!o.Quiet)
            {
                Console.Error.WriteLine(
                    $"{slim.FileName}: {slim.Paragraphs.Count} đoạn, {candidates.Count} ứng viên, " +
                    $"{candidates.Count(p => p.Role == ParagraphRole.StyledHeading)} theo style");
            }

            if (o.CompactXml)
            {
                // Đúng bằng nội dung sẽ gửi cho mô hình. Production chỉ hỏi ứng viên;
                // --review-all dùng khi audit/thu nhãn toàn văn bản.
                var review = o.Pipeline.ReviewAllParagraphs
                    ? slim.Paragraphs.Where(p => p.Role != ParagraphRole.Empty).Select(p => p.Index).ToHashSet()
                    : null;
                var lines = SlimXmlSerializer.BuildLines(slim, o.Pipeline.Extraction, review);
                Console.WriteLine(SlimXmlSerializer.WrapChunk(lines, 1, 1));
            }
            else
            {
                Console.WriteLine(SlimXmlSerializer.ToFullXml(slim, o.Pipeline.Extraction));
            }
        }
        finally
        {
            LegacyDocConverter.Cleanup(conversion);
        }
    }
    return 0;
}

static int RunBench(CommandLineOptions o)
{
    var dir = Path.GetFullPath(o.Inputs.FirstOrDefault() ?? "bench");
    foreach (var doc in DocxHeaderExtractor.Core.Eval.BenchDocumentFactory.All())
    {
        var path = DocxHeaderExtractor.Core.Eval.BenchDocumentFactory.Write(doc, dir);
        var headings = doc.Paragraphs.Count(p => p.Level is not null);
        Console.Error.WriteLine($"  {Path.GetFileName(path)}  ({headings} tiêu đề)  — {doc.Description}");
    }
    Console.Error.WriteLine($"Đã sinh bộ test vào: {dir}");
    Console.Error.WriteLine("Chấm bằng: dhx eval \"" + dir + "\" --no-llm");
    return 0;
}

static async Task<int> RunEvalAsync(CommandLineOptions o, CancellationToken ct)
{
    if (!o.Pipeline.DisableLlm && o.Pipeline.Backend == InferenceBackend.Local &&
        string.IsNullOrWhiteSpace(o.Pipeline.Llama.ModelPath))
    {
        var found = ModelLocator.Locate();
        if (found is null)
        {
            Console.Error.WriteLine("Chưa có mô hình. Dùng --no-llm để chấm riêng tầng luật OpenXML.");
            return 2;
        }
        o.Pipeline.Llama.ModelPath = found;
    }

    var dir = Path.GetFullPath(o.Inputs.FirstOrDefault() ?? "bench");
    return await EvalRunner.RunAsync(dir, o.Pipeline, o.Quiet, ct, o.CalibrationOutputPath);
}

static async Task<int> RunReviewAsync(CommandLineOptions o, CancellationToken ct)
{
    if (!o.Pipeline.DisableLlm && o.Pipeline.Backend == InferenceBackend.Local &&
        string.IsNullOrWhiteSpace(o.Pipeline.Llama.ModelPath))
    {
        var found = ModelLocator.Locate();
        if (found is null)
        {
            Console.Error.WriteLine("Chưa có mô hình. Dùng --no-llm hoặc chỉ định --model để tạo review.");
            return 2;
        }
        o.Pipeline.Llama.ModelPath = found;
    }

    var files = ExpandInputs(o.Inputs);
    if (files.Count == 0)
    {
        Console.Error.WriteLine("Không tìm thấy tài liệu để review.");
        return 2;
    }
    if (files.Count > 1 && o.OutputPath is not null)
    {
        Console.Error.WriteLine("Chỉ dùng --out khi review đúng một tài liệu.");
        return 2;
    }

    using var pipeline = new HeaderExtractionPipeline(o.Pipeline);
    foreach (var file in files)
    {
        if (!o.Quiet) Console.Error.WriteLine($"» Review: {Path.GetFileName(file)}");
        var conversion = LegacyDocConverter.EnsureDocx(file);
        try
        {
            var slim = new DocxSlimExtractor(o.Pipeline.Extraction).Extract(conversion.Path);
            var outline = await pipeline.RunAsync(file, ct);
            var bundle = ReviewBundle.Create(outline, slim);
            var output = o.OutputPath ?? Path.ChangeExtension(file, ".review.json");
            await File.WriteAllTextAsync(output, bundle.ToJson(), new UTF8Encoding(false), ct);
            if (!o.Quiet)
                Console.Error.WriteLine($"  Đã ghi {bundle.Rows.Count} paragraph cần duyệt: {output}");
        }
        finally
        {
            LegacyDocConverter.Cleanup(conversion);
        }
    }
    return 0;
}

static int RunReviewKey(CommandLineOptions o)
{
    if (o.Inputs.Count != 1)
    {
        Console.Error.WriteLine("review-key cần đúng một file .review.json.");
        return 2;
    }

    var reviewPath = Path.GetFullPath(o.Inputs[0]);
    if (!File.Exists(reviewPath))
    {
        Console.Error.WriteLine($"Không tìm thấy file review: {reviewPath}");
        return 2;
    }

    var bundle = ReviewBundle.Load(reviewPath);
    var stem = Path.GetFileName(reviewPath).EndsWith(".review.json", StringComparison.OrdinalIgnoreCase)
        ? Path.GetFileName(reviewPath)[..^12]
        : Path.GetFileNameWithoutExtension(reviewPath);
    var directory = Path.GetDirectoryName(reviewPath)!;
    var keyPath = Path.GetFullPath(o.OutputPath ?? Path.Combine(directory, stem + ".key"));
    var trainingPath = Path.GetFullPath(o.TrainingOutputPath ?? Path.Combine(directory, stem + ".training.jsonl"));

    File.WriteAllText(keyPath, bundle.ToAnswerKeyText(), new UTF8Encoding(false));
    File.WriteAllText(trainingPath, bundle.ToTrainingJsonl(), new UTF8Encoding(false));
    if (!o.Quiet)
    {
        Console.Error.WriteLine($"Đã ghi key đánh giá: {keyPath}");
        Console.Error.WriteLine($"Đã ghi dữ liệu huấn luyện: {trainingPath}");
    }
    return 0;
}

static int RunSample(CommandLineOptions o)
{
    var target = Path.GetFullPath(o.Inputs.FirstOrDefault() ?? "sample.docx");
    var dir = Path.GetDirectoryName(target);
    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

    SampleDocumentFactory.Create(target);
    Console.Error.WriteLine($"Đã tạo file mẫu: {target}");
    return 0;
}

static int RunModelInfo(CommandLineOptions o)
{
    var path = o.Inputs.FirstOrDefault() ?? o.Pipeline.Llama.ModelPath;
    if (string.IsNullOrWhiteSpace(path)) path = ModelLocator.Locate() ?? "";
    if (!File.Exists(path))
    {
        Console.Error.WriteLine("Không tìm thấy file .gguf.");
        return 2;
    }

    DocxHeaderExtractor.Core.Llm.LlamaHeaderExtractor.ConfigureNativeLogging(o.Pipeline.Llama.VerboseNativeLog);

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

    static bool IsSupported(string f) =>
        Path.GetExtension(f).ToLowerInvariant() is ".docx" or ".docm" or ".doc" or ".rtf" or ".odt";
}

/// <summary>Tìm mô hình .gguf theo thứ tự: DHX_MODEL → appsettings.json → thư mục models.</summary>
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
