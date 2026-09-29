using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// Converts parser-owned DOCX/PDF source units into the shared V5 evidence graph. The adapter
/// carries observations only; it never assigns a semantic function, candidate status or task role.
/// </summary>
public static class V5SourceEvidenceAdapter
{
    public static UniversalEvidenceGraph Build(DocumentSourceCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var observations = catalog.Units.Select(unit =>
        {
            var anchor = unit.SourceAnchor;
            if (anchor.BoundingBox is { } box && (box.Right < box.Left || box.Top < box.Bottom))
                throw new InvalidOperationException($"source-geometry-inverted:{unit.SourceId}");
            EvidenceGeometry? geometry = anchor.Page is null && anchor.BoundingBox is null
                ? null
                : new EvidenceGeometry(
                    anchor.Page,
                    anchor.BoundingBox?.Left,
                    anchor.BoundingBox?.Top,
                    anchor.BoundingBox is { } widthBox ? widthBox.Right - widthBox.Left : null,
                    anchor.BoundingBox is { } heightBox ? heightBox.Top - heightBox.Bottom : null);
            var facts = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["sourceType"] = anchor.SourceType,
                ["paragraphId"] = anchor.ParagraphId,
                ["renderBlockId"] = anchor.RenderBlockId,
            };
            return new SourceObservation(
                $"E{unit.SourceOrdinal:0000}",
                unit.SourceId,
                $"S{unit.SourceOrdinal:0000}",
                unit.SourceOrdinal,
                EvidenceModality.TEXT,
                unit.Text,
                unit.SourceSpan,
                geometry,
                facts);
        });
        return EvidenceGraphBuilder.Build(observations);
    }
}
