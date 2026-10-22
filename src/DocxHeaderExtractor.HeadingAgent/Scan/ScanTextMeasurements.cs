using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.HeadingAgent.Formats;
using DocxHeaderExtractor.HeadingAgent.Protocol;
using DocxHeaderExtractor.HeadingAgent.Tools;

namespace DocxHeaderExtractor.HeadingAgent.Scan;

/// <summary>Literal observations of the declared reading itself, each line on its own. No heading, body,
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
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var facts = new JsonObject { ["origin"] = "TEXT_MEASURED", ["readingViewId"] = readingOf(line.Alias) };
            if (LeadingToken(line.Text) is { } leading) facts["leadingToken"] = leading;
            if (shapes[i] is { } shape) facts["numberedPrefix"] = Shape(shape);
            Ending(facts, line.Text);
            result[line.Alias] = facts;
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

}
