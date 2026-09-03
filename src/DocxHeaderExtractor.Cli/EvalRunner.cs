using System.Text;
using DocxHeaderExtractor.AgentHarness;
using DocxHeaderExtractor.Core.Eval;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Cli;

/// <summary>
/// Chấm công cụ trên một thư mục tài liệu có đáp án: mỗi <c>X.docx</c> đi kèm <c>X.key</c>.
/// Không có .key thì bỏ qua file đó.
/// </summary>
public static class EvalRunner
{
    public static async Task<int> RunAsync(
        string directory,
        PipelineOptions options,
        bool quiet,
        CancellationToken ct,
        string? calibrationOutputPath = null)
    {
        if (!Directory.Exists(directory))
        {
            Console.Error.WriteLine($"Không tìm thấy thư mục: {directory}");
            return 2;
        }

        var pairs = Directory.EnumerateFiles(directory)
            .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".docx" or ".docm" or ".doc" or ".rtf" or ".odt")
            .Select(f => (Docx: f, Key: Path.ChangeExtension(f, ".key")))
            .Where(p => File.Exists(p.Key))
            .OrderBy(p => p.Docx, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (pairs.Count == 0)
        {
            Console.Error.WriteLine($"Không có cặp .docx + .key nào trong {directory}. Chạy `dhx bench` để sinh bộ mẫu.");
            return 2;
        }

        // Đáp án MỒ CÔI: có .key mà không có tài liệu. Phép duyệt trên đi từ tài liệu nên nhóm này
        // hoàn toàn vô hình — bộ đo thiếu một tài liệu và không ai được báo, trong khi mọi bảng số
        // vẫn ghi "bench 8 tài liệu". Đó là cách bộ đo trôi mà không để lại dấu vết; ĐÃ xảy ra thật
        // (`08-plph2.key` còn, `.docx` thì không, xem handoff §10).
        var orphanKeys = Directory.EnumerateFiles(directory, "*.key")
            .Where(k => !pairs.Any(p => string.Equals(p.Key, k, StringComparison.OrdinalIgnoreCase)))
            .Select(Path.GetFileNameWithoutExtension)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (orphanKeys.Count > 0)
            Console.Error.WriteLine(
                $"⚠ {orphanKeys.Count} đáp án không có tài liệu đi kèm nên KHÔNG được chấm: " +
                $"{string.Join(", ", orphanKeys)}. Bộ đo nhỏ hơn bộ đáp án — đừng so con số này với " +
                "bảng nào ghi số tài liệu lớn hơn.");

        using var tool = new PipelineDocumentExtractionTool(options);
        var harness = CliHarnessComposition.Create(pairs.Select(pair => pair.Docx), tool);
        var sourceReader = new AuthorityEvaluationSourceReader(options);
        var scores = new List<DocScore>();
        var calibration = new PrecisionCalibrationBuilder(
            PrecisionCalibrationProfile.ConfigurationFor(options),
            options.TargetPrecision,
            options.MinimumCalibrationSamples);
        var processingFailures = 0;

        foreach (var (docx, keyPath) in pairs)
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileNameWithoutExtension(docx);
            if (!quiet) Console.Error.WriteLine($"» {name}");

            try
            {
                var key = AnswerKey.Load(keyPath);

                // Tập ứng viên đọc riêng: cần biết tầng OpenXML có đánh rơi tiêu đề nào không,
                // vì đó là trần của phần MÔ HÌNH quyết được — nó không nhìn thấy cái đã bị loại.
                // Recall cuối vẫn có thể vượt tỉ lệ này nhờ StructuralRecovery/OutlineStructureResolver
                // chạy SAU mô hình và cứu theo quan hệ đánh số.
                HashSet<int> candidates;
                {
                    var sourceSnapshot = sourceReader.Read(docx);
                    // Review key dùng stable ID nên vẫn sống khi một đoạn phía trước bị thêm/xoá.
                    // Chỉ ở đúng đây nó mới được resolve về index của bản DOCX đang chấm.
                    key = key.ResolveStableIds(sourceSnapshot.Document.Paragraphs
                        .ToDictionary(p => p.SourceId, p => p.SourceOrdinal));
                    candidates = [.. sourceSnapshot.CandidateIndexes];
                }

                var run = await harness.RunAsync(new DocumentAgentRequest(
                    docx,
                    AllowExternalDataTransfer:
                        !options.DisableLlm && options.Backend is InferenceBackend.OpenRouter or InferenceBackend.Sglang), ct);
                var outline = run.TaskResult.Value;
                scores.Add(Evaluator.Score(name, outline, candidates, key));
                if (!key.IsPartial)
                    calibration.Add(outline, key);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"  Thất bại: {ex.Message}");
                processingFailures++;
            }
        }

        Console.WriteLine(Report(new SuiteScore(scores), options));
        if (!string.IsNullOrWhiteSpace(calibrationOutputPath))
        {
            if (processingFailures > 0)
            {
                Console.Error.WriteLine(
                    $"Không ghi calibration profile: {processingFailures} tài liệu/key xử lý thất bại.");
                return 2;
            }
            var full = Path.GetFullPath(calibrationOutputPath);
            var parent = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            calibration.Build().Save(full);
            if (!quiet) Console.Error.WriteLine($"Đã ghi calibration profile từ holdout: {full}");
        }
        if (processingFailures > 0) return 2;
        return scores.Any(s => s.FalsePositives.Count > 0 || s.FalseNegatives.Count > 0 || s.WrongLevels.Count > 0)
            ? 1
            : 0;
    }

    private static string Report(SuiteScore suite, PipelineOptions options)
    {
        var sb = new StringBuilder();
        var mode = options.DisableLlm
            ? "chỉ luật OpenXML"
            : options.Backend switch
            {
                InferenceBackend.OpenRouter => options.OpenRouter.Model,
                InferenceBackend.LmStudio => $"LM Studio/{options.LmStudio.Model}",
                InferenceBackend.Sglang => $"SGLang/{options.Sglang.Model}",
                _ => Path.GetFileNameWithoutExtension(options.Llama.ModelPath),
            };

        sb.AppendLine($"Bộ test: {suite.Documents} tài liệu · chế độ: {mode}");
        // Con số và điều kiện sinh ra nó phải đi cùng một chỗ. Handoff §8.1 từng chốt bằng LỜI rằng
        // "mọi con số phải ghi kèm số lớp offload", nhưng công cụ sinh ra con số lại không ghi — nên
        // một lượt CPU và một lượt GPU trông giống hệt nhau trên giấy. Cùng họ với bẫy §4.3 (cấu
        // hình đo lệch cấu hình chạy) và §4.4 (log nói dối).
        sb.AppendLine($"Cấu hình: {PrecisionCalibrationProfile.ConfigurationFor(options)}");
        if (suite.Docs.Any(d => d.PartialTruth))
            sb.AppendLine("Đáp án partial: không phạt false positive ngoài phạm vi đã gán; P/F1 chỉ đọc trong phạm vi đã gán.");
        sb.AppendLine();
        sb.AppendLine("| Tài liệu | Đáp án | Trả về | Ứng viên | P | R | Nav | Nav cấp | Cấp | Thừa | Thiếu | Sai cấp |");
        sb.AppendLine("|---|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|");

        foreach (var d in suite.Docs)
        {
            sb.Append("| ").Append(d.File)
              .Append(" | ").Append(d.TruthCount)
              .Append(" | ").Append(d.ResultCount)
              .Append(" | ").Append(d.CandidateCount)
              .Append(" | ").Append(Pct(d.Precision))
              .Append(" | ").Append(Pct(d.Recall))
              .Append(" | ").Append(d.NavigationJudged == 0 ? "—" : Pct(d.NavigationRecall))
              .Append(" | ").Append(d.NavigationLevelJudged == 0 ? "—" : Pct(d.NavigationLevelAccuracy))
              .Append(" | ").Append(d.LevelJudged == 0 ? "—" : Pct(d.LevelAccuracy))
              .Append(" | ").Append(d.FalsePositives.Count)
              .Append(" | ").Append(d.FalseNegatives.Count)
              .Append(" | ").Append(d.WrongLevels.Count)
              .AppendLine(" |");
        }

        sb.AppendLine();
        sb.AppendLine($"Gộp toàn bộ đoạn:  P {Pct(suite.MicroPrecision)}  ·  R {Pct(suite.MicroRecall)}  ·  " +
                      $"F1 {Pct(suite.MicroF1)}  ·  đúng cấp {Pct(suite.MicroLevelAccuracy)}  ·  " +
                      $"đúng cha {Pct(suite.MicroParentAccuracy)}");
        if (suite.Docs.Any(d => d.NavigationJudged > 0))
            sb.AppendLine($"Mục lục điều hướng: Nav {Pct(suite.MicroNavigationRecall)}  ·  " +
                          $"Nav+cấp {Pct(suite.MicroNavigationLevelAccuracy)} " +
                          "(cùng paragraph/index, output bắt đầu bằng title trong comment key sau chuẩn hoá tìm kiếm)");
        sb.AppendLine($"Trung bình theo tài liệu: F1 {Pct(suite.MacroF1)}");
        var coverageName = !options.DisableLlm && options.ReviewAllParagraphs
            ? "Tiêu đề được model review"
            : "Tiêu đề lọt vào tập ứng viên";
        var coverageNote = !options.DisableLlm && options.ReviewAllParagraphs
            ? "(không bị heuristic chặn trước khi model thấy)"
            : "(trần của phần MÔ HÌNH quyết được — nó không nhìn thấy đoạn đã bị loại. Recall cuối "
              + "CÓ THỂ CAO HƠN nhờ tầng cứu theo cấu trúc chạy sau: đo được 88,9% recall trên một công "
              + "văn thật có tỉ lệ này chỉ 66,7%)";
        sb.AppendLine($"{coverageName}: {Pct(suite.MicroCandidateRecall)}  {coverageNote}");
        sb.AppendLine($"Tài liệu đạt tuyệt đối: {suite.Perfect}/{suite.Documents}");

        var flawed = suite.Docs.Where(d =>
            d.FalsePositives.Count > 0 || d.FalseNegatives.Count > 0 || d.WrongLevels.Count > 0).ToList();

        if (flawed.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Chi tiết chỗ sai:");
            foreach (var d in flawed)
            {
                sb.Append("  ").Append(d.File).AppendLine(":");
                if (d.FalsePositives.Count > 0)
                    sb.AppendLine($"    thừa   : {string.Join(", ", d.FalsePositives)}");
                if (d.FalseNegatives.Count > 0)
                    sb.AppendLine($"    thiếu  : {string.Join(", ", d.FalseNegatives)}");
                foreach (var (index, got, expected) in d.WrongLevels)
                    sb.AppendLine($"    cấp    : i={index} trả về {got}, đáp án {expected}");
            }
        }

        return sb.ToString();
    }

    private static string Pct(double v) => (v * 100).ToString("0.#") + "%";
}
