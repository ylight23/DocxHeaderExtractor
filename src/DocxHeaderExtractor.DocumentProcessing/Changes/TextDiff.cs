using System.Text.RegularExpressions;

namespace DocxHeaderExtractor.DocumentProcessing.Changes;

/// <summary>A span of one side's raw text: UTF-16 offsets, end exclusive, and the exact substring at those offsets.</summary>
public sealed record TextSpan(int Start, int End, string Text);

/// <summary>One run of differing tokens between two raw texts. A side with no tokens in the hunk has an empty span at the insertion point.</summary>
public sealed record TextHunk(string Kind, TextSpan Template, TextSpan Document);

/// <summary>
/// Word-level diff on the raw text of each side. Tokens are whitespace runs, runs of letters, marks and digits, a surrogate pair, or any
/// other single UTF-16 unit, compared exactly as written, so a whitespace or Unicode difference is kept as a difference. Offsets are on the
/// raw text, never on a normalized copy.
/// </summary>
public static class TextDiff
{
    static readonly Regex Token = new(@"\s+|[\p{L}\p{M}\p{N}]+|[\uD800-\uDBFF][\uDC00-\uDFFF]|.", RegexOptions.Compiled | RegexOptions.Singleline);

    readonly record struct Piece(int Start, int End, string Text);

    public static IReadOnlyList<TextHunk> Compare(string template, string document)
    {
        var a = Tokens(template);
        var b = Tokens(document);
        var edits = Myers.Diff(a, b, (x, y) => string.Equals(x.Text, y.Text, StringComparison.Ordinal));
        var hunks = new List<TextHunk>();
        int ai = 0, bi = 0, i = 0;
        while (i < edits.Count)
        {
            if (edits[i].Kind == EditKind.Equal) { ai = edits[i].OldIndex + 1; bi = edits[i].NewIndex + 1; i++; continue; }
            int aFrom = ai, bFrom = bi;
            while (i < edits.Count && edits[i].Kind != EditKind.Equal)
            {
                if (edits[i].Kind == EditKind.Delete) ai = edits[i].OldIndex + 1;
                else bi = edits[i].NewIndex + 1;
                i++;
            }
            var templateSpan = Span(template, a, aFrom, ai);
            var documentSpan = Span(document, b, bFrom, bi);
            var kind = templateSpan.Text.Length == 0 ? "text_inserted" : documentSpan.Text.Length == 0 ? "text_deleted" : "text_modified";
            hunks.Add(new TextHunk(kind, templateSpan, documentSpan));
        }
        return hunks;
    }

    /// <summary>The non-whitespace tokens of a text, the unit used to score how similar two paragraphs are.</summary>
    internal static string[] WordTokens(string text) =>
        Token.Matches(text).Select(m => m.Value).Where(v => !string.IsNullOrWhiteSpace(v)).ToArray();

    static List<Piece> Tokens(string text) =>
        Token.Matches(text).Select(m => new Piece(m.Index, m.Index + m.Length, m.Value)).ToList();

    /// <summary>Span over tokens [from, to); an empty token range is a point at the next token's start (or the text's end).</summary>
    static TextSpan Span(string text, List<Piece> tokens, int from, int to)
    {
        var start = from < tokens.Count ? tokens[from].Start : text.Length;
        var end = to > from ? tokens[to - 1].End : start;
        return new TextSpan(start, end, text[start..end]);
    }
}
