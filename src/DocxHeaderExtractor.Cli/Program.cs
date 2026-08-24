using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using DocxHeaderExtractor.Cli;
using DocxHeaderExtractor.AgentHarness;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Chunking;
using DocxHeaderExtractor.Core.Eval;
using DocxHeaderExtractor.Core.Llm;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Output;
using DocxHeaderExtractor.Core.Pipeline;
using DocxHeaderExtractor.Core.Repair;
using DocxHeaderExtractor.Core.Vision;

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
        "repair" => await RunRepairAsync(options, cts.Token),
        "repair-calibrate" => await RunRepairCalibrateAsync(options, cts.Token),
        "repair-audit" => await RunRepairAuditAsync(options, cts.Token),
        "repair-key-package" => await RunRepairKeyPackageAsync(options, cts.Token),
        "pdf-clusters" => await RunPdfClustersAsync(options, cts.Token),
        "pdf-stage-eval" => await RunPdfStageEvalAsync(options, cts.Token),
        "pdf-hierarchy-facts" => await RunPdfHierarchyFactsAsync(options, cts.Token),
        "pdf-hierarchy-marker-counterfactual" => RunPdfHierarchyMarkerCounterfactual(options),
        "pdf-visual-probe" => await RunPdfVisualProbeAsync(options, cts.Token),
        "pdf-visual-representation-eval" => await RunPdfVisualRepresentationEvalAsync(options, cts.Token),
        "pdf-visual-result-eval" => await RunPdfVisualResultEvalAsync(options, cts.Token),
        "key-rebase" => await RunKeyRebaseAsync(options, cts.Token),
        "pdf-visual-provenance-eval" => await RunPdfVisualProvenanceEvalAsync(options, cts.Token),
        "pdf-visual-scheduler-benchmark" => await RunPdfVisualSchedulerBenchmarkAsync(options, cts.Token),
        "pdf-rank-eval" => RunPdfRankEval(options),
        "pdf-first-loss-audit" => RunPdfFirstLossAudit(options),
        "pdf-occurrence-eval" => RunPdfOccurrenceEval(options),
        "pdf-occurrence-counterfactual-eval" => RunPdfOccurrenceCounterfactualEval(options),
        "pdf-candidate-construction-audit" => RunPdfCandidateConstructionAudit(options),
        "pdf-semantic-recovery-eval" => await RunPdfSemanticRecoveryEvalAsync(options, cts.Token),
        "pdf-semantic-recovery-result-eval" => RunPdfSemanticRecoveryResultEval(options),
        "pdf-hierarchy-facts-eval" => RunPdfHierarchyFactsEval(options),
        "pdf-tags" => await RunPdfTagsAsync(options, cts.Token),
        "pdf-bookmarks" => RunPdfBookmarks(options),
        "verify-corrupt" => await RunVerifyCorruptAsync(options, cts.Token),
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
                Console.WriteLine($"{Path.GetFileName(file)}	{slim.Mode?.Mode}	{slim.Mode?.Status}	{slim.Mode?.Describe()}");
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

        var shouldWrite = r.Accepted || (o.TocPartial && r.MatchedCount > 0);
        if (!shouldWrite) continue;
        var keyPath = Path.Combine(outDir, Path.GetFileNameWithoutExtension(r.FileName) + ".key");
        File.WriteAllText(keyPath, r.ToAnswerKeyText(partial: !r.Accepted), new UTF8Encoding(false));
        accepted++;
    }

    Console.Error.WriteLine();
    var writeLabel = o.TocPartial ? "file đã ghi .key (gồm partial_toc nếu dưới ngưỡng)" : $"file đủ ngưỡng {o.TocMatchThreshold:P0}";
    Console.Error.WriteLine($"{accepted}/{results.Count} {writeLabel} — đã ghi .key vào {outDir}");
    var insufficient = results.Count(r => r.Status == TocKeyStatus.InsufficientTocEntries);
    var below = results.Count(r => r.Status == TocKeyStatus.BelowMatchThreshold);
    Console.Error.WriteLine($"  thiếu mục lục: {insufficient}   dưới ngưỡng: {below}");
    Console.Error.WriteLine("Đáp án toc_derived CHƯA qua người duyệt — xem keys/README.md trước khi dùng làm nền so sánh.");
    return 0;
}

static async Task<int> RunRepairAsync(CommandLineOptions o, CancellationToken ct)
{
    var files = ExpandInputs(o.Inputs);
    if (files.Count == 0)
    {
        Console.Error.WriteLine("Không tìm thấy tài liệu để repair.");
        return 2;
    }

    var outDir = Path.GetFullPath(o.OutputPath ?? Path.Combine(".verify-build", "auto-repair"));
    Directory.CreateDirectory(outDir);

    var workflow = new AutoRepairWorkflow(o.Pipeline);
    var failed = 0;
    foreach (var file in files)
    {
        if (!o.Quiet) Console.Error.WriteLine($"» Repair probe: {Path.GetFileName(file)}");
        try
        {
            var result = await workflow.RunAsync(
                file,
                new AutoRepairOptions(outDir, AlwaysWriteCase: true),
                ct);
            if (!o.Quiet)
            {
                Console.Error.WriteLine($"  status={result.Status} needsAnalysis={result.NeedsAnalysis}");
                Console.Error.WriteLine($"  case={result.CaseDirectory}");
                foreach (var written in result.WrittenFiles)
                    Console.Error.WriteLine($"  wrote {written}");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"  lỗi repair {Path.GetFileName(file)}: {ex.Message}");
            failed++;
        }
    }

    return failed == 0 ? 0 : 1;
}

static async Task<int> RunRepairCalibrateAsync(CommandLineOptions o, CancellationToken ct)
{
    var allFiles = ExpandCalibrationInputs(o.Inputs)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
        .ToList();
    var keyIndex = BuildKeyIndex(allFiles);
    var files = allFiles
        .Where(f => File.Exists(Path.ChangeExtension(f, ".key")) ||
                    keyIndex.ContainsKey(Path.GetFileNameWithoutExtension(f)))
        .ToList();
    if (files.Count == 0)
    {
        Console.Error.WriteLine("Không tìm thấy cặp .docx + .key để repair-calibrate.");
        return 2;
    }

    var outputPrefix = Path.GetFullPath(o.OutputPath ?? Path.Combine(".verify-build", "repair-gate-calibration"));
    var parent = Path.GetDirectoryName(outputPrefix);
    if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

    var previousLog = o.Pipeline.Log;
    if (!o.Verbose) o.Pipeline.Log = null;
    RepairGateCalibrationReport report;
    try
    {
        report = await RepairGateCalibration.RunAsync(files, o.Pipeline, keyIndex, ct);
    }
    finally
    {
        o.Pipeline.Log = previousLog;
    }
    var jsonPath = outputPrefix.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
        ? outputPrefix
        : outputPrefix + ".json";
    var csvPath = Path.ChangeExtension(jsonPath, ".csv");
    await File.WriteAllTextAsync(jsonPath, RepairGateCalibration.ToJson(report), new UTF8Encoding(false), ct);
    await File.WriteAllTextAsync(csvPath, RepairGateCalibration.ToCsv(report), new UTF8Encoding(false), ct);

    Console.Error.WriteLine($"Đã chấm gate repair trên {report.Documents} tài liệu.");
    Console.Error.WriteLine($"  status           = {report.CalibrationStatus}");
    Console.Error.WriteLine($"  corr(score, Nav) = {report.ScoreNavigationPearson:0.###}");
    Console.Error.WriteLine($"  corr(score, F1)  = {report.ScoreF1Pearson:0.###}");
    Console.Error.WriteLine($"  gate pass rate   = {report.GatePassRate:P1}");
    Console.Error.WriteLine($"  Nav pass/fail    = {report.GatePassedAverageNavigation:P1} / {report.GateFailedAverageNavigation:P1}");
    Console.Error.WriteLine($"  ranking metric   = {report.PerFileRankingStatus}");
    Console.Error.WriteLine($"  stop condition   = {report.ScoreStopCondition}");
    Console.Error.WriteLine($"  gate branch      = {report.GateBranchStatus}");
    if (report.Split is { } split)
    {
        PrintSubset(split.Tune);
        PrintSubset(split.Holdout);
    }
    if (report.FixedRuleReplay is { } replay)
    {
        Console.Error.WriteLine(
            $"  replay  status={replay.Status} docs={replay.Documents} avgNav={replay.AverageNavigation:P1} " +
            $"Nav pass/fail={replay.GatePassedAverageNavigation:P1}/{replay.GateFailedAverageNavigation:P1} " +
            $"lowPasses={replay.LowNavigationGatePasses}");
        Console.Error.WriteLine($"  replay  reason={replay.Reason}");
        foreach (var finding in replay.Findings)
            Console.Error.WriteLine($"  replay  finding={finding}");
    }
    foreach (var finding in report.Findings)
        Console.Error.WriteLine($"  finding          = {finding}");
    Console.Error.WriteLine($"  wrote {jsonPath}");
    Console.Error.WriteLine($"  wrote {csvPath}");

    return 0;

    static void PrintSubset(RepairGateCalibrationSubset subset)
    {
        Console.Error.WriteLine(
            $"  {subset.Name,-7} docs={subset.Documents} corrNav={subset.ScoreNavigationPearson:0.###} " +
            $"pass={subset.GatePassRate:P1} Nav pass/fail={subset.GatePassedAverageNavigation:P1}/{subset.GateFailedAverageNavigation:P1}");
        Console.Error.WriteLine($"  {subset.Name,-7} routes={string.Join("; ", subset.RouteDistribution.Select(kv => $"{kv.Key}:{kv.Value}"))}");
        Console.Error.WriteLine($"  {subset.Name,-7} modes={string.Join("; ", subset.ModeDistribution.Select(kv => $"{kv.Key}:{kv.Value}"))}");
        foreach (var finding in subset.Findings)
            Console.Error.WriteLine($"  {subset.Name,-7} finding={finding}");
    }
}

static async Task<int> RunRepairAuditAsync(CommandLineOptions o, CancellationToken ct)
{
    var files = ExpandCalibrationInputs(o.Inputs)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
        .ToList();
    if (files.Count == 0)
    {
        Console.Error.WriteLine("Không tìm thấy tài liệu để repair-audit.");
        return 2;
    }

    var outputPrefix = Path.GetFullPath(o.OutputPath ?? Path.Combine(".verify-build", "repair-corpus-audit", "audit"));
    var parent = Path.GetDirectoryName(outputPrefix);
    if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

    var previousLog = o.Pipeline.Log;
    if (!o.Verbose) o.Pipeline.Log = null;
    RepairCorpusAuditReport report;
    try
    {
        report = await RepairCorpusAudit.RunAsync(files, BuildKeyIndex(files), o.Pipeline, ct);
    }
    finally
    {
        o.Pipeline.Log = previousLog;
    }

    var jsonPath = outputPrefix.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
        ? outputPrefix
        : outputPrefix + ".json";
    var csvPath = Path.ChangeExtension(jsonPath, ".csv");
    await File.WriteAllTextAsync(jsonPath, RepairCorpusAudit.ToJson(report), new UTF8Encoding(false), ct);
    await File.WriteAllTextAsync(csvPath, RepairCorpusAudit.ToCsv(report), new UTF8Encoding(false), ct);

    Console.Error.WriteLine($"Đã audit repair corpus trên {report.Documents} tài liệu.");
    Console.Error.WriteLine($"  gate failed    = {report.GateFailed}");
    Console.Error.WriteLine($"  needs_analysis = {report.NeedsAnalysis}");
    Console.Error.WriteLine($"  missing key    = {report.MissingKey}");
    Console.Error.WriteLine($"  rare routes    = {string.Join("; ", report.RareRoutes)}");
    Console.Error.WriteLine("  routes:");
    foreach (var kv in report.RouteDistribution.OrderByDescending(kv => kv.Value))
        Console.Error.WriteLine($"    {kv.Key}: {kv.Value}");
    Console.Error.WriteLine("  diagnostics:");
    foreach (var kv in report.DiagnosticDistribution.OrderByDescending(kv => kv.Value).Take(10))
        Console.Error.WriteLine($"    {kv.Key}: {kv.Value}");
    Console.Error.WriteLine($"  wrote {jsonPath}");
    Console.Error.WriteLine($"  wrote {csvPath}");

    return 0;
}

static async Task<int> RunRepairKeyPackageAsync(CommandLineOptions o, CancellationToken ct)
{
    var files = ExpandCalibrationInputs(o.Inputs)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
        .ToList();
    if (files.Count == 0)
    {
        Console.Error.WriteLine("Không tìm thấy tài liệu để tạo repair-key-package.");
        return 2;
    }

    var outDir = Path.GetFullPath(o.OutputPath ?? Path.Combine(".verify-build", "partial-key-packages"));
    Directory.CreateDirectory(outDir);

    var previousLog = o.Pipeline.Log;
    if (!o.Verbose) o.Pipeline.Log = null;
    var packager = new PartialKeyPackage(o.Pipeline);
    var failed = 0;
    var skipped = 0;
    try
    {
        // Vòng 1: chạy pipeline một lần mỗi file (giữ outline để dùng lại — không chạy pipeline lần
        // hai bên trong PartialKeyPackage) và đo tỷ lệ "cần xem lại". Cổng chẩn đoán cần trung vị của
        // CẢ ĐỢT nên phải có đủ outline trước khi xét từng file (handoff §171/§173).
        var runs = new List<(string File, DocumentOutline? Outline, Exception? Error)>();
        foreach (var file in files)
        {
            try
            {
                using var pipeline = new HeaderExtractionPipeline(o.Pipeline);
                var outline = await pipeline.RunAsync(file, ct);
                runs.Add((file, outline, null));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                runs.Add((file, null, ex));
            }
        }

        var gateInput = runs
            .Where(r => r.Outline is not null)
            .Select(r => (r.File, ReviewRate: RepairDiagnosticGate.ReviewRate(r.Outline!.Headings)))
            .ToList();
        var gate = RepairDiagnosticGate.Evaluate(gateInput)
            .ToDictionary(g => g.File, StringComparer.OrdinalIgnoreCase);

        foreach (var (file, outline, error) in runs)
        {
            if (!o.Quiet) Console.Error.WriteLine($"» Key package: {Path.GetFileName(file)}");

            if (error is not null)
            {
                Console.Error.WriteLine($"  lỗi trích xuất {Path.GetFileName(file)}: {error.Message}");
                failed++;
                continue;
            }

            if (!o.ForceReviewPackage &&
                gate.TryGetValue(file, out var diagnostic) && diagnostic.SuspectedUpstreamError)
            {
                Console.Error.WriteLine($"  BỎ QUA — {diagnostic.Reason}");
                Console.Error.WriteLine(
                    "  (dùng --force-review-package nếu đã tự xem và vẫn muốn sinh gói duyệt cho file này)");
                skipped++;
                continue;
            }

            try
            {
                var result = await packager.RunAsync(
                    file,
                    outline!,
                    new PartialKeyPackageOptions(
                        outDir,
                        o.KeyPackageLimit,
                        o.KeyPackageStart,
                        DistributedSample: !o.KeyPackageContiguous),
                    ct);
                Console.Error.WriteLine(
                    $"  selected {result.SelectedHeadings}/{result.TotalHeadings} headings ({result.SampleStrategy}) -> {result.Directory}");
                Console.Error.WriteLine(
                    $"  lines   paragraphs={result.LineProbe.TextParagraphs} hard={result.LineProbe.HardLines} " +
                    $"recovered={result.LineProbe.RecoveredLines} long={result.LineProbe.LongParagraphs}");
                Console.Error.WriteLine($"  key     {result.DraftKeyPath}");
                Console.Error.WriteLine($"  review  {result.ReviewCsvPath}");
                Console.Error.WriteLine($"  outline {result.OutlineJsonPath}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"  lỗi key package {Path.GetFileName(file)}: {ex.Message}");
                failed++;
            }
        }

        if (skipped > 0)
            Console.Error.WriteLine(
                $"Bỏ qua {skipped}/{files.Count} file do cổng chẩn đoán (tỷ lệ cần xem lại bất thường — xem lý do ở trên).");
    }
    finally
    {
        o.Pipeline.Log = previousLog;
    }

    return failed == 0 ? 0 : 1;
}

static async Task<int> RunVerifyCorruptAsync(CommandLineOptions o, CancellationToken ct)
{
    var files = ExpandInputs(o.Inputs);
    if (files.Count == 0)
    {
        Console.Error.WriteLine("Không tìm thấy file để chạy verify-corrupt.");
        return 2;
    }
    // Quét TRƯỚC, đòi model SAU: không có đoạn nào bị gắn cờ thì chẳng cần VLM, và người dùng không
    // phải chỉ định model chỉ để nghe "không có gì để kiểm".
    var pending = new List<(string File, SlimDocument Document, List<SlimParagraph> Corrupt)>();
    foreach (var file in files)
    {
        var conversion = LegacyDocConverter.EnsureDocx(file);
        var slim = new DocxSlimExtractor(o.Pipeline.Extraction).Extract(conversion.Path);
        var corrupt = slim.Paragraphs.Where(p => p.Corrupt).ToList();
        if (corrupt.Count > 0) pending.Add((file, slim, corrupt));
    }

    var totalCorrupt = pending.Sum(p => p.Corrupt.Count);
    if (totalCorrupt == 0)
    {
        Console.Error.WriteLine("Không có đoạn nào bị is_doubled gắn cờ trong các file đã cho.");
        return 0;
    }

    if (string.IsNullOrWhiteSpace(o.VlmModelPath) || string.IsNullOrWhiteSpace(o.VlmMmprojPath))
    {
        Console.Error.WriteLine(
            $"Có {totalCorrupt} đoạn cần kiểm nhưng thiếu --vlm-model và --vlm-mmproj (hoặc biến " +
            "DHX_VLM_MODEL/DHX_VLM_MMPROJ) — cổng chẩn đoán này không dùng model text-only đã cấu " +
            "hình cho các lệnh khác.");
        return 2;
    }

    Console.Error.WriteLine($"Có {totalCorrupt} đoạn bị is_doubled gắn cờ trên {pending.Count} file. Đang nạp VLM...");

    using var vlm = await VlmImageQuestion.LoadAsync(
        o.VlmModelPath, o.VlmMmprojPath, o.VlmContextSize, o.VlmGpuLayerCount, ct);
    Console.Error.WriteLine("Đã nạp VLM.");

    var confirmed = 0;
    var suspectedBug = 0;
    var inconclusive = 0;
    foreach (var (file, document, corruptParagraphs) in pending)
    {
        Console.Error.WriteLine($"» {Path.GetFileName(file)} ({corruptParagraphs.Count} đoạn nghi vấn)");
        foreach (var paragraph in corruptParagraphs)
        {
            var check = await CorruptParagraphVisualVerifier.VerifyAsync(
                file, document, paragraph, vlm, o.VlmDpi, ct);
            switch (check.Verdict)
            {
                case CorruptParagraphVisualVerdict.ConfirmedSourceCorruption: confirmed++; break;
                case CorruptParagraphVisualVerdict.SuspectedParserBug: suspectedBug++; break;
                default: inconclusive++; break;
            }
            Console.Error.WriteLine(
                $"  đoạn {check.ParagraphIndex} [{check.Verdict}] trang={check.RenderedPage?.ToString() ?? "?"} " +
                $"text=\"{TruncateForLog(check.ExtractedText, 60)}\"");
            if (!o.Quiet) Console.Error.WriteLine($"    {TruncateForLog(check.Reason, 200)}");
        }
    }

    Console.Error.WriteLine(
        $"Tổng: {confirmed} xác nhận lỗi nguồn thật, {suspectedBug} nghi lỗi parser, {inconclusive} không kết luận được.");
    if (suspectedBug > 0)
        Console.Error.WriteLine(
            $"CẢNH BÁO: {suspectedBug} đoạn có thể là is_doubled báo động giả (lỗi tầng đọc) — nên xem lại code, không phải file.");

    return 0;
}

static string TruncateForLog(string text, int max) => text.Length <= max ? text : text[..max] + "…";

static Task<int> RunPdfTagsAsync(CommandLineOptions o, CancellationToken ct)
{
    var inputs = ExpandPdfClusterInputs(o.Inputs);
    if (inputs.Count == 0)
    {
        Console.Error.WriteLine("Không tìm thấy PDF/DOCX để chạy pdf-tags.");
        return Task.FromResult(2);
    }

    var reports = new List<PdfTaggedHeadingProbeReport>();
    foreach (var input in inputs)
    {
        ct.ThrowIfCancellationRequested();
        SlimDocument? slim = null;
        var pdf = input;
        if (!string.Equals(Path.GetExtension(input), ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            var conversion = LegacyDocConverter.EnsureDocx(input);
            slim = new DocxSlimExtractor(o.Pipeline.Extraction).Extract(conversion.Path);
            pdf = PdfTextbookOutline.FindSiblingPdf(input) ?? "";
        }
        if (string.IsNullOrWhiteSpace(pdf) || !File.Exists(pdf))
        {
            reports.Add(new PdfTaggedHeadingProbeReport(input, 0, 0, 0, 0, "no-sibling-pdf", TaggedStructureTrace.Empty, []));
            continue;
        }
        var report = PdfTaggedHeadingProbe.Analyze(pdf, slim);
        reports.Add(report);
        if (!o.Quiet)
            Console.Error.WriteLine($"{Path.GetFileName(input)}: H={report.HeadingElements}, aligned={report.DocxAligned}, status={report.Status}");
    }

    var payload = reports.Count == 1 ? (object)reports[0] : reports;
    var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    if (string.IsNullOrWhiteSpace(o.OutputPath))
        Console.WriteLine(json);
    else
    {
        var path = Path.GetFullPath(o.OutputPath);
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
        File.WriteAllText(path, json, new UTF8Encoding(false));
        Console.Error.WriteLine($"Đã ghi: {path}");
    }
    return Task.FromResult(0);
}

static int RunPdfBookmarks(CommandLineOptions o)
{
    var inputs = ExpandPdfClusterInputs(o.Inputs);
    if (inputs.Count == 0)
    {
        Console.Error.WriteLine("Không tìm thấy PDF/DOCX để chạy pdf-bookmarks.");
        return 2;
    }

    var reports = new List<PdfBookmarkProbeReport>();
    foreach (var input in inputs)
    {
        var pdf = string.Equals(Path.GetExtension(input), ".pdf", StringComparison.OrdinalIgnoreCase)
            ? input
            : PdfTextbookOutline.FindSiblingPdf(input) ?? "";
        var report = string.IsNullOrWhiteSpace(pdf) || !File.Exists(pdf)
            ? new PdfBookmarkProbeReport(input, 0, "no-sibling-pdf", [])
            : PdfBookmarkProbe.Analyze(pdf);
        reports.Add(report);
        if (!o.Quiet)
            Console.Error.WriteLine($"{Path.GetFileName(input)}: bookmarks={report.Candidates.Count}, status={report.Status}");
    }

    var payload = reports.Count == 1 ? (object)reports[0] : reports;
    var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    if (string.IsNullOrWhiteSpace(o.OutputPath)) Console.WriteLine(json);
    else
    {
        var path = Path.GetFullPath(o.OutputPath);
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
        File.WriteAllText(path, json, new UTF8Encoding(false));
        Console.Error.WriteLine($"Đã ghi: {path}");
    }
    return 0;
}

static async Task<int> RunPdfVisualProbeAsync(CommandLineOptions o, CancellationToken ct)
{
    var files = ExpandCalibrationInputs(o.Inputs);
    if (files.Count != 1)
    {
        Console.Error.WriteLine("pdf-visual-probe cần đúng một file DOCX.");
        return 2;
    }

    var docx = files[0];
    var pdf = PdfTextbookOutline.FindSiblingPdf(docx);
    if (pdf is null)
    {
        Console.Error.WriteLine("Không tìm thấy PDF sibling cho visual probe.");
        return 2;
    }

    var slim = new DocxSlimExtractor(o.Pipeline.Extraction).Extract(docx);
    if (o.PdfVisualPage is { } page)
    {
        var bounds = PdfRegionRasterizer.GetPageBounds(pdf, page);
        var png = PdfRegionRasterizer.RenderCropPng(pdf, page, 0, 0, bounds.Width, bounds.Height, o.VlmDpi);
        var output = Path.GetFullPath(o.OutputPath ?? $"pdf-visual-page-{page}.png");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllBytesAsync(output, png, ct);
        Console.Error.WriteLine($"Đã ghi: {output}");
        return 0;
    }
    if (o.PdfVisualLineList)
    {
        var lines = PdfVisualTextRecovery.ListLinesForAudit(pdf);
        var lineJson = JsonSerializer.Serialize(new { file = Path.GetFileName(docx), pdf = Path.GetFileName(pdf), lines }, new JsonSerializerOptions { WriteIndented = true });
        if (string.IsNullOrWhiteSpace(o.OutputPath)) Console.WriteLine(lineJson);
        else
        {
            var output = Path.GetFullPath(o.OutputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await File.WriteAllTextAsync(output, lineJson, new UTF8Encoding(false), ct);
            Console.Error.WriteLine($"Đã ghi: {output}");
        }
        return 0;
    }
    if (o.PdfVisualProbeList)
    {
        var regions = PdfVisualTextRecovery.ListRegionsForAudit(pdf);
        var listJson = JsonSerializer.Serialize(new { file = Path.GetFileName(docx), pdf = Path.GetFileName(pdf), regions }, new JsonSerializerOptions { WriteIndented = true });
        if (string.IsNullOrWhiteSpace(o.OutputPath)) Console.WriteLine(listJson);
        else
        {
            var output = Path.GetFullPath(o.OutputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await File.WriteAllTextAsync(output, listJson, new UTF8Encoding(false), ct);
            Console.Error.WriteLine($"Đã ghi: {output}");
        }
        return 0;
    }
    if (!string.IsNullOrWhiteSpace(o.PdfVisualProbeText))
    {
        var sourceOnly = PdfVisualTextRecovery.InspectSourceForAudit(slim, o.PdfVisualProbeText);
        var sourceJson = JsonSerializer.Serialize(new { file = Path.GetFileName(docx), sourceOnly }, new JsonSerializerOptions { WriteIndented = true });
        if (string.IsNullOrWhiteSpace(o.OutputPath)) Console.WriteLine(sourceJson);
        else
        {
            var output = Path.GetFullPath(o.OutputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await File.WriteAllTextAsync(output, sourceJson, new UTF8Encoding(false), ct);
            Console.Error.WriteLine($"Đã ghi: {output}");
        }
        return 0;
    }

    if (string.IsNullOrWhiteSpace(o.VlmModelPath) != string.IsNullOrWhiteSpace(o.VlmMmprojPath))
        throw new ArgumentException("pdf-visual-probe dùng VLM local cần đủ --vlm-model và --vlm-mmproj.");
    using IPdfVisualQuestion visual = !string.IsNullOrWhiteSpace(o.VlmModelPath)
        ? await VlmImageQuestion.LoadAsync(o.VlmModelPath!, o.VlmMmprojPath!, o.VlmContextSize, o.VlmGpuLayerCount, ct)
        : o.UseNvidiaNim
            ? new NvidiaNimVisualQuestion(o.Pipeline.Sglang.Endpoint, o.Pipeline.Sglang.ApiKey, o.Pipeline.Sglang.Model,
                o.Pipeline.Sglang.RequestTimeoutSeconds, o.Pipeline.Sglang.TransientRequestRetries)
            : o.Pipeline.Backend == InferenceBackend.OpenRouter
                ? new OpenRouterVisualQuestion(o.Pipeline.OpenRouter.Endpoint, o.Pipeline.OpenRouter.ApiKey, o.Pipeline.OpenRouter.Model)
            : throw new ArgumentException("pdf-visual-probe cần --nvidia-nim hoặc --vlm-model/--vlm-mmproj.");

    var result = await PdfVisualTextRecovery.ProbeAsync(pdf, slim, visual, o.PdfVisualProbeIndex, o.VlmDpi, ct);
    var payload = new { file = Path.GetFileName(docx), pdf = Path.GetFileName(pdf), result };
    var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    if (string.IsNullOrWhiteSpace(o.OutputPath)) Console.WriteLine(json);
    else
    {
        var output = Path.GetFullPath(o.OutputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, json, new UTF8Encoding(false), ct);
        Console.Error.WriteLine($"Đã ghi: {output}");
    }
    return 0;
}

static async Task<int> RunPdfVisualRepresentationEvalAsync(CommandLineOptions o, CancellationToken ct)
{
    var files = ExpandCalibrationInputs(o.Inputs);
    if (files.Count != 1 || string.IsNullOrWhiteSpace(o.PdfVisualRepresentationGoldPath))
    {
        Console.Error.WriteLine("pdf-visual-representation-eval cần đúng một DOCX và --gold <file.key>.");
        return 2;
    }

    var docx = files[0];
    var pdf = PdfTextbookOutline.FindSiblingPdf(docx);
    if (pdf is null)
    {
        Console.Error.WriteLine("Không tìm thấy PDF sibling cho visual representation audit.");
        return 2;
    }

    var slim = new DocxSlimExtractor(o.Pipeline.Extraction).Extract(docx);
    var rawKey = AnswerKey.Load(o.PdfVisualRepresentationGoldPath);
    var stableMap = slim.Paragraphs.Where(p => !string.IsNullOrWhiteSpace(p.StableId))
        .ToDictionary(p => p.StableId, p => p.Index, StringComparer.Ordinal);
    var key = rawKey.HasStableIds ? rawKey.ResolveStableIds(stableMap) : rawKey;
    var report = PdfVisualRepresentationAudit.Evaluate(pdf, slim, key);
    var payload = new
    {
        file = Path.GetFileName(docx),
        pdf = Path.GetFileName(pdf),
        gold = Path.GetFileName(o.PdfVisualRepresentationGoldPath),
        report,
    };
    var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    if (string.IsNullOrWhiteSpace(o.OutputPath)) Console.WriteLine(json);
    else
    {
        var output = Path.GetFullPath(o.OutputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, json, new UTF8Encoding(false), ct);
        Console.Error.WriteLine($"Đã ghi: {output}");
    }
    return 0;
}

static async Task<int> RunPdfVisualResultEvalAsync(CommandLineOptions o, CancellationToken ct)
{
    if (o.Inputs.Count != 1 || string.IsNullOrWhiteSpace(o.PdfVisualRepresentationGoldPath))
    {
        Console.Error.WriteLine("pdf-visual-result-eval cần một run.json và --gold <file.key>.");
        return 2;
    }
    using var json = JsonDocument.Parse(await File.ReadAllTextAsync(o.Inputs[0], ct));
    var root = json.RootElement.ValueKind == JsonValueKind.Array ? json.RootElement[0] : json.RootElement;
    if (!root.TryGetProperty("visualInferenceArtifact", out var artifact))
    {
        Console.Error.WriteLine("Artifact không có visualInferenceArtifact; run cũ không lưu per-region inference facts nên không thể replay trung thực.");
        return 2;
    }
    var traces = artifact.GetProperty("recoveries").Deserialize<List<PdfVisualRecoveryTrace>>() ?? [];
    var representation = artifact.TryGetProperty("representation", out var coverage)
        ? coverage.Deserialize<List<PdfVisualGoldCoverage>>()
        : null;
    var key = AnswerKey.Load(o.PdfVisualRepresentationGoldPath);
    var gold = key.PositiveEntries.Where(entry => !entry.Excluded && !string.IsNullOrWhiteSpace(entry.Text))
        .Select(entry => entry.Text!).ToArray();
    var targets = representation is { Count: > 0 } ? representation.Select(item => item.Gold) : gold;
    var evaluation = PdfVisualInferenceEvaluator.Evaluate(targets, traces, representation);
    var structuralReplay = key.HasStableIds
        ? PdfVisualStructuralReplayEvaluator.Evaluate(key, traces)
        : null;
    var payload = new
    {
        input = Path.GetFileName(o.Inputs[0]),
        gold = Path.GetFileName(o.PdfVisualRepresentationGoldPath),
        evaluation,
        structuralReplay
    };
    var output = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    if (string.IsNullOrWhiteSpace(o.OutputPath)) Console.WriteLine(output);
    else await File.WriteAllTextAsync(Path.GetFullPath(o.OutputPath), output, new UTF8Encoding(false), ct);
    return 0;
}

static async Task<int> RunKeyRebaseAsync(CommandLineOptions o, CancellationToken ct)
{
    if (o.Inputs.Count != 1 || string.IsNullOrWhiteSpace(o.PdfVisualRepresentationGoldPath) ||
        string.IsNullOrWhiteSpace(o.OutputPath) || string.IsNullOrWhiteSpace(o.RebaseProvenancePath))
    {
        Console.Error.WriteLine("key-rebase cần <regenerated.docx>, --gold <old.key>, --out <new.key>, và --rebase-provenance <audit.json>.");
        return 2;
    }

    var documentPath = Path.GetFullPath(o.Inputs[0]);
    var sourceKeyPath = Path.GetFullPath(o.PdfVisualRepresentationGoldPath);
    var rawKey = AnswerKey.Load(sourceKeyPath);
    var slim = new DocxSlimExtractor(o.Pipeline.Extraction).Extract(documentPath);
    var resolved = EvaluationAnchorResolver.Resolve(rawKey, slim.Paragraphs);
    var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(documentPath, ct))).ToLowerInvariant();
    var goldVersion = o.GoldVersion ?? $"{Path.GetFileNameWithoutExtension(sourceKeyPath)}-rebased";
    var provenance = new
    {
        schemaVersion = 1,
        goldVersion,
        previousGoldVersion = o.PreviousGoldVersion,
        sourceDocumentPath = documentPath,
        sourceDocumentSha256 = hash,
        sourceKeyPath,
        rebaseReason = "DOCX regenerated; paragraph stable IDs changed",
        evaluationOnly = true,
        modelOutputUsed = false,
        complete = resolved.Complete,
        entries = resolved.Entries.Select((resolution, ordinal) => new
        {
            ordinal,
            title = resolution.Title,
            oldStableId = rawKey.Entries[ordinal].StableId,
            oldIndex = rawKey.Entries[ordinal].Index,
            level = rawKey.Entries[ordinal].Level,
            status = resolution.Status,
            method = resolution.Method,
            newStableId = resolution.ResolvedStableId,
            newIndex = resolution.ResolvedIndex,
            candidateCount = resolution.CandidateCount
        }).ToArray()
    };
    var provenancePath = Path.GetFullPath(o.RebaseProvenancePath);
    Directory.CreateDirectory(Path.GetDirectoryName(provenancePath)!);
    await File.WriteAllTextAsync(provenancePath,
        JsonSerializer.Serialize(provenance, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false), ct);

    if (!resolved.Complete)
    {
        Console.Error.WriteLine($"Rebase chưa complete; không ghi key mới. Xem audit: {provenancePath}");
        return 1;
    }

    var outputPath = Path.GetFullPath(o.OutputPath);
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    var lines = new StringBuilder()
        .Append("# Evaluation-only rebased gold: ").AppendLine(goldVersion)
        .AppendLine("# Source-derived from reviewed key titles + regenerated DOCX canonical occurrence order; model output was not used.")
        .Append("# Previous gold: ").AppendLine(o.PreviousGoldVersion ?? Path.GetFileName(sourceKeyPath))
        .Append("# Source document SHA-256: ").AppendLine(hash);
    foreach (var item in resolved.Entries.Select((resolution, ordinal) => new { resolution, entry = rawKey.Entries[ordinal] }))
    {
        if (item.entry.Excluded || string.IsNullOrWhiteSpace(item.resolution.ResolvedStableId)) continue;
        lines.Append('@').Append(item.resolution.ResolvedStableId);
        if (item.entry.Level is not null) lines.Append(' ').Append(item.entry.Level.Value);
        if (!string.IsNullOrWhiteSpace(item.entry.Text)) lines.Append("   # ").Append(item.entry.Text);
        lines.AppendLine();
    }
    await File.WriteAllTextAsync(outputPath, lines.ToString(), new UTF8Encoding(false), ct);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        status = "rebased",
        goldVersion,
        key = outputPath,
        provenance = provenancePath,
        entries = rawKey.Count,
        resolved = resolved.Entries.Count(entry => entry.Status == "resolved")
    }));
    return 0;
}

static async Task<int> RunPdfVisualProvenanceEvalAsync(CommandLineOptions o, CancellationToken ct)
{
    if (o.Inputs.Count < 2)
    {
        Console.Error.WriteLine("pdf-visual-provenance-eval cần ít nhất hai run.json.");
        return 2;
    }
    var traces = new List<PdfVisualRecoveryTrace>();
    foreach (var input in o.Inputs)
    {
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(input, ct));
        var root = json.RootElement.ValueKind == JsonValueKind.Array ? json.RootElement[0] : json.RootElement;
        if (!root.TryGetProperty("visualInferenceArtifact", out var artifact) || !artifact.TryGetProperty("recoveries", out var recoveries))
        {
            Console.Error.WriteLine($"Artifact thiếu recoveries: {input}");
            return 2;
        }
        traces.AddRange(recoveries.Deserialize<List<PdfVisualRecoveryTrace>>() ?? []);
    }
    var report = PdfVisualCrossProducerEvaluator.Evaluate(traces);
    var payload = new { inputs = o.Inputs.Select(Path.GetFileName).ToArray(), report };
    var output = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    if (string.IsNullOrWhiteSpace(o.OutputPath)) Console.WriteLine(output);
    else await File.WriteAllTextAsync(Path.GetFullPath(o.OutputPath), output, new UTF8Encoding(false), ct);
    return 0;
}

static async Task<int> RunPdfVisualSchedulerBenchmarkAsync(CommandLineOptions o, CancellationToken ct)
{
    if (o.Inputs.Count != 1 || o.PdfVisualArtifacts.Count == 0)
    {
        Console.Error.WriteLine("pdf-visual-scheduler-benchmark cần một DOCX và ít nhất một --visual-artifact run.json.");
        return 2;
    }
    var docx = o.Inputs[0];
    var pdf = PdfTextbookOutline.FindSiblingPdf(docx);
    if (pdf is null) { Console.Error.WriteLine("Không tìm thấy PDF sibling cho scheduler benchmark."); return 2; }
    var slim = new DocxSlimExtractor(o.Pipeline.Extraction).Extract(docx);
    var traces = new List<PdfVisualRecoveryTrace>();
    foreach (var input in o.PdfVisualArtifacts)
    {
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(input, ct));
        var root = json.RootElement.ValueKind == JsonValueKind.Array ? json.RootElement[0] : json.RootElement;
        if (!root.TryGetProperty("visualInferenceArtifact", out var artifact) || !artifact.TryGetProperty("recoveries", out var recoveries))
        { Console.Error.WriteLine($"Artifact thiếu recoveries: {input}"); return 2; }
        traces.AddRange(recoveries.Deserialize<List<PdfVisualRecoveryTrace>>() ?? []);
    }
    var regime = PdfDocumentRegime.Infer(slim.Paragraphs.Select(paragraph => paragraph.Text));
    var report = PdfVisualSchedulerBenchmark.Evaluate(regime, PdfVisualTextRecovery.ListRegionsForAudit(pdf), traces);
    var payload = new { file = Path.GetFileName(docx), artifacts = o.PdfVisualArtifacts.Select(Path.GetFileName).ToArray(), report };
    var output = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    if (string.IsNullOrWhiteSpace(o.OutputPath)) Console.WriteLine(output);
    else await File.WriteAllTextAsync(Path.GetFullPath(o.OutputPath), output, new UTF8Encoding(false), ct);
    return 0;
}

static async Task<int> RunPdfStageEvalAsync(CommandLineOptions o, CancellationToken ct)
{
    if (o.Pipeline.DisableLlm)
    {
        Console.Error.WriteLine("pdf-stage-eval cần LLM analyst; bỏ --no-llm và chỉ định --sglang/--model.");
        return 2;
    }

    var files = ExpandCalibrationInputs(o.Inputs);
    var keyIndex = BuildKeyIndex(files, o.PdfStageKeyRoot);
    if (files.Count == 0) { Console.Error.WriteLine("Không tìm thấy DOCX để chấm PDF stages."); return 2; }

    using var analyst = await CreateClassifierAsync(o, ct);
    if (string.IsNullOrWhiteSpace(o.VlmModelPath) != string.IsNullOrWhiteSpace(o.VlmMmprojPath))
        throw new ArgumentException("pdf-stage-eval dùng VLM cần đủ --vlm-model và --vlm-mmproj.");
    var visualRequested = !string.IsNullOrWhiteSpace(o.VlmModelPath) || o.UseNvidiaNim ||
        o.PdfStageVisualReview || o.PdfStageVisualRegions > 0 || o.PdfStageVisualScheduler ||
        !string.IsNullOrWhiteSpace(o.PdfStageVisualProducer);
    using IPdfVisualQuestion? visualAnalyst = !visualRequested
        ? null
        : !string.IsNullOrWhiteSpace(o.VlmModelPath)
            ? await VlmImageQuestion.LoadAsync(o.VlmModelPath!, o.VlmMmprojPath!, o.VlmContextSize, o.VlmGpuLayerCount, ct)
        : o.UseNvidiaNim
            ? new NvidiaNimVisualQuestion(o.Pipeline.Sglang.Endpoint, o.Pipeline.Sglang.ApiKey, o.Pipeline.Sglang.Model,
                o.Pipeline.Sglang.RequestTimeoutSeconds, o.Pipeline.Sglang.TransientRequestRetries)
        : o.Pipeline.Backend == InferenceBackend.OpenRouter
            ? new OpenRouterVisualQuestion(o.Pipeline.OpenRouter.Endpoint, o.Pipeline.OpenRouter.ApiKey, o.Pipeline.OpenRouter.Model)
            : null;
    var rows = new List<object>();
    var hasPartialTimeout = false;
    Exception? terminalFault = null;
    var semanticLaneOptions = new SemanticLaneOptions(
        TimeSpan.FromSeconds(o.PdfStageSemanticRequestTimeoutSeconds),
        TimeSpan.FromSeconds(o.PdfStageSemanticBatchTimeoutSeconds),
        TimeSpan.FromSeconds(o.PdfStageSemanticLaneDeadlineSeconds),
        o.PdfStageSemanticConcurrency);
    try
    {
    foreach (var file in files)
    {
        var stem = Path.GetFileNameWithoutExtension(file);
        var sourceKeyRoot = Path.GetFullPath(o.PdfStageKeyRoot ?? "keys");
        var sourceKeys = keyIndex.TryGetValue(stem, out var paths)
            ? paths.Where(path => path.StartsWith(sourceKeyRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)).ToArray()
            : Array.Empty<string>();
        if (sourceKeys.Length != 1)
        {
            rows.Add(new { file = Path.GetFileName(file), status = sourceKeys.Length == 0 ? "no-source-key" : "ambiguous-source-key" });
            continue;
        }

        var slim = new DocxSlimExtractor(o.Pipeline.Extraction).Extract(file);
        var rawKey = AnswerKey.Load(sourceKeys[0]);
        var sourceDocumentSha256 = FileSha256(file);
        var goldKeySha256 = FileSha256(sourceKeys[0]);
        var stableMap = slim.Paragraphs
            .Where(p => !string.IsNullOrWhiteSpace(p.StableId))
            .ToDictionary(p => p.StableId!, p => p.Index, StringComparer.Ordinal);
        var key = rawKey.HasStableIds ? rawKey.ResolveStableIds(stableMap) : rawKey;
        var truth = key.PositiveEntries.Where(e => !string.IsNullOrWhiteSpace(e.Text)).ToArray();
        if (truth.Length != key.Count)
        {
            rows.Add(new { file = Path.GetFileName(file), status = "key-title-not-measured", key = key.Count, titledKey = truth.Length });
            continue;
        }

        var analystBudget = o.PdfStageAllCandidates ? 0 : o.PdfStageAnalystBlocks;
        var result = await PdfLayoutEvidenceOutline.TryBuildBroadAuditWithAnalystAsync(
            file, slim, analyst, analystBudget, o.PdfStageWideCandidates,
            o.PdfStageSupplementCandidates, visualAnalyst, o.VlmDpi, o.PdfStageVisualRegions, o.PdfStageVisualProducer,
            o.PdfStageVisualScheduler, ct, semanticLaneOptions, o.PdfStageCheckpointPath, o.PdfStageResume,
            o.PdfStageVisualConcurrency, o.PdfStageSemanticHierarchy);
        var audit = result.Audit;
        if (audit is null)
        {
            rows.Add(new { file = Path.GetFileName(file), status = "route-not-applicable", key = key.Count, reason = result.Reason });
            continue;
        }
        hasPartialTimeout |= string.Equals(audit.SemanticLane?.Status, "partial_timeout", StringComparison.Ordinal) ||
                             string.Equals(audit.VisualLane?.Status, "partial_timeout", StringComparison.Ordinal);

        string Canon(string? value) => string.Concat((value ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant));
        var expected = truth.Select(e => Canon(e.Text)).ToArray();
        int Hits(IEnumerable<string> texts) => expected.Count(e => texts.Select(Canon).Any(t => t == e));
        var allCandidates = audit.CandidateBlocks;
        var selected = audit.SelectedCandidateBlocks;
        var headingIds = audit.BlockDecisions
            .Where(d => string.Equals(d.Role, "HeadingTopic", StringComparison.OrdinalIgnoreCase))
            .Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        var roleHeading = selected.Where(b => headingIds.Contains(b.Id)).ToArray();
        var grounded = selected.Where(b => audit.GroundedBlockIds.Contains(b.Id, StringComparer.Ordinal)).ToArray();
        var aligned = selected.Where(b => audit.AlignedBlockIds.Contains(b.Id, StringComparer.Ordinal)).ToArray();
        var stageTraces = audit.CandidateStageTraces;
        var finalOutline = new DocumentOutline
        {
            File = file,
            ParagraphCount = slim.Paragraphs.Count,
            CandidateCount = audit.CandidatesSelected,
            Headings = result.Headings,
            DeterministicRoute = "auto:pdf-layout-block-grounded",
            RouteAudit = audit,
        };
        var score = Evaluator.Score(file, finalOutline, [], key);
        var exact = Hits(result.Headings.Select(h => h.Text));
        // A failed hosted batch is materialized as one Uncertain decision per input so candidate
        // coverage remains auditable. Treat that shape as unavailable only when no raw model reply
        // exists; an actual all-Uncertain reply is still a measured result.
        var semanticPartialTimeout = string.Equals(audit.SemanticLane?.Status, "partial_timeout", StringComparison.Ordinal);
        var semanticUnavailable = !semanticPartialTimeout && audit.BlockDecisions.Count > 0 && audit.RawAnalystResponses.Count == 0 &&
            audit.BlockDecisions.All(decision => string.Equals(decision.Role, "Uncertain", StringComparison.Ordinal));
        var visualAttempts = audit.VisualRecoveries.SelectMany(recovery => recovery.Attempts ?? []).ToArray();
        var visualUnavailable = visualAttempts.Length > 0 && visualAttempts.All(attempt =>
            string.Equals(attempt.Status, "failed", StringComparison.OrdinalIgnoreCase));
        var modelUnavailable = semanticUnavailable || visualUnavailable;
        var visualHttpStatuses = visualAttempts
            .Where(attempt => attempt.HttpStatus.HasValue)
            .Select(attempt => attempt.HttpStatus!.Value)
            .Distinct()
            .ToArray();
        var unavailableHttpStatus = visualUnavailable && visualHttpStatuses.Length == 1
            ? visualHttpStatuses[0]
            : (int?)null;
        var unavailableFailureClass = modelUnavailable
            ? unavailableHttpStatus switch
            {
                402 => "billing",
                401 or 403 => "authentication-or-authorization",
                429 => "rate-limit",
                >= 500 => "provider",
                _ when semanticUnavailable => "semantic-provider-or-contract",
                _ => "transport-or-provider",
            }
            : null;
        var unavailableRetryable = unavailableHttpStatus switch
        {
            402 or 401 or 403 => false,
            408 or 429 or 502 or 503 or 504 => true,
            _ => (bool?)null,
        };
        var levelHits = truth.Count(e => result.Headings.Any(h => Canon(h.Text) == Canon(e.Text) && h.Level == e.Level));
        var markerReconstructed = result.Headings
            .Where(h => h.BoundarySource == "pdf-marker-span-reconstruction")
            .ToArray();
        var visualRecovered = result.Headings
            .Where(h => h.SourceId?.StartsWith("v-", StringComparison.Ordinal) == true)
            .ToArray();
        var visualRecoveredKeyTitles = truth
            .Where(entry => visualRecovered.Any(heading => Canon(heading.Text) == Canon(entry.Text)))
            .Select(entry => entry.Text!)
            .ToArray();
        var missingCandidateTitles = truth
            .Where(e => !allCandidates.Any(b => Canon(b.Text) == Canon(e.Text)))
            .Select(e => e.Text!)
            .ToArray();
        // This is deterministic and intentionally separate from hosted inference. Persist it with
        // the immutable per-region facts so a changed key/metric can be replayed offline.
        var visualRepresentation = visualAnalyst is null
            ? null
            : PdfVisualRepresentationAudit.Evaluate(PdfTextbookOutline.FindSiblingPdf(file)!, slim, key);
        rows.Add(new
        {
            file = Path.GetFileName(file), status = modelUnavailable ? "model-unavailable" : semanticPartialTimeout ? "partial_timeout" : "measured", key = key.Count,
            modelUnavailable,
            availability = new
            {
                state = modelUnavailable ? "unavailable" : "available",
                failureClass = unavailableFailureClass,
                httpStatus = unavailableHttpStatus,
                retryable = modelUnavailable ? unavailableRetryable : null,
                semanticUnavailable,
                semanticPartialTimeout,
                visual = new
                {
                    attempts = visualAttempts.Length,
                    succeeded = visualAttempts.Count(attempt => string.Equals(attempt.Status, "success", StringComparison.OrdinalIgnoreCase)),
                    failed = visualAttempts.Count(attempt => string.Equals(attempt.Status, "failed", StringComparison.OrdinalIgnoreCase)),
                },
            },
            fingerprints = new
            {
                sourceDocumentSha256,
                goldKeySha256,
                model = analyst.ModelName,
                promptProfile = $"pdf-stage:{(o.PdfStageWideCandidates ? "wide" : "broad")}{(o.PdfStageSupplementCandidates ? "+supplement" : "")}:visual-scheduler={o.PdfStageVisualScheduler}",
                promptSha256 = PdfStagePromptProfile.SemanticPromptSha256,
                semanticConcurrency = o.PdfStageSemanticConcurrency,
                semanticRequestTimeoutSeconds = o.PdfStageSemanticRequestTimeoutSeconds,
                semanticBatchTimeoutSeconds = o.PdfStageSemanticBatchTimeoutSeconds,
                semanticLaneDeadlineSeconds = o.PdfStageSemanticLaneDeadlineSeconds,
                visualRegionBudget = o.PdfStageVisualRegions,
                visualRegionIdsSha256 = HashText(string.Join("\n", audit.VisualRecoveries
                    .Where(trace => trace.Attempts is { Count: > 0 })
                    .Select(trace => trace.RegionId).OrderBy(id => id, StringComparer.Ordinal))),
                resume = o.PdfStageResume,
            },
            lanes = new { semantic = audit.SemanticLane, visual = audit.VisualLane },
            rawCandidateRecall = new { hits = Hits(allCandidates.Select(b => b.Text)), total = key.Count, candidates = allCandidates.Count },
            analystCoverage = new { hits = Hits(selected.Select(b => b.Text)), total = key.Count, selected = selected.Count, available = audit.CandidatesAvailable },
            vlmRole = new { hits = Hits(roleHeading.Select(b => b.Text)), selected = roleHeading.Length, precision = roleHeading.Length == 0 ? (double?)null : Hits(roleHeading.Select(b => b.Text)) / (double)roleHeading.Length },
            pdfGrounding = new { hits = Hits(grounded.Select(b => b.Text)), total = key.Count, grounded = grounded.Length },
            docxAlignment = new { hits = Hits(aligned.Select(b => b.Text)), total = key.Count, aligned = aligned.Length },
            validation = new
            {
                proposed = stageTraces.Count(trace => trace.SemanticRole is not ("unknown" or "Uncertain")),
                semanticHeading = audit.BlockDecisions.Count(decision =>
                    string.Equals(decision.Role, "HeadingTopic", StringComparison.OrdinalIgnoreCase)),
                eligible = stageTraces.Count(trace => trace.ValidationStatus == "eligible"),
                unresolved = stageTraces.Count(trace => trace.ValidationStatus == "unresolved"),
                invalidSpan = stageTraces.Count(trace => trace.SpanStatus == "invalid"),
                scopeConflict = stageTraces.Count(trace => trace.Reason == "scope-conflict"),
            },
            titleExact = new { hits = exact, total = key.Count },
            markerSpanReconstruction = new
            {
                headings = markerReconstructed.Length,
                titleExact = Hits(markerReconstructed.Select(h => h.Text)),
            },
            visualProposal = new
            {
                queried = audit.VisualEvidence.Count,
                neighborhood = new
                {
                    minAbove = audit.VisualEvidence.Count == 0 ? 0 : audit.VisualEvidence.Min(item => item.ContextLinesAbove),
                    maxAbove = audit.VisualEvidence.Count == 0 ? 0 : audit.VisualEvidence.Max(item => item.ContextLinesAbove),
                    minBelow = audit.VisualEvidence.Count == 0 ? 0 : audit.VisualEvidence.Min(item => item.ContextLinesBelow),
                    maxBelow = audit.VisualEvidence.Count == 0 ? 0 : audit.VisualEvidence.Max(item => item.ContextLinesBelow),
                },
                roles = audit.VisualEvidence.GroupBy(item => item.Role)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .Select(group => new { role = group.Key, count = group.Count() }).ToArray(),
                evidence = o.Pipeline.ShowRawOutput ? audit.VisualEvidence : null,
            },
            proposalResolution = new
            {
                decisions = audit.ProposalResolutions.GroupBy(item => item.Resolution)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .Select(group => new { resolution = group.Key, count = group.Count() }).ToArray(),
                items = o.Pipeline.ShowRawOutput ? audit.ProposalResolutions : null,
            },
            semanticHierarchy = new
            {
                proposals = audit.HierarchyProposals.Count,
                decisions = audit.HierarchyProposals.GroupBy(item => item.Resolution)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .Select(group => new { resolution = group.Key, count = group.Count() }).ToArray(),
                items = o.Pipeline.ShowRawOutput ? audit.HierarchyProposals : null,
            },
            hierarchyFacts = new
            {
                validatedHeadingCoverage = audit.HierarchyFacts.Count,
                markerPathFacts = audit.HierarchyFacts.Count(item => item.MarkerPath is not null),
                markerPathDepthMismatch = audit.HierarchyFacts.Count(item =>
                    item.MarkerIsPath && item.MarkerDepth is not null && item.MarkerPath is { } path &&
                    path.Split('.').Length != item.MarkerDepth.Value),
                markerFamilies = audit.HierarchyFacts.GroupBy(item => item.MarkerFamily ?? "none")
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .Select(group => new { markerFamily = group.Key, count = group.Count() }).ToArray(),
                scopes = audit.HierarchyFacts.GroupBy(item => item.StructuralScope)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .Select(group => new { scope = group.Key, count = group.Count() }).ToArray(),
                regimes = audit.HierarchyFacts.GroupBy(item => item.DocumentRegime)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .Select(group => new { regime = group.Key, count = group.Count() }).ToArray(),
                deterministicLevelCoverage = audit.HierarchyFacts.Count(item => item.ResolvedLevel is not null),
                deterministicParentCoverage = audit.HierarchyFacts.Count(item => item.MarkerPrefixParentCandidate is not null),
                unresolvedRelationships = audit.HierarchyFacts.Count(item => item.ParentResolution == "relationship_unresolved"),
                conflicts = new
                {
                    state = "not_measured",
                    count = (int?)null,
                },
                items = audit.HierarchyFacts,
            },
            textLayerRecovery = new
            {
                items = audit.TextLayerRecoveries.GroupBy(item => item.Status)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .Select(group => new { status = group.Key, count = group.Count() }).ToArray(),
                validatedHeadings = visualRecovered.Length,
                recoveredKeyTitles = visualRecoveredKeyTitles,
                missingKeyRecovered = missingCandidateTitles
                    .Where(title => visualRecoveredKeyTitles.Any(recovered => Canon(recovered) == Canon(title)))
                    .ToArray(),
            },
            levelAccuracy = new { hits = levelHits, total = key.Count },
            final = semanticPartialTimeout
                ? new { state = "not-measured-partial-run", result = result.Headings.Count, precision = (double?)null, recall = (double?)null, f1 = (double?)null, nav = (double?)null, navLevel = (double?)null }
                : new { state = "measured", result = result.Headings.Count, precision = (double?)score.Precision, recall = (double?)score.Recall, f1 = (double?)score.F1, nav = (double?)score.NavigationRecall, navLevel = (double?)score.NavigationLevelAccuracy },
            rawAnalystResponses = o.Pipeline.ShowRawOutput ? audit.RawAnalystResponses : null,
            modelInputContracts = o.Pipeline.ShowRawOutput ? audit.ModelInputContracts : null,
            candidateKeyTitles = o.Pipeline.ShowRawOutput
                ? truth.Where(e => allCandidates.Any(b => Canon(b.Text) == Canon(e.Text))).Select(e => e.Text).ToArray()
                : null,
            analystVisibleKeyTitles = o.Pipeline.ShowRawOutput
                ? truth.Where(e => selected.Any(b => Canon(b.Text) == Canon(e.Text))).Select(e => e.Text).ToArray()
                : null,
            candidateStageTraces = o.Pipeline.ShowRawOutput ? stageTraces : null,
            visualInferenceArtifact = visualAnalyst is null ? null : new
            {
                schemaVersion = 1,
                recoveries = audit.VisualRecoveries,
                representation = visualRepresentation!.Entries,
            },
            missingCandidateTitles,
            retrievalTrace = PdfLayoutEvidenceOutline.TraceCandidateRetrieval(file, missingCandidateTitles),
        });
    }
    }
    catch (Exception ex)
    {
        terminalFault = ex;
        throw;
    }
    finally
    {
        if (o.OutputPath is not null)
        {
            var checkpointManifest = JsonSerializer.Serialize(new
            {
                runStatus = terminalFault is not null ? "pipeline_fault" : hasPartialTimeout ? "partial_timeout" : "complete",
                rows,
            }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            await File.WriteAllTextAsync(Path.GetFullPath(o.OutputPath), checkpointManifest, new UTF8Encoding(false), CancellationToken.None);
        }
    }

    var json = JsonSerializer.Serialize(new
    {
        runStatus = hasPartialTimeout ? "partial_timeout" : "complete",
        rows,
    }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    if (o.OutputPath is null) Console.WriteLine(json);
    else await File.WriteAllTextAsync(Path.GetFullPath(o.OutputPath), json, new UTF8Encoding(false), ct);
    return 0;
}

// M8.1a evaluation-input producer. It is gold-free by construction: it takes no key option, never
// builds a key index, and never constructs an AnswerKey. That is what makes usesGold=false a
// structural property of the command rather than a self-declared field.
static async Task<int> RunPdfHierarchyFactsAsync(CommandLineOptions o, CancellationToken ct)
{
    if (o.Pipeline.DisableLlm)
    {
        Console.Error.WriteLine("pdf-hierarchy-facts cần LLM analyst; bỏ --no-llm và chỉ định --sglang/--model.");
        return 2;
    }

    var files = ExpandCalibrationInputs(o.Inputs);
    if (files.Count == 0) { Console.Error.WriteLine("Không tìm thấy tài liệu để dựng hierarchy facts."); return 2; }

    using var analyst = await CreateClassifierAsync(o, ct);
    var semanticLaneOptions = new SemanticLaneOptions(
        TimeSpan.FromSeconds(o.PdfStageSemanticRequestTimeoutSeconds),
        TimeSpan.FromSeconds(o.PdfStageSemanticBatchTimeoutSeconds),
        TimeSpan.FromSeconds(o.PdfStageSemanticLaneDeadlineSeconds),
        o.PdfStageSemanticConcurrency);
    var analystBudget = o.PdfStageAllCandidates ? 0 : o.PdfStageAnalystBlocks;
    var routeConfig = string.Join("|",
        $"analystBudget={analystBudget}",
        $"wide={o.PdfStageWideCandidates}",
        $"supplement={o.PdfStageSupplementCandidates}",
        $"semanticHierarchy={o.PdfStageSemanticHierarchy}",
        $"semanticConcurrency={o.PdfStageSemanticConcurrency}");

    var rows = new List<PdfHierarchyFactsRow>();
    var skipped = new List<object>();
    foreach (var file in files)
    {
        if (!o.Quiet) Console.Error.WriteLine($"» PDF hierarchy facts: {Path.GetFileName(file)}");
        var slim = new DocxSlimExtractor(o.Pipeline.Extraction).Extract(file);
        // Visual analyst stays out: M8.1a freezes the deterministic semantic route only.
        var result = await PdfLayoutEvidenceOutline.TryBuildBroadAuditWithAnalystAsync(
            file, slim, analyst, analystBudget, o.PdfStageWideCandidates, o.PdfStageSupplementCandidates,
            null, o.VlmDpi, 0, null, false, ct, semanticLaneOptions, null, false, 1,
            o.PdfStageSemanticHierarchy);
        if (result.Audit is not { } audit)
        {
            skipped.Add(new { file = Path.GetFileName(file), status = "route-not-applicable", reason = result.Reason });
            continue;
        }

        var row = PdfHierarchyFactsArtifact.BuildRow(Path.GetFileName(file), FileSha256(file), audit.HierarchyFacts,
            audit.ValidatedStructures, PdfCanonicalGrounding.FromGroundedHeadings(result.Headings));
        rows.Add(row);
        if (!o.Quiet)
            Console.Error.WriteLine(
                $"  validated={row.Counters.ValidatedHeadings} markerPath={row.Counters.MarkerPathFacts} " +
                $"levelResolved={row.Counters.DeterministicLevelResolved} " +
                $"parentResolved={row.Counters.DeterministicParentResolved} " +
                $"fingerprint={row.OccurrenceFingerprint[..12]}");
    }

    var envelope = new PdfHierarchyFactsArtifactEnvelope(
        new PdfHierarchyFactsGeneration(
            Environment.GetEnvironmentVariable("DHX_CODE_REVISION"),
            o.Pipeline.Backend.ToString(),
            o.Pipeline.Backend switch
            {
                InferenceBackend.OpenRouter => o.Pipeline.OpenRouter.Model,
                InferenceBackend.Sglang => o.Pipeline.Sglang.Model,
                InferenceBackend.LmStudio => o.Pipeline.LmStudio.Model,
                _ => o.Pipeline.Llama.ModelPath,
            },
            PdfStagePromptProfile.SemanticPromptSha256,
            HashText(routeConfig)),
        rows);
    var json = JsonSerializer.Serialize(new
    {
        envelope.SchemaVersion,
        envelope.ArtifactKind,
        envelope.UsesGold,
        envelope.Generation,
        envelope.Rows,
        skipped,
    }, new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });
    if (o.OutputPath is null) Console.WriteLine(json);
    else await File.WriteAllTextAsync(Path.GetFullPath(o.OutputPath), json, new UTF8Encoding(false), ct);
    return rows.Count == 0 ? 1 : 0;
}

// M8.1d-3 counterfactual audit. Offline and gold-free by construction: it accepts no key option,
// reads only a frozen facts artifact, and writes no decision back into hierarchy authority.
static int RunPdfHierarchyMarkerCounterfactual(CommandLineOptions o)
{
    if (o.Inputs.Count != 1 || !File.Exists(o.Inputs[0]))
    {
        Console.Error.WriteLine("pdf-hierarchy-marker-counterfactual cần đúng một frozen facts artifact JSON.");
        return 2;
    }

    var report = PdfMarkerAncestryCounterfactual.Evaluate(File.ReadAllText(o.Inputs[0]));
    var payload = new
    {
        artifact = Path.GetFileName(o.Inputs[0]),
        usesGold = false,
        report,
    };
    var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });
    if (o.OutputPath is null) Console.WriteLine(json);
    else
    {
        var output = Path.GetFullPath(o.OutputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, json, new UTF8Encoding(false));
    }
    return 0;
}

static string FileSha256(string path) =>
    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

static string HashText(string value) =>
    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

static int RunPdfFirstLossAudit(CommandLineOptions o)
{
    var files = ExpandCalibrationInputs(o.Inputs);
    var keyIndex = BuildKeyIndex(files, o.PdfStageKeyRoot);
    if (files.Count == 0) { Console.Error.WriteLine("Không tìm thấy DOCX để audit first loss PDF."); return 2; }

    var sourceKeyRoot = Path.GetFullPath(o.PdfStageKeyRoot ?? "keys");
    var rows = new List<object>();
    foreach (var file in files)
    {
        var stem = Path.GetFileNameWithoutExtension(file);
        var sourceKeys = keyIndex.TryGetValue(stem, out var paths)
            ? paths.Where(path => path.StartsWith(sourceKeyRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)).ToArray()
            : Array.Empty<string>();
        if (sourceKeys.Length != 1)
        {
            rows.Add(new { file = Path.GetFileName(file), status = sourceKeys.Length == 0 ? "no-source-key" : "ambiguous-source-key" });
            continue;
        }

        var slim = new DocxSlimExtractor(o.Pipeline.Extraction).Extract(file);
        var rawKey = AnswerKey.Load(sourceKeys[0]);
        var stableMap = slim.Paragraphs.Where(p => !string.IsNullOrWhiteSpace(p.StableId))
            .ToDictionary(p => p.StableId!, p => p.Index, StringComparer.Ordinal);
        var key = rawKey.HasStableIds ? rawKey.ResolveStableIds(stableMap) : rawKey;
        var report = PdfFirstLossAudit.Evaluate(file, slim, key, o.PdfStageAnalystBlocks);
        rows.Add(new
        {
            file = Path.GetFileName(file),
            pdf = PdfTextbookOutline.FindSiblingPdf(file) is { } pdf ? Path.GetFileName(pdf) : null,
            key = Path.GetFileName(sourceKeys[0]),
            report,
        });
    }

    var json = JsonSerializer.Serialize(rows, new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });
    if (o.OutputPath is null) Console.WriteLine(json);
    else
    {
        var output = Path.GetFullPath(o.OutputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, json, new UTF8Encoding(false));
    }
    return 0;
}

static int RunPdfOccurrenceEval(CommandLineOptions o)
{
    var files = ExpandCalibrationInputs(o.Inputs);
    var keyIndex = BuildKeyIndex(files, o.PdfStageKeyRoot);
    if (files.Count == 0) { Console.Error.WriteLine("Không tìm thấy DOCX để evaluation occurrence PDF."); return 2; }

    var sourceKeyRoot = Path.GetFullPath(o.PdfStageKeyRoot ?? "keys");
    var rows = new List<object>();
    foreach (var file in files)
    {
        var stem = Path.GetFileNameWithoutExtension(file);
        var sourceKeys = keyIndex.TryGetValue(stem, out var paths)
            ? paths.Where(path => path.StartsWith(sourceKeyRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)).ToArray()
            : Array.Empty<string>();
        if (sourceKeys.Length != 1)
        {
            rows.Add(new { file = Path.GetFileName(file), status = sourceKeys.Length == 0 ? "no-source-key" : "ambiguous-source-key" });
            continue;
        }

        var slim = new DocxSlimExtractor(o.Pipeline.Extraction).Extract(file);
        var rawKey = AnswerKey.Load(sourceKeys[0]);
        var stableMap = slim.Paragraphs.Where(p => !string.IsNullOrWhiteSpace(p.StableId))
            .ToDictionary(p => p.StableId!, p => p.Index, StringComparer.Ordinal);
        var key = rawKey.HasStableIds ? rawKey.ResolveStableIds(stableMap) : rawKey;
        var firstLoss = PdfFirstLossAudit.Evaluate(file, slim, key, o.PdfStageAnalystBlocks);
        var occurrence = PdfGoldOccurrenceEvaluator.Evaluate(slim, key, firstLoss);
        rows.Add(new
        {
            file = Path.GetFileName(file),
            key = Path.GetFileName(sourceKeys[0]),
            evaluationOnly = true,
            firstLoss,
            occurrence,
        });
    }

    var json = JsonSerializer.Serialize(rows, new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });
    if (o.OutputPath is null) Console.WriteLine(json);
    else
    {
        var output = Path.GetFullPath(o.OutputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, json, new UTF8Encoding(false));
    }
    return 0;
}

static int RunPdfOccurrenceCounterfactualEval(CommandLineOptions o)
{
    var files = ExpandCalibrationInputs(o.Inputs);
    var keyIndex = BuildKeyIndex(files, o.PdfStageKeyRoot);
    if (files.Count == 0) { Console.Error.WriteLine("Không tìm thấy DOCX để evaluation occurrence PDF."); return 2; }

    var sourceKeyRoot = Path.GetFullPath(o.PdfStageKeyRoot ?? "keys");
    var rows = new List<object>();
    foreach (var file in files)
    {
        var stem = Path.GetFileNameWithoutExtension(file);
        var sourceKeys = keyIndex.TryGetValue(stem, out var paths)
            ? paths.Where(path => path.StartsWith(sourceKeyRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)).ToArray()
            : Array.Empty<string>();
        if (sourceKeys.Length != 1)
        {
            rows.Add(new { file = Path.GetFileName(file), status = sourceKeys.Length == 0 ? "no-source-key" : "ambiguous-source-key" });
            continue;
        }

        var slim = new DocxSlimExtractor(o.Pipeline.Extraction).Extract(file);
        var rawKey = AnswerKey.Load(sourceKeys[0]);
        var stableMap = slim.Paragraphs.Where(p => !string.IsNullOrWhiteSpace(p.StableId))
            .ToDictionary(p => p.StableId!, p => p.Index, StringComparer.Ordinal);
        var key = rawKey.HasStableIds ? rawKey.ResolveStableIds(stableMap) : rawKey;
        var firstLoss = PdfFirstLossAudit.Evaluate(file, slim, key, o.PdfStageAnalystBlocks);
        var goldOccurrence = PdfGoldOccurrenceEvaluator.Evaluate(slim, key, firstLoss);
        // The resolver receives only the feature-only PDF ranking. Gold data is consumed below
        // exclusively by the counterfactual evaluator.
        var productionOccurrence = PdfProductionOccurrenceResolver.Resolve(
            PdfLayoutEvidenceOutline.BuildCandidateRankingAudit(file).Candidates);
        var counterfactual = PdfOccurrenceCounterfactualEvaluator.Evaluate(goldOccurrence, productionOccurrence);
        rows.Add(new
        {
            file = Path.GetFileName(file),
            key = Path.GetFileName(sourceKeys[0]),
            productionResolverUsesGold = false,
            productionOccurrence,
            evaluationOnly = new { goldOccurrence, counterfactual },
        });
    }

    var json = JsonSerializer.Serialize(rows, new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });
    if (o.OutputPath is null) Console.WriteLine(json);
    else
    {
        var output = Path.GetFullPath(o.OutputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, json, new UTF8Encoding(false));
    }
    return 0;
}

static int RunPdfCandidateConstructionAudit(CommandLineOptions o)
{
    var files = ExpandCalibrationInputs(o.Inputs);
    var keyIndex = BuildKeyIndex(files, o.PdfStageKeyRoot);
    if (files.Count == 0) { Console.Error.WriteLine("Không tìm thấy DOCX để audit candidate construction PDF."); return 2; }

    var sourceKeyRoot = Path.GetFullPath(o.PdfStageKeyRoot ?? "keys");
    var rows = new List<object>();
    foreach (var file in files)
    {
        var stem = Path.GetFileNameWithoutExtension(file);
        var sourceKeys = keyIndex.TryGetValue(stem, out var paths)
            ? paths.Where(path => path.StartsWith(sourceKeyRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)).ToArray()
            : Array.Empty<string>();
        if (sourceKeys.Length != 1)
        {
            rows.Add(new { file = Path.GetFileName(file), status = sourceKeys.Length == 0 ? "no-source-key" : "ambiguous-source-key" });
            continue;
        }

        var slim = new DocxSlimExtractor(o.Pipeline.Extraction).Extract(file);
        var rawKey = AnswerKey.Load(sourceKeys[0]);
        var stableMap = slim.Paragraphs.Where(p => !string.IsNullOrWhiteSpace(p.StableId))
            .ToDictionary(p => p.StableId!, p => p.Index, StringComparer.Ordinal);
        var key = rawKey.HasStableIds ? rawKey.ResolveStableIds(stableMap) : rawKey;
        var firstLoss = PdfFirstLossAudit.Evaluate(file, slim, key, o.PdfStageAnalystBlocks);
        var targets = firstLoss.Entries.Where(entry => entry.FirstLoss is "semantic_block_grouping" or "candidate_producer")
            .Select(entry => entry.Gold).ToArray();
        var construction = PdfLayoutEvidenceOutline.TraceCandidateConstruction(file, targets)
            .ToDictionary(trace => trace.ExpectedText, StringComparer.Ordinal);
        rows.Add(new
        {
            file = Path.GetFileName(file),
            key = Path.GetFileName(sourceKeys[0]),
            diagnosticOnly = true,
            targets = firstLoss.Entries.Where(entry => entry.FirstLoss is "semantic_block_grouping" or "candidate_producer")
                .Select(entry => new { entry.Ordinal, entry.Gold, entry.FirstLoss, construction = construction.GetValueOrDefault(entry.Gold) })
                .ToArray(),
        });
    }

    var json = JsonSerializer.Serialize(rows, new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });
    if (o.OutputPath is null) Console.WriteLine(json);
    else
    {
        var output = Path.GetFullPath(o.OutputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, json, new UTF8Encoding(false));
    }
    return 0;
}

// M7.23: this is deliberately source-only routing. Gold keys are not read here;
// they can only be applied later by an offline evaluator over this artifact.
static async Task<int> RunPdfSemanticRecoveryEvalAsync(CommandLineOptions o, CancellationToken ct)
{
    var files = ExpandCalibrationInputs(o.Inputs);
    if (files.Count == 0)
    {
        Console.Error.WriteLine("Không tìm thấy DOCX để chạy PDF semantic recovery.");
        return 2;
    }

    if (o.Pipeline.DisableLlm)
    {
        Console.Error.WriteLine("pdf-semantic-recovery-eval cần semantic model; bỏ --no-llm.");
        return 2;
    }

    var rows = new List<object>();
    using var analyst = await CreateClassifierAsync(o, ct);
    foreach (var file in files)
    {
        if (!o.Quiet)
            Console.Error.WriteLine($"» PDF semantic recovery: {Path.GetFileName(file)}");

        var recoveryOptions = PdfSemanticRecoveryOptions.Parse(o.PdfSemanticRecoveryProfile);
        var report = await PdfLayoutEvidenceOutline.RunSemanticRecoveryAuditAsync(file, analyst, recoveryOptions, ct);
        rows.Add(new
        {
            file = Path.GetFileName(file),
            sourceDocumentSha256 = FileSha256(file),
            usesGold = false,
            backend = o.Pipeline.Backend.ToString(),
            semanticRecoveryProfile = recoveryOptions.Name,
            report,
        });

        if (!o.Quiet)
            Console.Error.WriteLine(
                $"  status={report.Status} eligible={report.EligibleUnresolvedBlocks} " +
                $"proposed={report.HeadingRoleProposals} canonicalUnique={report.CanonicalUniqueProposals} " +
                $"validated={report.ValidatorAccepted}");
    }

    var json = JsonSerializer.Serialize(rows.Count == 1 ? rows[0] : rows, new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });
    if (o.OutputPath is null) Console.WriteLine(json);
    else
    {
        var output = Path.GetFullPath(o.OutputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, json, new UTF8Encoding(false), ct);
        if (!o.Quiet) Console.Error.WriteLine($"Đã ghi: {output}");
    }

    return 0;
}

static int RunPdfSemanticRecoveryResultEval(CommandLineOptions o)
{
    if (o.Inputs.Count != 1 || !File.Exists(o.Inputs[0]))
    {
        Console.Error.WriteLine("pdf-semantic-recovery-result-eval cần đúng một recovery artifact JSON.");
        return 2;
    }
    if (string.IsNullOrWhiteSpace(o.PdfVisualRepresentationGoldPath) || !File.Exists(o.PdfVisualRepresentationGoldPath))
    {
        Console.Error.WriteLine("Cần --gold <rebased.key> cho evaluation offline.");
        return 2;
    }
    if (string.IsNullOrWhiteSpace(o.PdfSemanticRecoveryBaselineArtifact) || !File.Exists(o.PdfSemanticRecoveryBaselineArtifact))
    {
        Console.Error.WriteLine("Cần --recovery-baseline-artifact <occurrence.json> đã đóng băng.");
        return 2;
    }

    var result = PdfSemanticRecoveryArtifactEvaluator.Evaluate(
        File.ReadAllText(o.Inputs[0]),
        File.ReadAllText(o.PdfSemanticRecoveryBaselineArtifact),
        AnswerKey.Load(o.PdfVisualRepresentationGoldPath));
    var payload = new
    {
        artifact = Path.GetFileName(o.Inputs[0]),
        gold = Path.GetFileName(o.PdfVisualRepresentationGoldPath),
        baselineArtifact = Path.GetFileName(o.PdfSemanticRecoveryBaselineArtifact),
        result,
    };
    var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });
    if (o.OutputPath is null) Console.WriteLine(json);
    else
    {
        var output = Path.GetFullPath(o.OutputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, json, new UTF8Encoding(false));
    }
    return 0;
}

static int RunPdfHierarchyFactsEval(CommandLineOptions o)
{
    if (o.Inputs.Count != 1 || !File.Exists(o.Inputs[0]))
    {
        Console.Error.WriteLine("pdf-hierarchy-facts-eval cần đúng một frozen route artifact JSON.");
        return 2;
    }
    if (string.IsNullOrWhiteSpace(o.PdfHierarchyGoldPath) || !File.Exists(o.PdfHierarchyGoldPath))
    {
        Console.Error.WriteLine("Cần --hierarchy-gold <gold.json> với occurrence-stable hierarchy gold.");
        return 2;
    }

    var result = PdfHierarchyFactsArtifactEvaluator.Evaluate(
        File.ReadAllText(o.Inputs[0]), File.ReadAllText(o.PdfHierarchyGoldPath));
    var payload = new
    {
        artifact = Path.GetFileName(o.Inputs[0]),
        hierarchyGold = Path.GetFileName(o.PdfHierarchyGoldPath),
        result,
    };
    var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });
    if (o.OutputPath is null) Console.WriteLine(json);
    else
    {
        var output = Path.GetFullPath(o.OutputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, json, new UTF8Encoding(false));
    }
    return 0;
}

static int RunPdfRankEval(CommandLineOptions o)
{
    var files = ExpandCalibrationInputs(o.Inputs);
    var keyIndex = BuildKeyIndex(files, o.PdfStageKeyRoot);
    if (files.Count == 0) { Console.Error.WriteLine("Không tìm thấy DOCX để chấm PDF ranking."); return 2; }

    string Canon(string? value) => string.Concat((value ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant));
    bool Matches(string candidate, string expected)
    {
        var left = Canon(candidate);
        var right = Canon(expected);
        return left == right || (right.Length >= 12 && left.Contains(right, StringComparison.Ordinal));
    }

    var rows = new List<object>();
    foreach (var file in files)
    {
        var stem = Path.GetFileNameWithoutExtension(file);
        var sourceKeyRoot = Path.GetFullPath(o.PdfStageKeyRoot ?? "keys");
        var sourceKeys = keyIndex.TryGetValue(stem, out var paths)
            ? paths.Where(path => path.StartsWith(sourceKeyRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)).ToArray()
            : Array.Empty<string>();
        if (sourceKeys.Length != 1)
        {
            rows.Add(new { file = Path.GetFileName(file), status = sourceKeys.Length == 0 ? "no-source-key" : "ambiguous-source-key" });
            continue;
        }

        var slim = new DocxSlimExtractor(o.Pipeline.Extraction).Extract(file);
        var rawKey = AnswerKey.Load(sourceKeys[0]);
        var stableMap = slim.Paragraphs.Where(p => !string.IsNullOrWhiteSpace(p.StableId))
            .ToDictionary(p => p.StableId!, p => p.Index, StringComparer.Ordinal);
        var key = rawKey.HasStableIds ? rawKey.ResolveStableIds(stableMap) : rawKey;
        var truth = key.PositiveEntries.Where(entry => !string.IsNullOrWhiteSpace(entry.Text)).ToArray();
        if (truth.Length != key.Count)
        {
            rows.Add(new { file = Path.GetFileName(file), status = "key-title-not-measured", key = key.Count, titledKey = truth.Length });
            continue;
        }

        var ranking = PdfLayoutEvidenceOutline.BuildCandidateRankingAudit(file);
        if (ranking.Status != "ranked")
        {
            rows.Add(new { file = Path.GetFileName(file), status = ranking.Status, candidates = ranking.CandidateCount });
            continue;
        }

        var cutoffs = new[] { 25, 50, 100, 200, 400, 800, ranking.CandidateCount }.Distinct().Order().ToArray();
        var ranked = ranking.Candidates;
        var recall = cutoffs.Select(cutoff => new
        {
            k = cutoff,
            hits = truth.Count(entry => ranked.Take(cutoff).Any(candidate => Matches(candidate.Text, entry.Text!))),
            total = truth.Length,
        }).ToArray();
        var poolHits = truth.Where(entry => ranked.Any(candidate => Matches(candidate.Text, entry.Text!))).ToArray();
        var poolMisses = truth.Where(entry => !ranked.Any(candidate => Matches(candidate.Text, entry.Text!)))
            .Select(entry => entry.Text).ToArray();
        rows.Add(new
        {
            file = Path.GetFileName(file),
            status = "measured",
            key = key.Count,
            parserObservable = new { hits = poolHits.Length, total = truth.Length, missingTitles = poolMisses },
            candidatePool = new { count = ranking.CandidateCount },
            recallAt = recall,
            tierCounts = ranked.GroupBy(candidate => candidate.Tier.ToString()).ToDictionary(group => group.Key, group => group.Count()),
            candidates = ranked,
        });
    }

    var json = JsonSerializer.Serialize(rows, new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });
    if (o.OutputPath is null) Console.WriteLine(json);
    else File.WriteAllText(Path.GetFullPath(o.OutputPath), json, new UTF8Encoding(false));
    return 0;
}

static async Task<int> RunPdfClustersAsync(CommandLineOptions o, CancellationToken ct)
{
    var files = ExpandPdfClusterInputs(o.Inputs);
    if (files.Count == 0)
    {
        Console.Error.WriteLine("Không tìm thấy PDF/DOCX để chạy pdf-clusters.");
        return 2;
    }

    var outputPath = o.OutputPath is null ? null : Path.GetFullPath(o.OutputPath);
    if (outputPath is not null)
    {
        var parent = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
    }

    IHeaderClassifier? analyst = null;
    if (!o.Pipeline.DisableLlm)
        analyst = await CreateClassifierAsync(o, ct);

    VlmImageQuestion? visualAnalyst = null;
    if (!string.IsNullOrWhiteSpace(o.VlmModelPath) || !string.IsNullOrWhiteSpace(o.VlmMmprojPath))
    {
        if (string.IsNullOrWhiteSpace(o.VlmModelPath) || string.IsNullOrWhiteSpace(o.VlmMmprojPath))
        {
            Console.Error.WriteLine("Muốn dùng VLM cho pdf-clusters thì cần đủ --vlm-model và --vlm-mmproj.");
            return 2;
        }

        if (!o.Quiet) Console.Error.WriteLine("Đang nạp VLM visual analyst cho candidate blocks...");
        visualAnalyst = await VlmImageQuestion.LoadAsync(
            o.VlmModelPath, o.VlmMmprojPath, o.VlmContextSize, o.VlmGpuLayerCount, ct);
        if (!o.Quiet) Console.Error.WriteLine("Đã nạp VLM visual analyst.");
    }

    var reports = new List<PdfClusterProbeReport>();
    using (analyst)
    using (visualAnalyst)
    {
        foreach (var file in files)
        {
            if (!o.Quiet) Console.Error.WriteLine($"» PDF clusters: {Path.GetFileName(file)}");
            var report = await PdfClusterProbe.RunAsync(file, analyst, visualAnalyst, o.VlmDpi, ct);
            reports.Add(report);
            if (!o.Quiet)
                Console.Error.WriteLine(
                    $"  status={report.Status} clusters={report.Clusters.Count} decisions={report.Decisions.Count} " +
                    $"visual={report.VisualBlockDecisions.Count} pdf={report.Pdf}");
        }
    }

    var payload = reports.Count == 1 ? (object)reports[0] : reports;
    var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    if (outputPath is null) Console.WriteLine(json);
    else
    {
        await File.WriteAllTextAsync(outputPath, json, new UTF8Encoding(false), ct);
        if (!o.Quiet) Console.Error.WriteLine($"Đã ghi: {outputPath}");
    }

    return 0;
}

static async Task<IHeaderClassifier> CreateClassifierAsync(CommandLineOptions o, CancellationToken ct)
{
    switch (o.Pipeline.Backend)
    {
        case InferenceBackend.OpenRouter:
            return OpenRouterHeaderExtractor.CreateOwned(o.Pipeline.OpenRouter);
        case InferenceBackend.LmStudio:
            return LmStudioHeaderExtractor.CreateOwned(o.Pipeline.LmStudio);
        case InferenceBackend.Sglang:
            return SglangHeaderExtractor.CreateOwned(o.Pipeline.Sglang);
        default:
            if (string.IsNullOrWhiteSpace(o.Pipeline.Llama.ModelPath))
            {
                var found = ModelLocator.Locate();
                if (found is null)
                    throw new InvalidOperationException(
                        "Chưa có mô hình cho pdf-clusters analyst. Dùng --no-llm để chỉ dump cluster samples, hoặc chỉ định --model/--lmstudio/--openrouter.");
                o.Pipeline.Llama.ModelPath = found;
            }
            o.Pipeline.PrepareLocalModelProfile();
            return await LlamaHeaderExtractor.LoadAsync(o.Pipeline.Llama, ct);
    }
}

static IReadOnlyDictionary<string, IReadOnlyList<string>> BuildKeyIndex(IReadOnlyList<string> files, string? additionalKeyRoot = null)
{
    var keys = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
    void AddStem(string stem, string fullPath)
    {
        if (!keys.TryGetValue(stem, out var list))
            keys[stem] = list = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        list.Add(fullPath);
    }
    void AddKey(string path)
    {
        if (!File.Exists(path)) return;
        var stem = Path.GetFileNameWithoutExtension(path);
        var fullPath = Path.GetFullPath(path);
        AddStem(stem, fullPath);

        // Rebased evaluation keys keep their source stem plus a versioned suffix. The route audit
        // still receives the original DOCX stem, so index the alias without duplicating/replacing
        // either key. Filtering by --pdf-stage-key-root below keeps source authority explicit.
        const string regeneratedSuffix = "-regenerated-docx";
        if (!stem.EndsWith(regeneratedSuffix, StringComparison.OrdinalIgnoreCase)) return;
        var versionStart = stem.LastIndexOf(".v", stem.Length - regeneratedSuffix.Length, StringComparison.OrdinalIgnoreCase);
        if (versionStart <= 0) return;
        var version = stem[(versionStart + 2)..^regeneratedSuffix.Length];
        if (int.TryParse(version, out _)) AddStem(stem[..versionStart], fullPath);
    }

    foreach (var file in files)
        AddKey(Path.ChangeExtension(file, ".key"));

    foreach (var root in new[] { "keys", ".verify-build", "bench" })
    {
        if (!Directory.Exists(root)) continue;
        foreach (var key in Directory.EnumerateFiles(root, "*.key", SearchOption.AllDirectories))
            AddKey(key);
    }

    if (!string.IsNullOrWhiteSpace(additionalKeyRoot) && Directory.Exists(additionalKeyRoot))
    {
        foreach (var key in Directory.EnumerateFiles(additionalKeyRoot, "*.key", SearchOption.AllDirectories))
            AddKey(key);
    }

    return keys.ToDictionary(
        kv => kv.Key,
        kv => (IReadOnlyList<string>)kv.Value.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
        StringComparer.OrdinalIgnoreCase);
}

static List<string> ExpandCalibrationInputs(IEnumerable<string> inputs)
{
    var files = new List<string>();
    foreach (var input in inputs)
    {
        if (File.Exists(input)) { files.Add(Path.GetFullPath(input)); continue; }
        if (Directory.Exists(input))
        {
            files.AddRange(Directory.EnumerateFiles(input, "*.docx", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(input, "*.docm", SearchOption.AllDirectories))
                .Concat(Directory.EnumerateFiles(input, "*.doc", SearchOption.AllDirectories))
                .Concat(Directory.EnumerateFiles(input, "*.rtf", SearchOption.AllDirectories))
                .Concat(Directory.EnumerateFiles(input, "*.odt", SearchOption.AllDirectories)));
            continue;
        }

        var dir = Path.GetDirectoryName(input);
        var pattern = Path.GetFileName(input);
        dir = string.IsNullOrEmpty(dir) ? Directory.GetCurrentDirectory() : dir;
        if (Directory.Exists(dir) && (pattern.Contains('*') || pattern.Contains('?')))
            files.AddRange(Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly).Where(IsSupported));
        else
            Console.Error.WriteLine($"Bỏ qua (không tồn tại): {input}");
    }

    return files;

    static bool IsSupported(string f) =>
        Path.GetExtension(f).ToLowerInvariant() is ".docx" or ".docm" or ".doc" or ".rtf" or ".odt";
}

static DocumentAgentRequest AgentRequest(string file, CommandLineOptions o) =>
    new(file, AllowExternalDataTransfer:
        !o.Pipeline.DisableLlm && o.Pipeline.Backend is InferenceBackend.OpenRouter or InferenceBackend.Sglang)
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

static List<string> ExpandPdfClusterInputs(IEnumerable<string> inputs)
{
    var files = new List<string>();
    foreach (var input in inputs)
    {
        if (File.Exists(input)) { files.Add(Path.GetFullPath(input)); continue; }

        if (Directory.Exists(input))
        {
            files.AddRange(Directory.EnumerateFiles(input, "*.pdf", SearchOption.AllDirectories));
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
        Path.GetExtension(f).ToLowerInvariant() is ".pdf" or ".docx" or ".docm" or ".doc" or ".rtf" or ".odt";
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
