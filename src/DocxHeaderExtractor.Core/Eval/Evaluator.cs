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
    int ParentCorrect,
    IReadOnlyList<int> FalsePositives,
    IReadOnlyList<int> FalseNegatives,
    IReadOnlyList<(int Index, int Got, int Expected)> WrongLevels,
    bool PartialTruth,
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

    /// <summary>
    /// Tỉ lệ tiêu đề có CHA đúng — bài toán con <i>parent finding</i> của HRDoc (AAAI 2023,
    /// arXiv 2303.13839), thước đo chuẩn của nhánh tái dựng cấu trúc phân cấp.
    /// <para>
    /// Vì sao cần thêm nó bên cạnh <see cref="LevelAccuracy"/>: cấp tuyệt đối phạt cả những cây
    /// ĐÚNG HÌNH nhưng lệch gốc. Đo được ở §26.2 trên lớp style-only của khoá luận — đúng cấp tuyệt
    /// đối 41,2% nhưng đúng cha 100%, không sai một cạnh nào; toàn bộ 40 lỗi là lệch đều một bậc.
    /// Chỉ đọc con số 41,2% thì sẽ kết luận "style vô dụng cho cấp" và ném đi một tín hiệu hoàn hảo,
    /// đúng như §17 đã lỡ làm.
    /// </para>
    /// <para>
    /// Tính trên PHẦN GIAO đã chấm cấp, cả hai cây dựng từ cùng tập mục đó. Nếu để mỗi bên tự dựng
    /// trên tập riêng thì lỗi chọn (thừa/thiếu) trộn vào lỗi quan hệ, và con số không còn trả lời
    /// được câu hỏi nào cả — P/R đã đo phần chọn rồi.
    /// </para>
    /// </summary>
    public double ParentAccuracy => LevelJudged == 0 ? 0 : (double)ParentCorrect / LevelJudged;
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
    public double MicroParentAccuracy => Div(Docs.Sum(d => d.ParentCorrect), Docs.Sum(d => d.LevelJudged));

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
        var fp = key.IsPartial
            ? new List<int>()
            : got.Keys.Where(i => !key.Contains(i)).OrderBy(i => i).ToList();
        var resultCount = key.IsPartial ? tp.Count : got.Count;
        var fn = key.Indexes.Where(i => !got.ContainsKey(i)).OrderBy(i => i).ToList();

        // Chỉ chấm cấp trên phần giao, và chỉ với những dòng đáp án có ghi cấp.
        var judged = tp.Where(i => key.LevelOf(i) is not null).ToList();
        var wrong = judged.Where(i => key.LevelOf(i) != got[i])
                          .Select(i => (Index: i, Got: got[i], Expected: key.LevelOf(i)!.Value))
                          .OrderBy(x => x.Index)
                          .ToList();

        var ordered = judged.OrderBy(i => i).ToList();
        var truthParent = Parents(ordered, i => key.LevelOf(i)!.Value);
        var gotParent = Parents(ordered, i => got[i]);
        var parentCorrect = ordered.Count(i => truthParent[i] == gotParent[i]);

        return new DocScore(
            File: file,
            TruthCount: key.Count,
            ResultCount: resultCount,
            CandidateCount: candidateIndexes.Count,
            TruePositive: tp.Count,
            CandidateHits: key.Indexes.Count(candidateIndexes.Contains),
            LevelJudged: judged.Count,
            LevelCorrect: judged.Count - wrong.Count,
            ParentCorrect: parentCorrect,
            FalsePositives: fp,
            FalseNegatives: fn,
            WrongLevels: wrong,
            PartialTruth: key.IsPartial,
            ElapsedMs: outline.ElapsedMs);
    }

    /// <summary>
    /// Cha của mỗi mục = mục GẦN NHẤT ĐỨNG TRƯỚC có cấp NHỎ HƠN; <c>null</c> nếu không có (mục ở
    /// tầng ngoài cùng). Đây là định nghĩa cây ngầm định của một dãy tiêu đề đánh cấp — cùng cách
    /// <see cref="Pipeline.StructuralHierarchyResolver"/> hiểu quan hệ cha–con.
    /// </summary>
    private static Dictionary<int, int?> Parents(IReadOnlyList<int> ordered, Func<int, int> levelOf)
    {
        var result = new Dictionary<int, int?>(ordered.Count);
        var stack = new List<int>();
        foreach (var index in ordered)
        {
            var level = levelOf(index);
            while (stack.Count > 0 && levelOf(stack[^1]) >= level) stack.RemoveAt(stack.Count - 1);
            result[index] = stack.Count > 0 ? stack[^1] : null;
            stack.Add(index);
        }
        return result;
    }
}
