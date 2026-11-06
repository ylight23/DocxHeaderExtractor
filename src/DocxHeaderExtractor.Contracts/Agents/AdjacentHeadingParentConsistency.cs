using System.Text.Json;
using DocxHeaderExtractor.Contracts.Models;

namespace DocxHeaderExtractor.Contracts.Agents;

/// <summary>
/// Finds a review-worthy contradiction: consecutive selected headings with the same measured
/// paragraph style, rendered numbering marker and numbering level were assigned different parents.
/// It does not infer which parent is right. An omitted heading between them is also possible.
/// The pipeline holds such a pair for review; the model is not asked again.
/// </summary>
public static class AdjacentHeadingParentConsistency
{
    public const string ResolutionBasis = "adjacent-heading-parent-disagreement";
    public const string Code = "SAME_RENDERED_HEADING_KIND_DIFFERENT_PARENT";

    public sealed record Conflict(
        IReadOnlyList<string> Aliases,
        IReadOnlyList<string> ProposedParents,
        string StyleName,
        string NumberLabel,
        int NumberLevel);

    private sealed record Signature(string StyleName, string NumberLabel, int NumberLevel, bool Bold);

    public static IReadOnlyList<Conflict> Find(
        IReadOnlyList<CanonicalSemanticBoundHeading> ordered,
        IReadOnlyDictionary<string, string> proposedParents,
        HeadingPlacementEvidence evidence)
    {
        var conflicts = new List<Conflict>();
        for (var i = 1; i < ordered.Count; i++)
        {
            var previous = ordered[i - 1];
            var current = ordered[i];
            if (!proposedParents.TryGetValue(previous.Alias, out var previousParent) ||
                !proposedParents.TryGetValue(current.Alias, out var currentParent) ||
                string.Equals(previousParent, currentParent, StringComparison.Ordinal) ||
                previousParent is "NONE" or "UNRESOLVED" || currentParent is "NONE" or "UNRESOLVED")
                continue;

            if (!evidence.StyleJsonByAlias.TryGetValue(previous.Alias, out var previousJson) ||
                !evidence.StyleJsonByAlias.TryGetValue(current.Alias, out var currentJson) ||
                !TrySignature(previousJson, evidence.NumberFormatByAlias.GetValueOrDefault(previous.Alias), out var previousSignature) ||
                !TrySignature(currentJson, evidence.NumberFormatByAlias.GetValueOrDefault(current.Alias), out var currentSignature) ||
                previousSignature != currentSignature)
                continue;

            conflicts.Add(new([previous.Alias, current.Alias], [previousParent, currentParent],
                previousSignature.StyleName, previousSignature.NumberLabel, previousSignature.NumberLevel));
        }
        return conflicts;
    }

    private static bool TrySignature(string json, string? numberFormat, out Signature signature)
    {
        signature = null!;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !(ReadString(root, "styleName", out var styleName) || ReadString(root, "name", out styleName)) ||
                !ReadString(root, "numberLabel", out var numberLabel) ||
                !root.TryGetProperty("numberLevel", out var level) || !level.TryGetInt32(out var numberLevel) ||
                !root.TryGetProperty("bold", out var bold) || bold.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return false;
            signature = new(styleName, MarkerKind(numberLabel, numberFormat), numberLevel, bold.GetBoolean());
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// The kind of a list marker. With Word's numbering format, a counted list is its format plus the marker's shape ("upperRoman|A." for
    /// "I." and "II.", "upperLetter|A." for "A." and "B."), so roman numerals and letters stay different kinds; a bullet is its glyph.
    /// Without the format only digits are read as a counter: "1." and "2." are one kind, "I." and "A." stay different.
    /// </summary>
    public static string MarkerKind(string label, string? numberFormat) =>
        numberFormat is null ? DigitKind(label)
        : numberFormat is "bullet" or "none" ? numberFormat + "|" + label.Trim()
        : numberFormat + "|" + LabelKind(label);

    static string DigitKind(string label)
    {
        var kind = new System.Text.StringBuilder(label.Length);
        foreach (var c in label.Trim())
            if (!char.IsDigit(c) || kind.Length == 0 || kind[^1] != '#') kind.Append(char.IsDigit(c) ? '#' : c);
        return kind.ToString();
    }

    /// <summary>
    /// The kind of a rendered marker, not its value: each run of digits reads as "#" and each run of letters as "A" or "a",
    /// so "1." and "2.", "a)" and "b)", "I." and "II." are one kind, while "•", "-" and "1." stay different kinds.
    /// </summary>
    public static string LabelKind(string label)
    {
        var kind = new System.Text.StringBuilder(label.Length);
        char? last = null;
        foreach (var c in label.Trim())
        {
            var mapped = char.IsDigit(c) ? '#' : char.IsLetter(c) ? (char.IsUpper(c) ? 'A' : 'a') : c;
            if ((mapped is '#' or 'A' or 'a') && last == mapped) continue;
            kind.Append(mapped);
            last = mapped;
        }
        return kind.ToString();
    }

    private static bool ReadString(JsonElement root, string name, out string value)
    {
        value = "";
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String) return false;
        value = element.GetString() ?? "";
        return value.Length > 0;
    }
}
