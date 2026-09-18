using DocxHeaderExtractor.DocumentProcessing.Review;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using DocxHeaderExtractor.Cli;
using DocxHeaderExtractor.AgentHarness;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Chunking;
using DocxHeaderExtractor.DocumentProcessing.Features;
using DocxHeaderExtractor.DocumentProcessing.Policy;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Projection;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Repair;
using DocxHeaderExtractor.DocumentProcessing.Vision;
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
// The source-only correctness preflight has a fixed document target and intentionally takes
// no positional input. Keep the generic CLI input guard from treating it as a missing-file call.
// `sample`/`bench`/`eval` có đích mặc định, `info` tự dò mô hình – không cần đầu vào.
if (options.Inputs.Count == 0 && options.Command is not "info")
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
        "info" => RunModelInfo(options),
        "score" => RunScore(options),
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

    using var tool = new PipelineDocumentExtractionTool(o.Pipeline, new HeaderClassifierFactory(o.Provider));
    // Normal extraction always writes through the canonical ProductOutput authority. Legacy
    // OutlineWriteback remains available only from explicit replay/evaluation commands.
    using IDocumentActionTool? actionTool = o.WritebackPath is null
        ? null
        : new PdfProductWritebackTool(o.Pipeline.Extraction);
    var harness = CliHarnessComposition.Create(files, tool, actionTool);
    if (!o.Quiet)
        Console.Error.WriteLine($"  policy: {harness.Skill}");
    var outputs = new List<string>();
    int failed = 0;

    foreach (var file in files)
    {
        if (!o.Quiet) Console.Error.WriteLine($"» {Path.GetFileName(file)}");
        try
        {
            var agentRun = await harness.RunAsync(AgentRequest(file, o), ct);
            var outline = agentRun.TaskResult.Value;
            if (!o.Quiet)
            {
                Console.Error.WriteLine($"  {AgentRunNarrator.Describe(agentRun)}");

                // Lượt dựng lại là chi phí GẤP ĐÔI và trước đây hoàn toàn câm: narrator chỉ nói
                // "đã phải dựng lại 1 lượt" mà không nói vì sao. Ở §132 một mục do mô hình bù bị
                // nuốt mất vì danh sách sai thứ tự, và phải cắm mốc in chỉ số thủ công mới truy ra.
                foreach (var e in agentRun.Trace.Where(x => x.Kind == AgentRunEventKind.Repairing))
                    Console.Error.WriteLine($"  ⟲ {e.Message}");

                Console.Error.WriteLine($"  agent={agentRun.Outcome} · run={agentRun.RunId:N}");
            }
            outputs.Add(OutlineFormatter.Format(outline, o.Format));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"  {AgentRunNarrator.DescribeError(ex)}");
            // The narrated one-liner is for operators; a failure inside the pipeline is not
            // diagnosable without the frame that threw.
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
/// Ghi ĐÚNG các khối pipeline sẽ gửi cho mô hình, kèm system prompt. Dùng để đo một mô hình khác
/// trên cùng đầu vào: nếu tự dựng lại prompt thì phép so biến thành so hai cách dựng prompt.
/// <para>
/// Truyền <c>--model</c> thì chia khối bằng ĐÚNG tokenizer của mô hình đó và in tỉ lệ ký tự/token
/// đo được. Không truyền thì rơi về ước lượng <see cref="SlimXmlChunker.CharsPerToken"/> — mà
/// chính hằng số đó đang bị nghi sai nặng cho tiếng Việt, nên bản dump khi ấy KHÔNG khớp lượt chạy
/// thật và tệp ghi rõ điều đó.
/// </para>
/// </summary>
static DocumentAgentRequest AgentRequest(string file, CommandLineOptions o) =>
    new(file, AllowExternalDataTransfer:
        !o.Pipeline.DisableLlm && o.Provider.Backend is InferenceBackend.OpenRouter or InferenceBackend.Sglang)
    {
        WritebackTargetPath = o.WritebackPath,
        AllowWritebackOverwrite = o.WritebackOverwrite,
        ApplyHeadingStyles = o.WritebackHeadingStyles,
    };

static int RunScore(CommandLineOptions o)
{
    if (o.Inputs.Count != 2)
    {
        Console.Error.WriteLine("dhx score <reference.json> <prediction.json>");
        return 2;
    }

    static IReadOnlyList<ScoredHeading> Read(string path, params string[] textFields)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var rows = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement
            : document.RootElement.EnumerateObject()
                .Where(property => property.Value.ValueKind == JsonValueKind.Array)
                .Select(property => property.Value)
                .FirstOrDefault(array => array.GetArrayLength() > 0 &&
                    array[0].ValueKind == JsonValueKind.Object);
        var result = new List<ScoredHeading>();
        if (rows.ValueKind != JsonValueKind.Array) return result;
        foreach (var row in rows.EnumerateArray())
        {
            var text = textFields
                .Select(field => row.TryGetProperty(field, out var value) ? value.GetString() : null)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            if (text is null) continue;
            int? level = row.TryGetProperty("level", out var levelValue) &&
                levelValue.ValueKind == JsonValueKind.Number ? levelValue.GetInt32() : null;
            result.Add(new ScoredHeading(text, level));
        }
        return result;
    }

    var gold = Read(o.Inputs[0], "exactText", "text");
    var predicted = Read(o.Inputs[1], "originalText", "text");
    var score = HeadingLevelScorer.Score(gold, predicted);
    Console.WriteLine(score.Describe(Path.GetFileNameWithoutExtension(o.Inputs[0])));
    foreach (var (text, goldLevel, predictedLevel) in score.StructuralMismatches.Take(10))
        Console.WriteLine($"    reference L{goldLevel} -> predicted L{predictedLevel}  {text}");
    return 0;
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

    DocxHeaderExtractor.Infrastructure.AI.LlamaHeaderExtractor.ConfigureNativeLogging(o.Provider.LocalModel.VerboseNativeLog);

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
