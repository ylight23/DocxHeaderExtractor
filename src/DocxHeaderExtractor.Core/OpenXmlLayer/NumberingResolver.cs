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
    private sealed record LevelDefinition(int Start, string Format, string Text);

    public static void Apply(MainDocumentPart mainPart, IReadOnlyList<SlimParagraph> paragraphs)
    {
        var numbering = mainPart.NumberingDefinitionsPart?.Numbering;
        if (numbering is null) return;

        var abstractLevels = ReadAbstractLevels(numbering);
        var instances = ReadInstances(numbering, abstractLevels);
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

    private static Dictionary<int, Dictionary<int, (int Start, string Format, string Text)>> ReadAbstractLevels(OpenXmlElement numbering)
    {
        var result = new Dictionary<int, Dictionary<int, (int Start, string Format, string Text)>>();
        foreach (var abstractNum in numbering.ChildElements.Where(e => e.LocalName == "abstractNum"))
        {
            if (!TryInt(Attr(abstractNum, "abstractNumId"), out var id)) continue;
            var levels = new Dictionary<int, (int Start, string Format, string Text)>();
            foreach (var level in abstractNum.ChildElements.Where(e => e.LocalName == "lvl"))
            {
                if (!TryInt(Attr(level, "ilvl"), out var index)) continue;
                var start = ChildValue(level, "start");
                var format = ChildValue(level, "numFmt") ?? "decimal";
                var text = ChildValue(level, "lvlText") ?? $"%{index + 1}.";
                levels[index] = (TryInt(start, out var parsedStart) ? parsedStart : 1, format, text);
            }
            result[id] = levels;
        }
        return result;
    }

    private static Dictionary<int, Dictionary<int, LevelDefinition>> ReadInstances(
        OpenXmlElement numbering,
        Dictionary<int, Dictionary<int, (int Start, string Format, string Text)>> abstractLevels)
    {
        var result = new Dictionary<int, Dictionary<int, LevelDefinition>>();
        foreach (var num in numbering.ChildElements.Where(e => e.LocalName == "num"))
        {
            if (!TryInt(Attr(num, "numId"), out var numId) || !TryInt(ChildValue(num, "abstractNumId"), out var abstractId) ||
                !abstractLevels.TryGetValue(abstractId, out var source))
                continue;

            var levels = source.ToDictionary(x => x.Key, x => new LevelDefinition(x.Value.Start, x.Value.Format, x.Value.Text));
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
