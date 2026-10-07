using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Semantics.Canonical;

namespace DocxHeaderExtractor.DocumentProcessing.Projection;

/// <summary>Projects canonical grounding without placing output compatibility logic in semantic contracts.</summary>
public static class CanonicalGroundingProjection
{
    /// <summary>Builds canonical occurrences using the producer's separate projection context.</summary>
    public static IReadOnlyList<CanonicalGrounding> Project(ValidatedStructure structure,
        HeadingProjectionContext? projectionContext = null) =>
        structure.Elements
            .Select(element => (Element: element, Source: element.Sources.FirstOrDefault()))
            .Where(item => item.Source is not null)
            .Select(item =>
            {
                var source = item.Source!;
                var paragraphText = projectionContext?.ForElement(item.Element.Id)?.OriginalText ?? item.Element.Text;
                return new CanonicalGrounding(
                    source.SourceId,
                    source.SourceOrdinal,
                    projectionContext?.StableIdFor(item.Element.Id, source.SourceId),
                    new DocxTextSpan(source.Span.Start, source.Span.End),
                    paragraphText);
            })
            .ToArray();
}
