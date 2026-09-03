using System.Collections.ObjectModel;

namespace DocxHeaderExtractor.DocumentProcessing.Features;

/// <summary>Document-wide facts derived from immutable source facts, before policy stages.</summary>
public sealed record DerivedDocumentFeatures
{
    public double? BodyFontSizePt { get; init; }
    public IReadOnlyDictionary<double, long> FontSizeCharacterWeights { get; init; } =
        new ReadOnlyDictionary<double, long>(new Dictionary<double, long>());
    public IReadOnlySet<string> CorruptSourceIds { get; init; } =
        new ReadOnlySet<string>(new HashSet<string>(StringComparer.Ordinal));

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
