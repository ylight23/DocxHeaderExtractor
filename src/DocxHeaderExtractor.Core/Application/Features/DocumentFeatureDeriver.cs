using System.Collections.ObjectModel;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Application.Features;

/// <summary>Pure document-wide feature derivation. It has no heading or selection policy.</summary>
public sealed class DocumentFeatureDeriver : IDocumentFeatureDeriver
{
    public DerivedDocumentFeatures Derive(SourceDocument source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var weights = new Dictionary<double, long>();
        foreach (var paragraph in source.Paragraphs)
        {
            if (paragraph.Style.FontSizePt is not { } size || paragraph.Text.Length == 0) continue;
            weights[size] = weights.GetValueOrDefault(size) + paragraph.Text.Length;
        }

        var frozenWeights = new ReadOnlyDictionary<double, long>(weights);
        var bodySize = weights.Count == 0
            ? (double?)null
            : weights.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key).First().Key;

        return new DerivedDocumentFeatures
        {
            BodyFontSizePt = bodySize,
            FontSizeCharacterWeights = frozenWeights,
        };
    }
}
