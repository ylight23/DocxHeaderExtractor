using System.Collections.Frozen;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;

namespace DocxHeaderExtractor.DocumentProcessing.Features;

/// <summary>Derives TOC adjacency only; TOC recognition remains owned by the existing source stage.</summary>
public sealed class TocStructuralFeatureDeriver : ITocStructuralFeatureDeriver
{
    public TocStructuralFeatures Derive(
        SourceDocument source,
        IReadOnlySet<string> tocEntrySourceIds)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(tocEntrySourceIds);

        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < source.Paragraphs.Count; index++)
        {
            var paragraph = source.Paragraphs[index];
            if (string.IsNullOrWhiteSpace(paragraph.Text) || tocEntrySourceIds.Contains(paragraph.SourceId)) continue;

            var next = NextNonEmpty(source.Paragraphs, index);
            if (next is not null && tocEntrySourceIds.Contains(next.SourceId))
                ids.Add(paragraph.SourceId);
        }

        return new TocStructuralFeatures(ids.ToFrozenSet(StringComparer.Ordinal));
    }

    private static SourceParagraph? NextNonEmpty(
        IReadOnlyList<SourceParagraph> paragraphs,
        int index)
    {
        for (var next = index + 1; next < paragraphs.Count; next++)
            if (!string.IsNullOrWhiteSpace(paragraphs[next].Text)) return paragraphs[next];
        return null;
    }
}
