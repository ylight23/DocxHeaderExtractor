using System.Collections.ObjectModel;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Application.Features;

/// <summary>Pure document-wide feature derivation. It has no heading or selection policy.</summary>
public sealed class DocumentFeatureDeriver : IDocumentFeatureDeriver
{
    private const int CorruptMinimumLength = 12;
    private const double CorruptThreshold = 0.55;

    public DerivedDocumentFeatures Derive(SourceDocument source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var weights = new Dictionary<double, long>();
        var corruptSourceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var paragraph in source.Paragraphs)
        {
            if (paragraph.Style.FontSizePt is not { } size || paragraph.Text.Length == 0) continue;
            weights[size] = weights.GetValueOrDefault(size) + paragraph.Text.Length;
            if (IsDoubled(paragraph.Text)) corruptSourceIds.Add(paragraph.SourceId);
        }

        foreach (var paragraph in source.Paragraphs.Where(p => p.Style.FontSizePt is null || p.Text.Length == 0))
            if (IsDoubled(paragraph.Text)) corruptSourceIds.Add(paragraph.SourceId);

        var frozenWeights = new ReadOnlyDictionary<double, long>(weights);
        var bodySize = weights.Count == 0
            ? (double?)null
            : weights.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key).First().Key;

        return new DerivedDocumentFeatures
        {
            BodyFontSizePt = bodySize,
            FontSizeCharacterWeights = frozenWeights,
            CorruptSourceIds = new ReadOnlySet<string>(corruptSourceIds),
        };
    }

    private static bool IsDoubled(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var characters = text.Where(char.IsLetterOrDigit).ToArray();
        if (characters.Length < CorruptMinimumLength) return false;

        var pairs = characters.Length / 2;
        var same = 0;
        for (var i = 0; i + 1 < characters.Length; i += 2)
            if (char.ToLowerInvariant(characters[i]) == char.ToLowerInvariant(characters[i + 1])) same++;

        return (double)same / pairs >= CorruptThreshold;
    }

    private sealed class ReadOnlySet<T>(ISet<T> source) : IReadOnlySet<T>
    {
        public int Count => source.Count;
        public bool Contains(T item) => source.Contains(item);
        public IEnumerator<T> GetEnumerator() => source.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        public bool IsProperSubsetOf(IEnumerable<T> other) => source.IsProperSubsetOf(other);
        public bool IsProperSupersetOf(IEnumerable<T> other) => source.IsProperSupersetOf(other);
        public bool IsSubsetOf(IEnumerable<T> other) => source.IsSubsetOf(other);
        public bool IsSupersetOf(IEnumerable<T> other) => source.IsSupersetOf(other);
        public bool Overlaps(IEnumerable<T> other) => source.Overlaps(other);
        public bool SetEquals(IEnumerable<T> other) => source.SetEquals(other);
    }
}
