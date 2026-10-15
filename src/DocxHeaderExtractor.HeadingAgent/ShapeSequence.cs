using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DocxHeaderExtractor.HeadingAgent;

/// <summary>
/// The numbered sequences of a document, read from the form of its lines only. A line's sequence key is the words before its first number
/// (at most <see cref="MaxPrefixWords"/>), then # for the number, then the punctuation written right after it: "Điều 4." and "Điều 17." share
/// "Điều #.", "Chương IV" is "Chương #", "2. Text" is "#.". Lines that share a key form one sequence in reading order, and the sequence is
/// described by what its numbers do: where a number is missing, repeated, or smaller than the one before. No word is interpreted and no line is
/// called a heading: a missing number may be an OCR misreading, a gap in the source, or a sequence that legitimately skips or restarts.
/// <para>
/// Tokens made only of symbols ("-", "•", "–") are not words of the key: a list dash or a speck the OCR read in front of a label does not make
/// a sequence of its own. For every gap the result also lists the lines that stand between the two neighbours of the gap in reading order and
/// carry exactly a missing number under another key (a label the OCR misspelled, a comma for a full stop). The number must match exactly; the
/// key is not compared at all, so nothing is matched approximately, and whether such a line belongs to the sequence is not decided here.
/// </para>
/// </summary>
internal static class ShapeSequence
{
    public const string Version = "SHAPE_SEQUENCE_V2";
    public const int MaxPrefixWords = 2, MaxAnomaliesShown = 20, WindowEach = 4, WindowTextChars = 80, MaxCandidatesPerGap = 3;

    static readonly Regex Decimal = new(@"^\d{1,4}$", RegexOptions.CultureInvariant);
    static readonly Regex Roman = new(@"^[IVXLCDM]{1,7}$", RegexOptions.CultureInvariant);
    static readonly char[] Trailing = ['.', ':', ')', ';', ','];

    /// <summary>A line's key and number, or null when no number stands among its first words. Roman numbers count only with words before them or
    /// punctuation after them, so that a bare "I" opening a sentence is not read as one.</summary>
    public static (string Key, int Value, string Form)? KeyOf(string text)
    {
        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).SkipWhile(t => t.All(c => !char.IsLetterOrDigit(c))).ToArray();
        for (var i = 0; i < Math.Min(tokens.Length, MaxPrefixWords + 1); i++)
        {
            var token = tokens[i].TrimStart('(');
            var core = token.TrimEnd(Trailing);
            var suffix = token[core.Length..];
            int value;
            string form;
            if (Decimal.IsMatch(core)) { value = int.Parse(core, System.Globalization.CultureInfo.InvariantCulture); form = "decimal"; }
            else if (Roman.IsMatch(core) && (i > 0 || suffix.Length > 0) && RomanValue(core) is { } r) { value = r; form = "roman"; }
            else continue;
            var prefix = string.Join(" ", tokens[..i]);
            return ((prefix.Length > 0 ? prefix + " " : "") + "#" + suffix, value, form);
        }
        return null;
    }

    static string Clip(string text) => text.Length <= WindowTextChars ? text : text[..WindowTextChars] + "…";

    static int? RomanValue(string s)
    {
        static int V(char c) => c switch { 'I' => 1, 'V' => 5, 'X' => 10, 'L' => 50, 'C' => 100, 'D' => 500, 'M' => 1000, _ => 0 };
        var total = 0;
        for (var i = 0; i < s.Length; i++) total += i + 1 < s.Length && V(s[i]) < V(s[i + 1]) ? -V(s[i]) : V(s[i]);
        // Only the canonical spelling: "IIII" or "VX" are not read as numbers.
        return total > 0 && ToRoman(total) == s ? total : null;
    }

    static string ToRoman(int n)
    {
        var map = new (int, string)[] { (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"), (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I") };
        var s = "";
        foreach (var (v, r) in map) while (n >= v) { s += r; n -= v; }
        return s;
    }

    /// <summary>The sequence one line belongs to: the key, how many lines share it, the numbers in reading order, the anomalies, and the lines
    /// just before and after the target in the sequence. Null when the line has no key.</summary>
    public static JsonObject? Describe(IReadOnlyList<AgentSourceLine> lines, int target)
    {
        if (KeyOf(lines[target].Text) is not { } key) return null;
        var keys = lines.Select(l => KeyOf(l.Text)).ToArray();
        var members = new List<(int Index, int Value)>();
        for (var i = 0; i < lines.Count; i++)
            if (keys[i] is { } k && k.Key == key.Key) members.Add((i, k.Value));
        var at = members.FindIndex(m => m.Index == target);

        var gaps = new JsonArray();
        var decreases = new JsonArray();
        int gapCount = 0, decreaseCount = 0;
        for (var k = 1; k < members.Count; k++)
        {
            var (prev, cur) = (members[k - 1], members[k]);
            if (cur.Value > prev.Value + 1)
            {
                if (gapCount++ < MaxAnomaliesShown)
                {
                    var candidates = Enumerable.Range(prev.Index + 1, cur.Index - prev.Index - 1)
                        .Where(i => keys[i] is { } c && c.Key != key.Key && c.Value > prev.Value && c.Value < cur.Value)
                        .Take(MaxCandidatesPerGap)
                        .Select(i => (JsonNode)new JsonObject { ["alias"] = lines[i].Alias, ["key"] = keys[i]!.Value.Key, ["value"] = keys[i]!.Value.Value, ["text"] = Clip(lines[i].Text) })
                        .ToArray();
                    gaps.Add(new JsonObject
                    {
                        ["after"] = prev.Value, ["before"] = cur.Value, ["missingCount"] = cur.Value - prev.Value - 1,
                        ["afterAlias"] = lines[prev.Index].Alias, ["beforeAlias"] = lines[cur.Index].Alias,
                        ["missingNumberUnderAnotherKey"] = new JsonArray(candidates),
                    });
                }
            }
            else if (cur.Value < prev.Value && decreaseCount++ < MaxAnomaliesShown)
                decreases.Add(new JsonObject { ["from"] = prev.Value, ["to"] = cur.Value, ["fromAlias"] = lines[prev.Index].Alias, ["toAlias"] = lines[cur.Index].Alias });
        }
        var repeated = members.GroupBy(m => m.Value).Where(g => g.Count() > 1).ToArray();
        JsonNode Entry((int Index, int Value) m)
        {
            return new JsonObject { ["alias"] = lines[m.Index].Alias, ["readingOrder"] = m.Index, ["value"] = m.Value, ["text"] = Clip(lines[m.Index].Text) };
        }
        var from = Math.Max(0, at - WindowEach);
        return new JsonObject
        {
            ["alias"] = lines[target].Alias,
            ["key"] = key.Key, ["form"] = key.Form, ["value"] = key.Value,
            ["linesWithThisKey"] = members.Count, ["placeInSequence"] = at + 1,
            ["firstValue"] = members[0].Value, ["lastValue"] = members[^1].Value,
            ["distinctValues"] = members.Select(m => m.Value).Distinct().Count(),
            ["gaps"] = gaps, ["gapsTotal"] = gapCount,
            ["decreases"] = decreases, ["decreasesTotal"] = decreaseCount,
            ["repeatedValues"] = new JsonArray(repeated.Take(MaxAnomaliesShown).Select(g => (JsonNode)new JsonObject
            {
                ["value"] = g.Key, ["count"] = g.Count(), ["aliases"] = new JsonArray(g.Take(4).Select(m => (JsonNode)lines[m.Index].Alias).ToArray()),
            }).ToArray()),
            ["repeatedValuesTotal"] = repeated.Length,
            ["window"] = new JsonArray(members.Skip(from).Take(Math.Min(members.Count, at + WindowEach + 1) - from).Select(Entry).ToArray()),
        };
    }
}
