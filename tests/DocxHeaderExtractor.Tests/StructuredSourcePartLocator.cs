using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Finds the fewest source parts that represent a heading exactly over a run of coordinate atoms.
/// <para>
/// An audit tool, not a binder: it proposes what the parts would be, and the real binder is what
/// decides whether they resolve. Shared so the structured-binding audit and the evidence authority
/// measure the same thing rather than two implementations of it.
/// </para>
/// </summary>
internal static class StructuredSourcePartLocator
{
    /// <summary>
    /// The fewest parts that represent <paramref name="target"/> exactly, or null when no run of
    /// adjacent atoms holds it.
    /// <para>
    /// Punctuation-insensitive matching exists for one reason: the old line reconstruction dropped
    /// characters before Gold was written against it. Matching on the reduced form finds the
    /// heading; the parts that come back quote the source, not the reduced form.
    /// </para>
    /// </summary>
    public static List<SemanticSourcePart>? Locate(
        IReadOnlyList<SemanticSourceAtom> atoms, string target, bool punctuationInsensitive, ref int cursor)
    {
        var stream = new System.Text.StringBuilder();
        var owner = new List<(int Atom, int Offset)>();

        for (var index = 0; index < atoms.Count; index++)
        {
            if (!punctuationInsensitive && index > 0)
            {
                stream.Append(' ');
                owner.Add((-1, -1));
            }

            var text = atoms[index].Text;
            for (var at = 0; at < text.Length; at++)
            {
                var character = text[at];
                if (punctuationInsensitive && (char.IsWhiteSpace(character) || char.IsPunctuation(character)))
                    continue;
                stream.Append(punctuationInsensitive ? char.ToLowerInvariant(character) : character);
                owner.Add((index, at));
            }
        }

        var wanted = punctuationInsensitive ? Reduce(target) : target;
        if (wanted.Length == 0) return null;

        var found = stream.ToString().IndexOf(wanted, Math.Min(cursor, Math.Max(0, stream.Length - 1)), StringComparison.Ordinal);
        if (found < 0) found = stream.ToString().IndexOf(wanted, StringComparison.Ordinal);
        if (found < 0) return null;
        cursor = found + 1;

        var first = owner[found];
        var last = owner[found + wanted.Length - 1];
        if (first.Atom < 0 || last.Atom < 0) return null;

        var parts = new List<SemanticSourcePart>();
        for (var index = first.Atom; index <= last.Atom; index++)
        {
            var text = atoms[index].Text;
            var start = index == first.Atom ? first.Offset : 0;
            var end = index == last.Atom ? last.Offset + 1 : text.Length;
            parts.Add(start == 0 && end == text.Length
                ? new SemanticSourcePart(atoms[index].Alias, CanonicalSemanticSelectionMode.WholeAlias)
                : new SemanticSourcePart(atoms[index].Alias, CanonicalSemanticSelectionMode.VerbatimText, text[start..end]));
        }

        return parts;
    }

    internal static string Reduce(string text) =>
        new(text.Where(character => !char.IsWhiteSpace(character) && !char.IsPunctuation(character))
            .Select(char.ToLowerInvariant).ToArray());
}
