using System.Globalization;
using System.Text;

namespace DocxHeaderExtractor.Eval;

public sealed record AnswerKeyEntry(int? Index, string? StableId, int? Level, string? Text, bool Excluded = false);

/// <summary>
/// Đáp án cho một tài liệu: chỉ số đoạn hoặc stable ID → cấp tiêu đề. Cấp có thể bỏ trống (null) khi
/// chỉ muốn chấm việc chọn đúng đoạn mà không chấm cấp.
/// <para>
/// Định dạng file .key — mỗi dòng một mục, phần sau '#' là chú thích:
/// <code>
/// # 3.2.PLPH2 — dàn ý chuẩn
/// 31 1      # DANH MỤC KÝ HIỆU VÀ TỪ VIẾT TẮT
/// 95 1
/// 96        # không ghi cấp ⇒ chỉ chấm việc chọn
/// @body[1]/p[12] 2 # stable ID từ review bundle
/// </code>
/// </para>
/// </summary>
public sealed class AnswerKey
{
    private readonly Dictionary<int, int?> _levels;
    private readonly Dictionary<string, int?> _stableLevels;
    private readonly IReadOnlyList<AnswerKeyEntry> _entries;

    public string? Title { get; }
    public bool IsPartial { get; }
    public IReadOnlyList<AnswerKeyEntry> Entries => _entries;

    private AnswerKey(
        Dictionary<int, int?> levels,
        Dictionary<string, int?> stableLevels,
        IReadOnlyList<AnswerKeyEntry> entries,
        string? title,
        bool isPartial)
    {
        _levels = levels;
        _stableLevels = stableLevels;
        _entries = entries;
        Title = title;
        IsPartial = isPartial;
    }

    public IReadOnlyCollection<int> Indexes => _levels.Keys;
    public IReadOnlyCollection<string> StableIds => _stableLevels.Keys;
    public bool HasStableIds => _stableLevels.Count > 0;
    public int Count => _entries.Count(e => !e.Excluded);
    public IReadOnlyList<AnswerKeyEntry> PositiveEntries => _entries.Where(e => !e.Excluded).ToList();
    public IReadOnlyList<AnswerKeyEntry> NegativeEntries => _entries.Where(e => e.Excluded).ToList();
    public bool HasDuplicateSourceKeys =>
        _entries.Where(e => e.Index is not null).GroupBy(e => e.Index!.Value).Any(g => g.Count() > 1) ||
        _entries.Where(e => e.StableId is not null).GroupBy(e => e.StableId!, StringComparer.Ordinal).Any(g => g.Count() > 1);
    public bool Contains(int index) => _levels.ContainsKey(index);
    public int? LevelOf(int index) => _levels.TryGetValue(index, out var l) ? l : null;
    public int? StableLevelOf(string stableId) => _stableLevels.TryGetValue(stableId, out var l) ? l : null;

    /// <summary>Các chỉ số có ghi cấp — chỉ những dòng này mới được chấm cấp.</summary>
    public IEnumerable<int> IndexesWithLevel => _levels.Where(kv => kv.Value is not null).Select(kv => kv.Key);

    /// <summary>Đổi stable ID của review key thành index của đúng tài liệu đang được chấm.</summary>
    public AnswerKey ResolveStableIds(IReadOnlyDictionary<string, int> stableIdToIndex)
    {
        if (_stableLevels.Count == 0) return this;

        var resolved = new Dictionary<int, int?>(_levels);
        var entries = new List<AnswerKeyEntry>(_entries.Where(e => e.Index is not null));
        foreach (var entry in _entries.Where(e => e.StableId is not null))
        {
            var stableId = entry.StableId!;
            var level = entry.Level;
            if (!stableIdToIndex.TryGetValue(stableId, out var index))
                throw new InvalidOperationException(
                    $"Stable ID trong key không có trong tài liệu hiện tại: {stableId}. " +
                    "Không dùng key này cho bản DOCX khác.");
            if (!entry.Excluded &&
                resolved.TryGetValue(index, out var existing) && existing != level &&
                string.IsNullOrWhiteSpace(entry.Text))
                throw new InvalidOperationException($"Key ghi hai cấp khác nhau cho paragraph {index}.");
            if (!entry.Excluded)
                resolved[index] = level;
            entries.Add(entry with { Index = index, StableId = null });
        }
        return new AnswerKey(resolved, [], entries, Title, IsPartial);
    }

    public static AnswerKey Parse(string text, string? title = null)
    {
        var levels = new Dictionary<int, int?>();
        var stableLevels = new Dictionary<string, int?>(StringComparer.Ordinal);
        var entries = new List<AnswerKeyEntry>();
        var isPartial = false;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine;
            if (line.Contains("partial_toc", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("partial_human", StringComparison.OrdinalIgnoreCase))
                isPartial = true;
            var hash = line.IndexOf('#');
            var comment = hash >= 0 ? line[(hash + 1)..].Trim() : null;
            if (hash >= 0) line = line[..hash];
            line = line.Trim();
            if (line.Length == 0) continue;

            // Chấp nhận "95 1", "95,1", "95:1", "95" — và nhiều mục trên một dòng cách nhau bởi ';'.
            foreach (var item in line.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = item.Split([' ', '\t', ',', ':'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;
                int? level = null;
                if (parts.Length > 1)
                {
                    if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var lvl))
                        throw new FormatException($"Không đọc được cấp từ: \"{item}\"");
                    level = lvl;
                }

                var excluded = parts[0].StartsWith('!');
                var keyText = excluded ? parts[0][1..] : parts[0];

                if (keyText.StartsWith('@'))
                {
                    var stableId = keyText[1..];
                    if (string.IsNullOrWhiteSpace(stableId))
                        throw new FormatException($"Stable ID rỗng trong: \"{item}\"");
                    if (!excluded)
                        stableLevels[stableId] = level;
                    entries.Add(new AnswerKeyEntry(null, stableId, level, comment, excluded));
                }
                else
                {
                    if (!int.TryParse(keyText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                        throw new FormatException($"Không đọc được chỉ số đoạn từ: \"{item}\"");
                    if (!excluded)
                        levels[index] = level;
                    entries.Add(new AnswerKeyEntry(index, null, level, comment, excluded));
                }
            }
        }

        return new AnswerKey(levels, stableLevels, entries, title, isPartial);
    }

    public static AnswerKey Load(string path) =>
        Parse(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path));

    /// <summary>
    /// Builds an evaluation-only key after a source document has been regenerated and its old
    /// paragraph anchors no longer apply. Production writeback must never use this method.
    /// </summary>
    public static AnswerKey FromResolvedEntries(
        IEnumerable<AnswerKeyEntry> entries,
        string? title = null,
        bool isPartial = false)
    {
        var materialized = entries.ToArray();
        var levels = materialized
            .Where(entry => !entry.Excluded && entry.Index is not null)
            .GroupBy(entry => entry.Index!.Value)
            .ToDictionary(group => group.Key, group => group.Last().Level);
        return new AnswerKey(levels, [], materialized, title, isPartial);
    }

    public static string Write(IEnumerable<(int Index, int Level, string Text)> headings, string title)
    {
        var sb = new StringBuilder();
        sb.Append("# ").AppendLine(title);
        sb.AppendLine("# <chỉ số đoạn> <cấp>   — sinh tự động cùng tài liệu, không gán nhãn tay");
        foreach (var (index, level, text) in headings.OrderBy(h => h.Index))
            sb.Append(index).Append(' ').Append(level).Append("   # ").AppendLine(text);
        return sb.ToString();
    }

    public static string WriteStable(IEnumerable<(string StableId, int Level, string Text)> headings, string title)
    {
        var sb = new StringBuilder();
        sb.Append("# ").AppendLine(title);
        sb.AppendLine("# @<stable-id> <cấp> — sinh từ review đã duyệt; ổn định khi index thay đổi");
        foreach (var (stableId, level, text) in headings.OrderBy(h => h.StableId, StringComparer.Ordinal))
            sb.Append('@').Append(stableId).Append(' ').Append(level).Append("   # ").AppendLine(text);
        return sb.ToString();
    }
}
