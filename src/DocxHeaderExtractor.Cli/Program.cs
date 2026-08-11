using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Cli;
using DocxHeaderExtractor.AgentHarness;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Chunking;
using DocxHeaderExtractor.Core.Eval;
using DocxHeaderExtractor.Core.Llm;
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
        "xml" => await RunDumpXmlAsync(options, cts.Token),
        "info" => RunModelInfo(options),
        "sample" => RunSample(options),
        "bench" => RunBench(options),
        "eval" => await RunEvalAsync(options, cts.Token),
        "review" => await RunReviewAsync(options, cts.Token),
        "review-key" => RunReviewKey(options),
        "toc-keys" => RunTocKeys(options),
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
    if (files.Count > 1 && o.WritebackPath is not null)
    {
        Console.Error.WriteLine("Chỉ dùng --write-docx khi xử lý đúng một tài liệu.");
        return 2;
    }

    using var tool = new PipelineDocumentExtractionTool(o.Pipeline);
    // Tool ghi chỉ được nạp khi người dùng nêu đích rõ ràng: một harness có sẵn quyền ghi mà
    // không ai yêu cầu là bề mặt rủi ro không cần thiết.
    using IDocumentActionTool? actionTool = o.WritebackPath is null
        ? null
        : new OutlineWritebackTool(o.Pipeline.Extraction);
    var harness = new DocumentAgentHarness(tool, actionTool: actionTool);
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
            var outline = agentRun.Outline;
            if (!o.Quiet)
            {
                Console.Error.WriteLine($"  {AgentRunNarrator.Describe(agentRun)}");
                Console.Error.WriteLine($"  agent={agentRun.Outcome} · run={agentRun.RunId:N}");
            }
            outputs.Add(OutlineFormatter.Format(outline, o.Format));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"  {AgentRunNarrator.DescribeError(ex)}");
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
static async Task DumpChunksAsync(SlimDocument slim, CommandLineOptions o, string directory, CancellationToken ct)
{
    Directory.CreateDirectory(directory);

    // Tokenizer thật chỉ có ở backend GGUF cục bộ. Nạp riêng ở đây thay vì dùng lại pipeline: lệnh
    // này không suy luận, chỉ cần bộ đếm token.
    LlamaHeaderExtractor? local = null;
    Func<string, int>? countTokens = null;
    if (!string.IsNullOrWhiteSpace(o.Pipeline.Llama.ModelPath))
    {
        o.Pipeline.PrepareLocalModelProfile();
        local = await LlamaHeaderExtractor.LoadAsync(o.Pipeline.Llama, ct);
        countTokens = local.CountTokens;
    }

    using (local)
    {
    // Sao đúng cách pipeline dựng view (HeaderExtractionPipeline.RunModelAsync): reviewIndexes
    // LUÔN khác null, nên MỌI đoạn không rỗng vào view làm ngữ cảnh và chỉ tập được liệt kê mới
    // mang requested=true. Bản đầu của lệnh này truyền null khi không có --review-all, tức chỉ đưa
    // ứng viên — view nhỏ hơn 4 lần và thiếu hẳn phần thân bài quanh mỗi ứng viên. Dùng nó để so
    // hai mô hình thì cả hai cùng đọc một đầu vào KHÔNG PHẢI đầu vào thật.
    var review = (o.Pipeline.ReviewAllParagraphs
            ? slim.Paragraphs.Where(p => p.Role != ParagraphRole.Empty)
            : slim.Candidates)
        .Select(p => p.Index).ToHashSet();
    var lines = NeutralDocumentViewSerializer.BuildLines(slim, o.Pipeline.Extraction, review);
    var chunks = SlimXmlChunker.Split(
        lines,
        o.Pipeline.Chunking.TokenBudget,
        o.Pipeline.Chunking.Overlap,
        o.Pipeline.Chunking.MaxCandidatesPerChunk,
        shouldAsk: null,
        countTokens: countTokens);

    File.WriteAllText(Path.Combine(directory, "system.txt"), HeaderPrompt.System);
    File.WriteAllText(Path.Combine(directory, "system-critic.txt"), HeaderPrompt.CriticSystem);

    long chars = 0, tokens = 0;
    for (var i = 0; i < chunks.Count; i++)
    {
        var view = NeutralDocumentViewSerializer.WrapChunk(chunks[i].Lines, chunks[i].Number, chunks.Count);
        var body = HeaderPrompt.WithIdConstraint(view, chunks[i].CandidateIndexes);
        File.WriteAllText(Path.Combine(directory, $"chunk-{i + 1:00}.txt"), body);
        chars += view.Length;
        if (countTokens is not null) tokens += countTokens(view);
    }

    if (o.Quiet) return;

    var unit = countTokens is null ? "token ƯỚC LƯỢNG" : "token THẬT";
    Console.Error.WriteLine(
        $"  Đã ghi {chunks.Count} khối (ngân sách {o.Pipeline.Chunking.TokenBudget} {unit}) " +
        $"và system prompt vào {directory}");

    if (countTokens is null)
    {
        Console.Error.WriteLine(
            "  ⚠ Không có --model nên chia khối bằng ước lượng ký tự; bản dump này KHÔNG khớp " +
            "lượt chạy thật. Truyền --model <file.gguf> để đo bằng tokenizer thật.");
        return;
    }

    Console.Error.WriteLine(
        $"  Đo bằng tokenizer thật: {chars} ký tự / {tokens} token = " +
        $"{(double)chars / tokens:0.###} ký tự/token " +
        $"(hằng ước lượng đang dùng: {SlimXmlChunker.CharsPerToken:0.##}; " +
        $"lệch {SlimXmlChunker.CharsPerToken / ((double)chars / tokens):0.##} lần)");
    }
}

static async Task<int> RunDumpXmlAsync(CommandLineOptions o, CancellationToken ct)
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

            if (o.Pipeline.Extraction.ReportModeOnly)
            {
                Console.WriteLine($"{Path.GetFileName(file)}	{slim.Mode?.Mode}	{slim.Mode?.Describe()}");
                continue;
            }

            if (o.DumpChunksDir is { } chunkDir)
            {
                await DumpChunksAsync(slim, o, chunkDir, ct);
                continue;
            }

            if (o.CompactXml)
            {
                // Trợ giúp CLI hứa "--compact = đúng nội dung gửi cho mô hình", nhưng nhánh này vẫn
                // in XML tinh gọn trong khi pipeline đã chuyển sang neutral document view (text +
                // metadata JSON) từ lâu — dump lệch khỏi thứ mô hình thật sự đọc thì mọi phiên gỡ
                // lỗi dựa vào nó đều đi sai hướng. Dùng đúng bộ serializer của pipeline.
                var review = o.Pipeline.ReviewAllParagraphs
                    ? slim.Paragraphs.Where(p => p.Role != ParagraphRole.Empty).Select(p => p.Index).ToHashSet()
                    : null;
                var lines = NeutralDocumentViewSerializer.BuildLines(slim, o.Pipeline.Extraction, review);
                Console.WriteLine(NeutralDocumentViewSerializer.WrapChunk(lines, 1, 1));
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

    using var tool = new PipelineDocumentExtractionTool(o.Pipeline);
    var harness = new DocumentAgentHarness(tool);
    foreach (var file in files)
    {
        if (!o.Quiet) Console.Error.WriteLine($"» Review: {Path.GetFileName(file)}");
        var conversion = LegacyDocConverter.EnsureDocx(file);
        try
        {
            var slim = new DocxSlimExtractor(o.Pipeline.Extraction).Extract(conversion.Path);
            var agentRun = await harness.RunAsync(AgentRequest(file, o), ct);
            var outline = agentRun.Outline;
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

static int RunTocKeys(CommandLineOptions o)
{
    if (o.Inputs.Count == 0)
    {
        Console.Error.WriteLine("Chưa chỉ định thư mục hoặc file .docx cho toc-keys.");
        return 2;
    }

    var files = new List<string>();
    foreach (var input in o.Inputs)
    {
        if (Directory.Exists(input))
            files.AddRange(Directory.EnumerateFiles(input, "*.docx", SearchOption.AllDirectories)
                .Where(f => !Path.GetFileName(f).StartsWith('~')));
        else if (File.Exists(input))
            files.Add(Path.GetFullPath(input));
        else
            Console.Error.WriteLine($"Bỏ qua (không tồn tại): {input}");
    }
    files = files.Distinct().OrderBy(f => f).ToList();
    if (files.Count == 0)
    {
        Console.Error.WriteLine("Không tìm thấy .docx nào.");
        return 2;
    }

    var outDir = Path.GetFullPath(o.OutputPath ?? Path.Combine("keys", "toc-derived"));
    Directory.CreateDirectory(outDir);

    var results = new List<TocKeyResult>();
    foreach (var file in files)
    {
        var conversion = LegacyDocConverter.EnsureDocx(file);
        try
        {
            var slim = new DocxSlimExtractor(o.Pipeline.Extraction).Extract(conversion.Path);
            var result = TocAnswerKeyGenerator.Generate(slim, o.TocMatchThreshold);
            results.Add(result with { FileName = Path.GetFileName(file) });
        }
        finally
        {
            LegacyDocConverter.Cleanup(conversion);
        }
    }

    var accepted = 0;
    foreach (var r in results)
    {
        var label = r.Status switch
        {
            TocKeyStatus.Accepted =>
                $"OK    {r.MatchedCount}/{r.TocEntryCount} ({r.MatchRatio:P0})",
            TocKeyStatus.BelowMatchThreshold =>
                $"DƯỚI  {r.MatchedCount}/{r.TocEntryCount} ({r.MatchRatio:P0})  " +
                $"[không tìm thấy {r.TocEntryCount - r.MatchedCount - r.AmbiguousBodyMatchCount}, " +
                $"mơ hồ {r.AmbiguousBodyMatchCount}]",
            _ => "THIẾU mục lục",
        };
        if (!o.Quiet) Console.Error.WriteLine($"  {r.FileName,-52} {label}");
        if (o.Verbose)
        {
            foreach (var t in r.UnmatchedTocText)
                Console.Error.WriteLine($"    không tìm thấy: {t}");
            foreach (var t in r.AmbiguousTocText)
                Console.Error.WriteLine($"    mơ hồ (>1 đoạn thân bài trùng text): {t}");
        }

        if (!r.Accepted) continue;
        var keyPath = Path.Combine(outDir, Path.GetFileNameWithoutExtension(r.FileName) + ".key");
        File.WriteAllText(keyPath, r.ToAnswerKeyText(), new UTF8Encoding(false));
        accepted++;
    }

    Console.Error.WriteLine();
    Console.Error.WriteLine($"{accepted}/{results.Count} file đủ ngưỡng {o.TocMatchThreshold:P0} — đã ghi .key vào {outDir}");
    var insufficient = results.Count(r => r.Status == TocKeyStatus.InsufficientTocEntries);
    var below = results.Count(r => r.Status == TocKeyStatus.BelowMatchThreshold);
    Console.Error.WriteLine($"  thiếu mục lục: {insufficient}   dưới ngưỡng: {below}");
    Console.Error.WriteLine("Đáp án toc_derived CHƯA qua người duyệt — xem keys/README.md trước khi dùng làm nền so sánh.");
    return 0;
}

static DocumentAgentRequest AgentRequest(string file, CommandLineOptions o) =>
    new(file, AllowExternalDataTransfer:
        !o.Pipeline.DisableLlm && o.Pipeline.Backend == InferenceBackend.OpenRouter)
    {
        WritebackTargetPath = o.WritebackPath,
        AllowWritebackOverwrite = o.WritebackOverwrite,
        ApplyHeadingStyles = o.WritebackHeadingStyles,
    };

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
