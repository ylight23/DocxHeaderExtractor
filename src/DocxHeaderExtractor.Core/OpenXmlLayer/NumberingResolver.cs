using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.OpenXmlLayer;

/// <summary>
/// Dựng nhãn list Word hiển thị từ numbering.xml. Word thường không ghi "1.2" trong text của
/// paragraph; nó chỉ ghi numId/ilvl, nên đây là evidence cấu trúc cần có trước khi hỏi model.
/// </summary>
public static class NumberingResolver
{
    private sealed record LevelDefinition(int Start, string Format, string Text, string? StyleId);

    public static void Apply(MainDocumentPart mainPart, IReadOnlyList<SlimParagraph> paragraphs)
    {
        var numbering = mainPart.NumberingDefinitionsPart?.Numbering;
        if (numbering is null) return;

        var abstractLevels = ReadAbstractLevels(numbering);
        var instances = ReadInstances(numbering, abstractLevels, mainPart);
        var counters = new Dictionary<(int NumId, int Level), int>();

        foreach (var paragraph in paragraphs)
        {
            if (paragraph.NumberingId is not { } numId || paragraph.NumberingLevel is not { } level ||
                !instances.TryGetValue(numId, out var levels) || !levels.TryGetValue(level, out var definition))
                continue;

            var key = (numId, level);
            counters[key] = counters.TryGetValue(key, out var previous) ? previous + 1 : definition.Start;
            foreach (var child in counters.Keys.Where(k => k.NumId == numId && k.Level > level).ToArray())
                counters.Remove(child);

            paragraph.NumberingDepth = level + 1;
            paragraph.NumberingFormat = definition.Format;
            paragraph.NumberLabel = Render(definition.Text, levels, counters, numId, level);
            // Cấp của danh sách chỉ trở thành cấp heading khi CHÍNH danh sách khai báo nó gắn với
            // style Heading — không suy ra từ độ sâu list, vì danh sách gạch đầu dòng cũng có độ sâu.
            paragraph.NumberingStyleLevel = HeadingHeuristics.BuiltInLevelFromStyleId(definition.StyleId);
        }
    }

    internal static void Apply(MainDocumentPart mainPart, IReadOnlyList<OpenXmlSourceParagraph> paragraphs)
    {
        var numbering = mainPart.NumberingDefinitionsPart?.Numbering;
        if (numbering is null) return;
        var abstractLevels = ReadAbstractLevels(numbering);
        var instances = ReadInstances(numbering, abstractLevels, mainPart);
        var counters = new Dictionary<(int NumId, int Level), int>();
        foreach (var paragraph in paragraphs)
        {
            if (paragraph.NumberingId is not { } numId || paragraph.NumberingLevel is not { } level ||
                !instances.TryGetValue(numId, out var levels) || !levels.TryGetValue(level, out var definition)) continue;
            var key = (numId, level);
            counters[key] = counters.TryGetValue(key, out var previous) ? previous + 1 : definition.Start;
            foreach (var child in counters.Keys.Where(k => k.NumId == numId && k.Level > level).ToArray()) counters.Remove(child);
            paragraph.NumberingFormat = definition.Format;
            paragraph.NumberLabel = Render(definition.Text, levels, counters, numId, level);
            paragraph.NumberingStyleLevel = HeadingHeuristics.BuiltInLevelFromStyleId(definition.StyleId);
        }
    }

    /// <summary>Định dạng một w:lvlText bằng bộ đếm hiện tại.</summary>
    private static string Render(
        string template,
        IReadOnlyDictionary<int, LevelDefinition> levels,
        IReadOnlyDictionary<(int NumId, int Level), int> counters,
        int numId,
        int currentLevel)
    {
        var result = template;
        for (var reference = 1; reference <= 9; reference++)
        {
            var level = reference - 1;
            if (!levels.TryGetValue(level, out var definition)) continue;
            var value = counters.TryGetValue((numId, level), out var counter) ? counter : definition.Start;
            result = result.Replace("%" + reference, Format(value, definition.Format), StringComparison.Ordinal);
        }

        return string.IsNullOrWhiteSpace(result)
            ? Format(counters[(numId, currentLevel)], levels[currentLevel].Format) + "."
            : result;
    }

    private sealed record AbstractDefinition(
        Dictionary<int, LevelDefinition> Levels,
        string? NumStyleLink,
        string? StyleLink);

    private static Dictionary<int, AbstractDefinition> ReadAbstractLevels(OpenXmlElement numbering)
    {
        var result = new Dictionary<int, AbstractDefinition>();
        foreach (var abstractNum in numbering.ChildElements.Where(e => e.LocalName == "abstractNum"))
        {
            if (!TryInt(Attr(abstractNum, "abstractNumId"), out var id)) continue;
            var levels = new Dictionary<int, LevelDefinition>();
            foreach (var level in abstractNum.ChildElements.Where(e => e.LocalName == "lvl"))
            {
                if (!TryInt(Attr(level, "ilvl"), out var index)) continue;
                var start = ChildValue(level, "start");
                var format = ChildValue(level, "numFmt") ?? "decimal";
                var text = ChildValue(level, "lvlText") ?? $"%{index + 1}.";
                // w:pStyle trong w:lvl là ánh xạ cấp danh sách → paragraph style. Đây là chỗ Word
                // ghi lại lựa chọn "Link level to style" của hộp thoại multilevel list.
                var styleId = ChildValue(level, "pStyle");
                levels[index] = new LevelDefinition(
                    TryInt(start, out var parsedStart) ? parsedStart : 1, format, text, styleId);
            }
            result[id] = new AbstractDefinition(
                levels,
                ChildValue(abstractNum, "numStyleLink"),
                ChildValue(abstractNum, "styleLink"));
        }
        return result;
    }

    private static Dictionary<int, Dictionary<int, LevelDefinition>> ReadInstances(
        OpenXmlElement numbering,
        Dictionary<int, AbstractDefinition> abstractLevels,
        MainDocumentPart mainPart)
    {
        var byStyleLink = abstractLevels
            .Where(x => !string.IsNullOrWhiteSpace(x.Value.StyleLink))
            .GroupBy(x => x.Value.StyleLink!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Key, StringComparer.OrdinalIgnoreCase);
        var styleNumIds = ReadStyleNumberingIds(mainPart);

        var result = new Dictionary<int, Dictionary<int, LevelDefinition>>();
        foreach (var num in numbering.ChildElements.Where(e => e.LocalName == "num"))
        {
            if (!TryInt(Attr(num, "numId"), out var numId) || !TryInt(ChildValue(num, "abstractNumId"), out var abstractId) ||
                !abstractLevels.TryGetValue(abstractId, out var source))
                continue;

            source = ResolveStyleLink(source, abstractLevels, byStyleLink, styleNumIds, numbering);
            var levels = source.Levels.ToDictionary(x => x.Key, x => x.Value);
            foreach (var overrideElement in num.ChildElements.Where(e => e.LocalName == "lvlOverride"))
            {
                if (!TryInt(Attr(overrideElement, "ilvl"), out var level) || !levels.TryGetValue(level, out var original)) continue;
                var overridden = original;
                if (TryInt(ChildValue(overrideElement, "startOverride"), out var start))
                    overridden = overridden with { Start = start };

                // w:lvlOverride có thể chứa hẳn w:lvl với numFmt/lvlText khác abstractNum.
                var levelDefinition = overrideElement.ChildElements.FirstOrDefault(e => e.LocalName == "lvl");
                if (levelDefinition is not null)
                {
                    var ownStart = ChildValue(levelDefinition, "start");
                    var ownFormat = ChildValue(levelDefinition, "numFmt");
                    var ownText = ChildValue(levelDefinition, "lvlText");
                    overridden = overridden with
                    {
                        Start = TryInt(ownStart, out var parsedStart) ? parsedStart : overridden.Start,
                        Format = ownFormat ?? overridden.Format,
                        Text = ownText ?? overridden.Text,
                    };
                }
                levels[level] = overridden;
            }
            result[numId] = levels;
        }
        return result;
    }

    /// <summary>
    /// Lần theo "list style" (thư viện numbering dùng chung). Một abstractNum có thể không chứa
    /// định nghĩa nào mà chỉ mang <c>w:numStyleLink</c> trỏ tới một paragraph style; style đó lại
    /// gắn với một numId khác, và abstractNum thật nằm ở cuối chuỗi trỏ. Không lần thì mọi đoạn
    /// dùng thư viện danh sách sẽ ra numbering rỗng — đúng kiểu tài liệu hành chính dùng chung
    /// một bộ danh sách cho cả cơ quan.
    /// </summary>
    private static AbstractDefinition ResolveStyleLink(
        AbstractDefinition source,
        Dictionary<int, AbstractDefinition> abstractLevels,
        Dictionary<string, int> byStyleLink,
        Dictionary<string, int> styleNumIds,
        OpenXmlElement numbering)
    {
        var seen = new HashSet<int>();
        // Theo đặc tả, abstractNum mang w:numStyleLink là bản trỏ chứ không phải bản định nghĩa —
        // luôn đi tiếp. Chuỗi trỏ có thể vòng lại chính nó trong file hỏng nên chặn bằng tập đã thăm.
        while (!string.IsNullOrWhiteSpace(source.NumStyleLink))
        {
            var styleId = source.NumStyleLink!;
            int? target = byStyleLink.TryGetValue(styleId, out var direct) ? direct : null;
            if (target is null && styleNumIds.TryGetValue(styleId, out var numId))
            {
                var num = numbering.ChildElements
                    .FirstOrDefault(e => e.LocalName == "num" && Attr(e, "numId") == numId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (num is not null && TryInt(ChildValue(num, "abstractNumId"), out var abstractId)) target = abstractId;
            }

            if (target is not { } resolved || !seen.Add(resolved) || !abstractLevels.TryGetValue(resolved, out var next))
                break;
            source = next;
        }
        return source;
    }

    /// <summary>styleId → numId mà chính style đó khai trong <c>w:pPr/w:numPr</c> của styles.xml.</summary>
    private static Dictionary<string, int> ReadStyleNumberingIds(MainDocumentPart mainPart)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var styles = mainPart.StyleDefinitionsPart?.Styles;
        if (styles is null) return result;

        foreach (var style in styles.ChildElements.Where(e => e.LocalName == "style"))
        {
            var styleId = Attr(style, "styleId");
            if (string.IsNullOrWhiteSpace(styleId)) continue;
            var numPr = style.ChildElements.FirstOrDefault(e => e.LocalName == "pPr")?
                .ChildElements.FirstOrDefault(e => e.LocalName == "numPr");
            if (numPr is null) continue;
            if (TryInt(ChildValue(numPr, "numId"), out var numId)) result[styleId] = numId;
        }
        return result;
    }

    private static string? Attr(OpenXmlElement element, string localName) =>
        element.GetAttributes().FirstOrDefault(a => a.LocalName == localName).Value;

    private static string? ChildValue(OpenXmlElement parent, string localName) =>
        parent.ChildElements.FirstOrDefault(e => e.LocalName == localName)?.GetAttributes()
            .FirstOrDefault(a => a.LocalName == "val").Value;

    private static bool TryInt(string? value, out int number) => int.TryParse(value, out number);

    private static string Format(int value, string format) => format.ToLowerInvariant() switch
    {
        "upperroman" => Roman(value),
        "lowerroman" => Roman(value).ToLowerInvariant(),
        "upperletter" => Letters(value),
        "lowerletter" => Letters(value).ToLowerInvariant(),
        "bullet" => "•",
        _ => value.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    private static string Letters(int value)
    {
        if (value <= 0) return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var chars = new Stack<char>();
        while (value > 0) { value--; chars.Push((char)('A' + value % 26)); value /= 26; }
        return new string(chars.ToArray());
    }

    private static string Roman(int value)
    {
        if (value is <= 0 or > 3999) return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var map = new (int Value, string Text)[] { (1000,"M"), (900,"CM"), (500,"D"), (400,"CD"), (100,"C"), (90,"XC"), (50,"L"), (40,"XL"), (10,"X"), (9,"IX"), (5,"V"), (4,"IV"), (1,"I") };
        var result = new System.Text.StringBuilder();
        foreach (var (part, text) in map)
            while (value >= part) { result.Append(text); value -= part; }
        return result.ToString();
    }
}
