using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DocxHeaderExtractor.HeadingAgent.Scan;

/// <summary>Literal observations of the declared reading and its neighbours. No heading, body,
/// sentence-completion or containment labels are inferred. A preceding numbered label is only a
/// position in reading order; its words are never matched against a language-specific vocabulary.</summary>
internal static partial class ScanTextMeasurements
{
    const int MaxPunctuationShown = 4;

    [GeneratedRegex(@"^\s*(?<token>(?<digits>[0-9]+(?:\.[0-9]+)*)[.)]?|\([0-9]+\)|\p{L}{1,7}[.)]|\(\p{L}\))(?=\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingTokenPattern();

    internal static IReadOnlyDictionary<string, JsonObject> Build(IReadOnlyList<AgentSourceLine> lines, Func<string, string> readingOf)
    {
        var shapes = lines.Select(l => ShapeOf(l.Text)).ToArray();
        var result = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        int? previousNamed = null;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var facts = new JsonObject { ["origin"] = "TEXT_MEASURED", ["readingViewId"] = readingOf(line.Alias) };
            if (LeadingToken(line.Text) is { } leading) facts["leadingToken"] = leading;
            if (shapes[i] is { } shape) facts["numberedPrefix"] = Shape(shape);
            Ending(facts, line.Text);
            if (i > 0)
            {
                var previous = Reference(lines[i - 1], readingOf);
                Ending(previous, lines[i - 1].Text);
                facts["previousLine"] = previous;
            }
            if (i + 1 < lines.Count)
            {
                var next = Reference(lines[i + 1], readingOf);
                next["firstLetter"] = FirstLetter(lines[i + 1].Text);
                if (LeadingToken(lines[i + 1].Text) is { } token) next["leadingToken"] = token;
                facts["nextLine"] = next;
            }
            if (previousNamed is { } at)
            {
                var preceding = Reference(lines[at], readingOf);
                preceding["numberedPrefix"] = Shape(shapes[at]!.Value);
                preceding["linesBetween"] = i - at - 1;
                facts["nearestPrecedingNamedNumber"] = preceding;
            }
            result[line.Alias] = facts;
            // The prefix can be ANY words (including OCR errors or an inline reference). This does
            // not declare it a heading, nor declare later lines its children.
            if (shapes[i] is { } named && named.Key.IndexOf('#') > 0) previousNamed = i;
        }
        return result;
    }

    static (string Key, int Value, string Form)? ShapeOf(string text)
    {
        try { return ShapeSequence.KeyOf(text); }
        catch (FormatException) { return null; } // a digit spelling the existing numeric reader cannot parse
    }

    static JsonObject Shape((string Key, int Value, string Form) shape) =>
        new() { ["key"] = shape.Key, ["value"] = shape.Value, ["form"] = shape.Form };

    static JsonObject Reference(AgentSourceLine line, Func<string, string> readingOf) =>
        new() { ["alias"] = line.Alias, ["page"] = line.Page, ["readingViewId"] = readingOf(line.Alias) };

    static JsonObject? LeadingToken(string text)
    {
        var match = LeadingTokenPattern().Match(text);
        if (!match.Success) return null;
        var token = match.Groups["token"];
        return new JsonObject
        {
            ["text"] = token.Value, ["start"] = token.Index, ["end"] = token.Index + token.Length,
            ["form"] = token.Value.Any(c => c is >= '0' and <= '9') ? "decimal" : "letters",
        };
    }

    static void Ending(JsonObject facts, string text)
    {
        var trimmed = text.TrimEnd();
        var start = trimmed.Length;
        while (start > 0 && char.IsPunctuation(trimmed[start - 1])) start--;
        var count = trimmed.Length - start;
        facts["endingPunctuation"] = trimmed[Math.Max(start, trimmed.Length - MaxPunctuationShown)..];
        if (count > MaxPunctuationShown) facts["endingPunctuationTotalChars"] = count;
    }

    static JsonObject? FirstLetter(string text)
    {
        foreach (var letter in text.EnumerateRunes())
            if (Rune.IsLetter(letter)) return new JsonObject
            {
                ["text"] = letter.ToString(), ["case"] = Rune.IsLower(letter) ? "lower" : Rune.IsUpper(letter) ? "upper" : "uncased",
            };
        return null;
    }

    /// <summary>These references were actually shown, but do not count as returning the referenced lines' core facts.</summary>
    internal static IEnumerable<string> ReferencedAliases(JsonNode facts)
    {
        foreach (var field in new[] { "previousLine", "nextLine", "nearestPrecedingNamedNumber" })
            if (facts["surface"]?[field]?["alias"] is JsonValue alias) yield return alias.GetValue<string>();
    }
}
