using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Semantics.Binding;

/// <summary>
/// Deterministic UTF-16 boundaries derived from parser-owned source text. It is intentionally
/// vocabulary-free: models may select a boundary, but they cannot invent one.
/// </summary>
public static class SourceTextBoundaryMap
{
    public static IReadOnlyList<int> For(string sourceText)
    {
        ArgumentNullException.ThrowIfNull(sourceText);

        var boundaries = new SortedSet<int> { 0, sourceText.Length };
        for (var index = 0; index < sourceText.Length; index++)
        {
            var current = sourceText[index];
            var previous = index == 0 ? '\0' : sourceText[index - 1];
            if (char.IsWhiteSpace(current) || char.IsPunctuation(current) || char.IsSymbol(current))
            {
                boundaries.Add(index);
                boundaries.Add(index + 1);
            }

            if (index > 0 && char.IsLetterOrDigit(current) != char.IsLetterOrDigit(previous))
                boundaries.Add(index);
        }

        return boundaries.ToArray();
    }

    public static bool Contains(IReadOnlyList<int> boundaries, int offset) =>
        boundaries.Contains(offset);
}
