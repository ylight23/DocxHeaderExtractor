using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;

namespace DocxHeaderExtractor.Eval;

/// <summary>Kết quả chấm một tài liệu so với đáp án.</summary>
public sealed record DocScore(
    string File,
    int TruthCount,
    int ResultCount,
    int CandidateCount,
    int TruePositive,
    int CandidateHits,
    int NavigationJudged,
    int NavigationTitleHits,
    int NavigationLevelJudged,
    int NavigationLevelHits,
    int LevelJudged,
    int LevelCorrect,
    int ParentCorrect,
    IReadOnlyList<int> FalsePositives,
    IReadOnlyList<int> FalseNegatives,
    IReadOnlyList<(int Index, int? Got, int Expected)> WrongLevels,
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

    /// <summary>
    /// Recall cho mục lục điều hướng/search: đáp án có text comment được tính đúng khi output cùng
    /// paragraph/index bắt đầu bằng title đó. Metric này tách khỏi exact span vì PDF text-layout
    /// thường dính title + body trong cùng paragraph.
    /// </summary>
    public double NavigationRecall => NavigationJudged == 0 ? 0 : (double)NavigationTitleHits / NavigationJudged;

    public double NavigationLevelAccuracy =>
        NavigationLevelJudged == 0 ? 0 : (double)NavigationLevelHits / NavigationLevelJudged;

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
    public double MicroNavigationRecall => Div(Docs.Sum(d => d.NavigationTitleHits), Docs.Sum(d => d.NavigationJudged));
    public double MicroNavigationLevelAccuracy => Div(Docs.Sum(d => d.NavigationLevelHits), Docs.Sum(d => d.NavigationLevelJudged));
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
        if (key.HasDuplicateSourceKeys)
            return ScoreWithTextIdentity(file, outline, candidateIndexes, key);

        var got = outline.Headings
            .GroupBy(h => h.Index)
            .ToDictionary(g => g.Key, g => g.First().Level);
        var gotIndexes = outline.Headings.Select(h => h.Index).ToList();
        var navigation = NavigationScore(outline, key.PositiveEntries);

        var tp = got.Keys.Where(key.Contains).ToList();
        var reviewedFp = key.NegativeEntries
            .Where(e => e.Index is not null && gotIndexes.Contains(e.Index.Value))
            .Select(e => e.Index!.Value)
            .OrderBy(i => i)
            .ToList();
        var fp = key.IsPartial
            ? reviewedFp
            : gotIndexes.Where(i => !key.Contains(i)).OrderBy(i => i).ToList();
        var resultCount = key.IsPartial ? tp.Count + fp.Count : gotIndexes.Count;
        var fn = key.Indexes.Where(i => !got.ContainsKey(i)).OrderBy(i => i).ToList();

        // Chỉ chấm cấp trên phần giao, và chỉ với những dòng đáp án có ghi cấp.
        var judged = tp.Where(i => key.LevelOf(i) is not null).ToList();
        var wrong = judged.Where(i => key.LevelOf(i) != got[i])
                          .Select(i => (Index: i, Got: got[i], Expected: key.LevelOf(i)!.Value))
                          .OrderBy(x => x.Index)
                          .ToList();

        var ordered = judged.OrderBy(i => i).ToList();
        var truthParent = Parents(ordered, key.LevelOf);
        var gotParent = Parents(ordered, i => got[i]);
        var parentCorrect = ordered.Count(i => truthParent[i] == gotParent[i]);

        return new DocScore(
            File: file,
            TruthCount: key.Count,
            ResultCount: resultCount,
            CandidateCount: candidateIndexes.Count,
            TruePositive: tp.Count,
            CandidateHits: key.Indexes.Count(candidateIndexes.Contains),
            NavigationJudged: navigation.Judged,
            NavigationTitleHits: navigation.TitleHits,
            NavigationLevelJudged: navigation.LevelJudged,
            NavigationLevelHits: navigation.LevelHits,
            LevelJudged: judged.Count,
            LevelCorrect: judged.Count - wrong.Count,
            ParentCorrect: parentCorrect,
            FalsePositives: fp,
            FalseNegatives: fn,
            WrongLevels: wrong,
            PartialTruth: key.IsPartial,
            ElapsedMs: outline.ElapsedMs);
    }

    private static DocScore ScoreWithTextIdentity(
        string file,
        DocumentOutline outline,
        IReadOnlyCollection<int> candidateIndexes,
        AnswerKey key)
    {
        var truth = key.PositiveEntries.Select((e, order) => new KeyItem(
            order,
            e.Index ?? throw new InvalidOperationException("Key duplicate-source phải được resolve stable ID trước khi chấm."),
            e.Level,
            Normalize(e.Text))).ToList();
        var negatives = key.NegativeEntries.Select((e, order) => new KeyItem(
            order,
            e.Index ?? throw new InvalidOperationException("Key negative duplicate-source phải được resolve stable ID trước khi chấm."),
            e.Level,
            Normalize(e.Text))).ToList();

        if (truth.Any(t => string.IsNullOrWhiteSpace(t.Text)))
            throw new InvalidOperationException(
                "Key có nhiều heading cùng paragraph phải ghi text ở comment để evaluator phân biệt.");
        if (negatives.Any(t => string.IsNullOrWhiteSpace(t.Text)))
            throw new InvalidOperationException(
                "Key negative phải ghi text ở comment để evaluator phân biệt false positive.");

        var got = outline.Headings.Select((h, order) => new GotItem(
            order,
            h.Index,
            h.Level,
            Normalize(h.Text))).ToList();
        var navigation = NavigationScore(outline, key.PositiveEntries);

        var used = new HashSet<int>();
        var matches = new List<(KeyItem Key, GotItem Got)>();
        foreach (var k in truth)
        {
            var at = got.FindIndex(g => !used.Contains(g.Order) && g.Index == k.Index && g.Text == k.Text);
            if (at < 0) continue;
            used.Add(got[at].Order);
            matches.Add((k, got[at]));
        }

        var tp = matches.Count;
        var negativeMatches = negatives
            .Select(k => got.FirstOrDefault(g => g.Index == k.Index && g.Text == k.Text))
            .Where(g => g is not null)
            .Select(g => g!.Index)
            .Order()
            .ToList();
        var fp = key.IsPartial
            ? negativeMatches
            : got.Where(g => !used.Contains(g.Order)).Select(g => g.Index).Order().ToList();
        var fn = truth.Where(k => !matches.Any(m => m.Key.Order == k.Order)).Select(k => k.Index).Order().ToList();
        var resultCount = key.IsPartial ? tp + fp.Count : got.Count;

        var judged = matches.Where(m => m.Key.Level is not null).OrderBy(m => m.Got.Order).ToList();
        var wrong = judged.Where(m => m.Key.Level != m.Got.Level)
            .Select(m => (Index: m.Got.Index, Got: m.Got.Level, Expected: m.Key.Level!.Value))
            .OrderBy(x => x.Index)
            .ToList();

        var truthLevels = judged.ToDictionary(m => m.Got.Order, m => m.Key.Level!.Value);
        var gotLevels = judged.ToDictionary(m => m.Got.Order, m => m.Got.Level);
        var ordered = judged.Select(m => m.Got.Order).ToList();
        var truthParent = Parents(ordered, i => truthLevels[i]);
        var gotParent = Parents(ordered, i => gotLevels[i]);
        var parentCorrect = ordered.Count(i => truthParent[i] == gotParent[i]);

        return new DocScore(
            File: file,
            TruthCount: key.Count,
            ResultCount: resultCount,
            CandidateCount: candidateIndexes.Count,
            TruePositive: tp,
            CandidateHits: truth.Count(k => candidateIndexes.Contains(k.Index)),
            NavigationJudged: navigation.Judged,
            NavigationTitleHits: navigation.TitleHits,
            NavigationLevelJudged: navigation.LevelJudged,
            NavigationLevelHits: navigation.LevelHits,
            LevelJudged: judged.Count,
            LevelCorrect: judged.Count - wrong.Count,
            ParentCorrect: parentCorrect,
            FalsePositives: fp,
            FalseNegatives: fn,
            WrongLevels: wrong,
            PartialTruth: key.IsPartial,
            ElapsedMs: outline.ElapsedMs);
    }

    private sealed record KeyItem(int Order, int Index, int? Level, string Text);
    private sealed record GotItem(int Order, int Index, int? Level, string Text);
    private readonly record struct NavigationCounts(int Judged, int TitleHits, int LevelJudged, int LevelHits);

    private static string Normalize(string? text) =>
        string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string NormalizeForNavigation(string? text)
    {
        var normalized = Normalize(text)
            .Replace('\u2010', '-')
            .Replace('\u2011', '-')
            .Replace('\u2012', '-')
            .Replace('\u2013', '-')
            .Replace('\u2014', '-')
            .Replace('\u2212', '-')
            .ToLowerInvariant();

        normalized = System.Text.RegularExpressions.Regex.Replace(
            normalized,
            @"\b(section\s+(?:[ivxlcdm]+|\d+))\s*[\.\-:]\s*",
            "$1 ");
        normalized = System.Text.RegularExpressions.Regex.Replace(
            normalized,
            @"\b(part\s+\d+)\s*[\-:]\s*",
            "$1 ");
        normalized = System.Text.RegularExpressions.Regex.Replace(
            normalized,
            @"[^a-z0-9]+",
            " ");
        return Normalize(normalized);
    }

    private static NavigationCounts NavigationScore(
        DocumentOutline outline,
        IReadOnlyList<AnswerKeyEntry> entries)
    {
        var truth = entries.Select((e, order) => new KeyItem(
                order,
                e.Index ?? -1,
                e.Level,
                NormalizeForNavigation(e.Text)))
            .Where(k => k.Index >= 0 && !string.IsNullOrWhiteSpace(k.Text))
            .ToList();
        if (truth.Count == 0) return default;

        var got = outline.Headings.Select((h, order) => new GotItem(
            order,
            h.Index,
            h.Level,
            NormalizeForNavigation(h.Text))).ToList();

        var used = new HashSet<int>();
        var titleHits = 0;
        var levelJudged = truth.Count(t => t.Level is not null);
        var levelHits = 0;

        foreach (var t in truth)
        {
            var matchAt = got.FindIndex(g =>
                !used.Contains(g.Order) &&
                g.Index == t.Index &&
                g.Text.StartsWith(t.Text, StringComparison.Ordinal));
            if (matchAt < 0) continue;

            var match = got[matchAt];
            used.Add(match.Order);
            titleHits++;
            if (t.Level == match.Level) levelHits++;
        }

        return new NavigationCounts(truth.Count, titleHits, levelJudged, levelHits);
    }

    /// <summary>
    /// Cha của mỗi mục = mục GẦN NHẤT ĐỨNG TRƯỚC có cấp NHỎ HƠN; <c>null</c> nếu không có (mục ở
    /// tầng ngoài cùng). Đây là định nghĩa cây ngầm định của một dãy tiêu đề đánh cấp — cùng cách
    /// <see cref="Pipeline.StructuralHierarchyResolver"/> hiểu quan hệ cha–con.
    /// </summary>
    private static Dictionary<int, int?> Parents(IReadOnlyList<int> ordered, Func<int, int?> levelOf)
    {
        var result = new Dictionary<int, int?>(ordered.Count);
        // An item whose level is unresolved cannot be placed in the level stack - it gets no parent,
        // and it is skipped rather than pushed so later items are never (mis)parented under it.
        var stack = new List<(int Index, int Level)>();
        foreach (var index in ordered)
        {
            var level = levelOf(index);
            if (level is null) { result[index] = null; continue; }
            while (stack.Count > 0 && stack[^1].Level >= level) stack.RemoveAt(stack.Count - 1);
            result[index] = stack.Count > 0 ? stack[^1].Index : null;
            stack.Add((index, level.Value));
        }
        return result;
    }
}
