using System.Text;
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
    public static async Task<int> RunAsync(string directory, PipelineOptions options, bool quiet, CancellationToken ct)
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

        var pipeline = new HeaderExtractionPipeline(options);
        var scores = new List<DocScore>();

        foreach (var (docx, keyPath) in pairs)
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileNameWithoutExtension(docx);
            if (!quiet) Console.Error.WriteLine($"» {name}");

            try
            {
                var key = AnswerKey.Load(keyPath);

                // Tập ứng viên đọc riêng: cần biết tầng OpenXML có đánh rơi tiêu đề nào không,
                // vì đó là trần trên của recall — mô hình không cứu được cái đã bị loại.
                var conversion = LegacyDocConverter.EnsureDocx(docx);
                HashSet<int> candidates;
                try
                {
                    var slim = new DocxSlimExtractor(options.Extraction).Extract(conversion.Path);
                    candidates = [.. slim.Candidates.Select(p => p.Index)];
                }
                finally { LegacyDocConverter.Cleanup(conversion); }

                var outline = await pipeline.RunAsync(docx, ct);
                scores.Add(Evaluator.Score(name, outline, candidates, key));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"  Thất bại: {ex.Message}");
            }
        }

        Console.WriteLine(Report(new SuiteScore(scores), options));
        return scores.Any(s => s.FalsePositives.Count > 0 || s.FalseNegatives.Count > 0 || s.WrongLevels.Count > 0)
            ? 1
            : 0;
    }

    private static string Report(SuiteScore suite, PipelineOptions options)
    {
        var sb = new StringBuilder();
        var mode = options.DisableLlm
            ? "chỉ luật OpenXML"
            : Path.GetFileNameWithoutExtension(options.Llama.ModelPath);

        sb.AppendLine($"Bộ test: {suite.Documents} tài liệu · chế độ: {mode}");
        sb.AppendLine();
        sb.AppendLine("| Tài liệu | Đáp án | Trả về | Ứng viên | P | R | Cấp | Thừa | Thiếu | Sai cấp |");
        sb.AppendLine("|---|--:|--:|--:|--:|--:|--:|--:|--:|--:|");

        foreach (var d in suite.Docs)
        {
            sb.Append("| ").Append(d.File)
              .Append(" | ").Append(d.TruthCount)
              .Append(" | ").Append(d.ResultCount)
              .Append(" | ").Append(d.CandidateCount)
              .Append(" | ").Append(Pct(d.Precision))
              .Append(" | ").Append(Pct(d.Recall))
              .Append(" | ").Append(d.LevelJudged == 0 ? "—" : Pct(d.LevelAccuracy))
              .Append(" | ").Append(d.FalsePositives.Count)
              .Append(" | ").Append(d.FalseNegatives.Count)
              .Append(" | ").Append(d.WrongLevels.Count)
              .AppendLine(" |");
        }

        sb.AppendLine();
        sb.AppendLine($"Gộp toàn bộ đoạn:  P {Pct(suite.MicroPrecision)}  ·  R {Pct(suite.MicroRecall)}  ·  " +
                      $"F1 {Pct(suite.MicroF1)}  ·  đúng cấp {Pct(suite.MicroLevelAccuracy)}");
        sb.AppendLine($"Trung bình theo tài liệu: F1 {Pct(suite.MacroF1)}");
        sb.AppendLine($"Tiêu đề lọt vào tập ứng viên: {Pct(suite.MicroCandidateRecall)}  " +
                      "(trần trên của recall — tầng OpenXML đánh rơi thì mô hình không cứu được)");
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
