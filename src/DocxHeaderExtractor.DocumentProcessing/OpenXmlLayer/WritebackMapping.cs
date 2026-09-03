using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;

namespace DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;

/// <summary>Stable source occurrence identity used by writeback; text is never the identity.</summary>
public sealed record SourceIdentity(string DocumentId, string SourceId);

/// <summary>Immutable source-to-OOXML location data captured while the source is authoritative.</summary>
public sealed record WritebackLocator(
    int ParagraphIndex,
    string SourceText,
    IReadOnlyList<SourceSegment> SourceSegments);

/// <summary>Minimal writeback mapping. It intentionally contains no policy or demotion state.</summary>
public sealed record WritebackMapping(SourceIdentity Identity, WritebackLocator Locator);

public static class WritebackMappingSet
{
    public static IReadOnlyDictionary<string, WritebackMapping> FromSourceDocument(SourceDocument source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.Paragraphs
            .GroupBy(paragraph => paragraph.SourceId, StringComparer.Ordinal)
            .Select(group => group.Single())
            .ToDictionary(
                paragraph => paragraph.SourceId,
                paragraph => new WritebackMapping(
                    new SourceIdentity(source.DocumentId, paragraph.SourceId),
                    new WritebackLocator(paragraph.SourceOrdinal, paragraph.Text, paragraph.SourceSegments)),
                StringComparer.Ordinal);
    }
}
