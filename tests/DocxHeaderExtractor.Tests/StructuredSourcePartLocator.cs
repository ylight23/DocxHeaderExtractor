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
    /// <para>
    /// Which makes the projection back to source offsets the delicate half, and it was wrong. The
    /// reduced stream remembers a coordinate per surviving character, so a match ended at the last
    /// character that survived reduction - and for a heading ending in punctuation, that is the
    /// character before it. Four of DOC-0252's forty-one headings end in ')', and all four were
    /// migrated to a selection one character short of the wording the same claim recorded as
    /// approved. The source never lost the parenthesis and neither did the model; only this
    /// projection did. Reduction is now undone at both ends before the parts are cut.
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
                if (punctuationInsensitive && Reducible(character))
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

        // Undo the reduction at the edges. The match covers the characters that survived it, so a
        // target that opens or closes on punctuation has coordinates that stop inside itself; the
        // source characters the target itself accounts for are taken back here, and nothing else is.
        var startOffset = punctuationInsensitive
            ? ExtendStart(atoms[first.Atom].Text, first.Offset, LeadingReducible(target))
            : first.Offset;
        var endOffset = punctuationInsensitive
            ? ExtendEnd(atoms[last.Atom].Text, last.Offset + 1, TrailingReducible(target))
            : last.Offset + 1;

        var parts = new List<SemanticSourcePart>();
        for (var index = first.Atom; index <= last.Atom; index++)
        {
            var text = atoms[index].Text;
            var start = index == first.Atom ? startOffset : 0;
            var end = index == last.Atom ? endOffset : text.Length;
            parts.Add(start == 0 && end == text.Length
                ? new SemanticSourcePart(atoms[index].Alias, CanonicalSemanticSelectionMode.WholeAlias)
                : new SemanticSourcePart(atoms[index].Alias, CanonicalSemanticSelectionMode.VerbatimText, text[start..end]));
        }

        return parts;
    }

    /// <summary>
    /// Walks the selection's end forward over the source characters the target's own tail accounts
    /// for. Whitespace is allowed to differ on either side - the reduced form ignored it, so
    /// insisting on it here would refuse matches this function exists to make - but a character
    /// that is neither in the target's tail nor whitespace stops the walk.
    /// </summary>
    private static int ExtendEnd(string text, int end, string tail)
    {
        var index = 0;
        while (end < text.Length && index < tail.Length)
        {
            if (text[end] == tail[index]) { end++; index++; }
            else if (char.IsWhiteSpace(text[end])) end++;
            else if (char.IsWhiteSpace(tail[index])) index++;
            else break;
        }
        return end;
    }

    /// <summary>The same walk at the other edge, for a heading that opens on punctuation.</summary>
    private static int ExtendStart(string text, int start, string head)
    {
        var index = head.Length - 1;
        while (start > 0 && index >= 0)
        {
            if (text[start - 1] == head[index]) { start--; index--; }
            else if (char.IsWhiteSpace(text[start - 1])) start--;
            else if (char.IsWhiteSpace(head[index])) index--;
            else break;
        }
        return start;
    }

    /// <summary>The target's own trailing run of characters that reduction would drop.</summary>
    private static string TrailingReducible(string target)
    {
        var index = target.Length;
        while (index > 0 && Reducible(target[index - 1])) index--;
        return target[index..];
    }

    /// <summary>The target's own leading run of characters that reduction would drop.</summary>
    private static string LeadingReducible(string target)
    {
        var index = 0;
        while (index < target.Length && Reducible(target[index])) index++;
        return target[..index];
    }

    private static bool Reducible(char character) =>
        char.IsWhiteSpace(character) || char.IsPunctuation(character);

    internal static string Reduce(string text) =>
        new(text.Where(character => !Reducible(character))
            .Select(char.ToLowerInvariant).ToArray());
}
