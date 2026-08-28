using System.Collections.ObjectModel;

namespace DocxHeaderExtractor.Core.Application.Features;

/// <summary>Document-wide facts derived from immutable source facts, before policy stages.</summary>
public sealed record DerivedDocumentFeatures
{
    public double? BodyFontSizePt { get; init; }
    public IReadOnlyDictionary<double, long> FontSizeCharacterWeights { get; init; } =
        new ReadOnlyDictionary<double, long>(new Dictionary<double, long>());
}
