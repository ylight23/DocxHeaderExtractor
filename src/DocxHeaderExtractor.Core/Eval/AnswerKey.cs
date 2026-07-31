using System.Globalization;
using System.Text;

namespace DocxHeaderExtractor.Core.Eval;

/// <summary>
/// Đáp án cho một tài liệu: chỉ số đoạn → cấp tiêu đề. Cấp có thể bỏ trống (null) khi chỉ
/// muốn chấm việc chọn đúng đoạn mà không chấm cấp.
/// <para>
/// Định dạng file .key — mỗi dòng một mục, phần sau '#' là chú thích:
/// <code>
/// # 3.2.PLPH2 — dàn ý chuẩn
/// 31 1      # DANH MỤC KÝ HIỆU VÀ TỪ VIẾT TẮT
/// 95 1
/// 96        # không ghi cấp ⇒ chỉ chấm việc chọn
/// </code>
/// </para>
/// </summary>
public sealed class AnswerKey
{
    private readonly Dictionary<int, int?> _levels;

    public string? Title { get; }

    private AnswerKey(Dictionary<int, int?> levels, string? title)
    {
        _levels = levels;
        Title = title;
    }

    public IReadOnlyCollection<int> Indexes => _levels.Keys;
    public int Count => _levels.Count;
    public bool Contains(int index) => _levels.ContainsKey(index);
    public int? LevelOf(int index) => _levels.TryGetValue(index, out var l) ? l : null;

    /// <summary>Các chỉ số có ghi cấp — chỉ những dòng này mới được chấm cấp.</summary>
    public IEnumerable<int> IndexesWithLevel => _levels.Where(kv => kv.Value is not null).Select(kv => kv.Key);

    public static AnswerKey Parse(string text, string? title = null)
    {
        var levels = new Dictionary<int, int?>();

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine;
            var hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash];
            line = line.Trim();
            if (line.Length == 0) continue;

            // Chấp nhận "95 1", "95,1", "95:1", "95" — và nhiều mục trên một dòng cách nhau bởi ';'.
            foreach (var item in line.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = item.Split([' ', '\t', ',', ':'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;
                if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                    throw new FormatException($"Không đọc được chỉ số đoạn từ: \"{item}\"");

                int? level = null;
                if (parts.Length > 1)
                {
                    if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var lvl))
                        throw new FormatException($"Không đọc được cấp từ: \"{item}\"");
                    level = lvl;
                }

                levels[index] = level;
            }
        }

        return new AnswerKey(levels, title);
    }

    public static AnswerKey Load(string path) =>
        Parse(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path));

    public static string Write(IEnumerable<(int Index, int Level, string Text)> headings, string title)
    {
        var sb = new StringBuilder();
        sb.Append("# ").AppendLine(title);
        sb.AppendLine("# <chỉ số đoạn> <cấp>   — sinh tự động cùng tài liệu, không gán nhãn tay");
        foreach (var (index, level, text) in headings.OrderBy(h => h.Index))
            sb.Append(index).Append(' ').Append(level).Append("   # ").AppendLine(text);
        return sb.ToString();
    }
}
