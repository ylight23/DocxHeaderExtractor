using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Eval;

/// <summary>Kết quả chấm một tài liệu so với đáp án.</summary>
public sealed record DocScore(
    string File,
    int TruthCount,
    int ResultCount,
    int CandidateCount,
    int TruePositive,
    int CandidateHits,
    int LevelJudged,
    int LevelCorrect,
    IReadOnlyList<int> FalsePositives,
    IReadOnlyList<int> FalseNegatives,
    IReadOnlyList<(int Index, int Got, int Expected)> WrongLevels,
    long ElapsedMs)
{
    public double Precision => ResultCount == 0 ? 0 : (double)TruePositive / ResultCount;
    public double Recall => TruthCount == 0 ? 0 : (double)TruePositive / TruthCount;
    public double F1 => Precision + Recall == 0 ? 0 : 2 * Precision * Recall / (Precision + Recall);

    /// <summary>
    /// Tỉ lệ tiêu đề đúng LỌT ĐƯỢC vào tập ứng viên. Đây là trần của phần MÔ HÌNH quyết được —
    /// KHÔNG phải trần của recall cuối cùng: các tầng cứu theo cấu trúc chạy SAU mô hình và có thể
    /// thêm heading nằm ngoài tập ứng viên. Đo được trên một công văn thật: tỉ lệ này 66,7% nhưng
    /// recall cuối đạt 88,9% nhờ 4 heading do StructuralRecovery cứu lại.
    /// Cách đọc cũ:
    /// tầng OpenXML đánh rơi thì mô hình không có cơ hội nào cứu lại.
    /// </summary>
    public double CandidateRecall => TruthCount == 0 ? 0 : (double)CandidateHits / TruthCount;

    public double LevelAccuracy => LevelJudged == 0 ? 0 : (double)LevelCorrect / LevelJudged;
}

/// <summary>Tổng hợp trên cả bộ. Vi mô = gộp mọi đoạn; vĩ mô = trung bình theo tài liệu.</summary>
public sealed record SuiteScore(IReadOnlyList<DocScore> Docs)
{
    public int Documents => Docs.Count;

    public double MicroPrecision => Div(Docs.Sum(d => d.TruePositive), Docs.Sum(d => d.ResultCount));
    public double MicroRecall => Div(Docs.Sum(d => d.TruePositive), Docs.Sum(d => d.TruthCount));
    public double MicroF1 => MicroPrecision + MicroRecall == 0
        ? 0
        : 2 * MicroPrecision * MicroRecall / (MicroPrecision + MicroRecall);
    public double MicroCandidateRecall => Div(Docs.Sum(d => d.CandidateHits), Docs.Sum(d => d.TruthCount));
    public double MicroLevelAccuracy => Div(Docs.Sum(d => d.LevelCorrect), Docs.Sum(d => d.LevelJudged));

    public double MacroF1 => Docs.Count == 0 ? 0 : Docs.Average(d => d.F1);

    /// <summary>Số tài liệu đạt tuyệt đối — không thừa, không thiếu, không sai cấp.</summary>
    public int Perfect => Docs.Count(d =>
        d.FalsePositives.Count == 0 && d.FalseNegatives.Count == 0 && d.WrongLevels.Count == 0);

    private static double Div(int a, int b) => b == 0 ? 0 : (double)a / b;
}

public static class Evaluator
{
    public static DocScore Score(
        string file,
        DocumentOutline outline,
        IReadOnlyCollection<int> candidateIndexes,
        AnswerKey key)
    {
        var got = outline.Headings.ToDictionary(h => h.Index, h => h.Level);

        var tp = got.Keys.Where(key.Contains).ToList();
        var fp = got.Keys.Where(i => !key.Contains(i)).OrderBy(i => i).ToList();
        var fn = key.Indexes.Where(i => !got.ContainsKey(i)).OrderBy(i => i).ToList();

        // Chỉ chấm cấp trên phần giao, và chỉ với những dòng đáp án có ghi cấp.
        var judged = tp.Where(i => key.LevelOf(i) is not null).ToList();
        var wrong = judged.Where(i => key.LevelOf(i) != got[i])
                          .Select(i => (Index: i, Got: got[i], Expected: key.LevelOf(i)!.Value))
                          .OrderBy(x => x.Index)
                          .ToList();

        return new DocScore(
            File: file,
            TruthCount: key.Count,
            ResultCount: got.Count,
            CandidateCount: candidateIndexes.Count,
            TruePositive: tp.Count,
            CandidateHits: key.Indexes.Count(candidateIndexes.Contains),
            LevelJudged: judged.Count,
            LevelCorrect: judged.Count - wrong.Count,
            FalsePositives: fp,
            FalseNegatives: fn,
            WrongLevels: wrong,
            ElapsedMs: outline.ElapsedMs);
    }
}
