using System.Text;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Semantics.Binding;

/// <summary>
/// How a bound claim reads, which is not what it is.
/// <para>
/// Identity is the tuple of coordinates; this is a rendering of it. They are kept apart on purpose:
/// a projection that replaced identity would make two different selections compare equal because
/// they happened to read the same, and a heading split across a line break would lose the record of
/// where it actually came from.
/// </para>
/// <para>
/// The rules are few and deliberately shy. A hyphen at a row break is kept rather than removed,
/// because nothing in the geometry distinguishes a word broken across lines from a compound that
/// was already hyphenated, and keeping it is the choice that can be undone.
/// </para>
/// </summary>
public static class SemanticSourceProjection
{
    /// <summary>Characters that end a part with a break rather than with a word.</summary>
    public const string ContinuationHyphens = "-‐‑­";

    public static string Render(IReadOnlyList<BoundSourcePart> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Count == 0) return string.Empty;

        var text = new StringBuilder(parts[0].Text);
        for (var index = 1; index < parts.Count; index++)
        {
            var previous = parts[index - 1].Text;
            var current = parts[index].Text;

            // No separator where the source already has one, where the earlier part ends on a
            // break hyphen, or where the later part opens with punctuation that belongs to the
            // word before it. A single space otherwise - never more, and never a guess at one.
            var joined =
                previous.Length == 0 || current.Length == 0 ||
                char.IsWhiteSpace(previous[^1]) || char.IsWhiteSpace(current[0]) ||
                ContinuationHyphens.Contains(previous[^1]) ||
                IsTrailingPunctuation(current[0]);

            if (!joined) text.Append(' ');
            text.Append(current);
        }

        return text.ToString();
    }

    private static bool IsTrailingPunctuation(char character) => character is '.' or ',' or ';' or ':' or '!' or '?' or ')' or ']';
}
