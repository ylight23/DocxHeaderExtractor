using System.Collections.Frozen;
using System.Collections.ObjectModel;

namespace DocxHeaderExtractor.DocumentProcessing.Features;

/// <summary>Document-wide facts derived from immutable source facts, before policy stages.</summary>
public sealed record DerivedDocumentFeatures
{
    public double? BodyFontSizePt { get; init; }
    public IReadOnlyDictionary<double, long> FontSizeCharacterWeights { get; init; } =
        new ReadOnlyDictionary<double, long>(new Dictionary<double, long>());
    public IReadOnlySet<string> CorruptSourceIds { get; init; } =
        Array.Empty<string>().ToFrozenSet(StringComparer.Ordinal);
}
