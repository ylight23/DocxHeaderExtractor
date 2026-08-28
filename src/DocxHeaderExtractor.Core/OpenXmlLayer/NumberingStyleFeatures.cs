using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.OpenXmlLayer;

public sealed record ParagraphNumberingFeatures(
    string SourceId,
    int? NumberingId,
    int? NumberingLevel,
    string? NumberLabel,
    string? NumberingFormat);

public sealed record ParagraphStyleFeatures(
    string SourceId,
    string? StyleId,
    string? StyleName,
    int? OutlineLevel,
    bool Bold,
    bool Italic,
    bool Underline,
    bool AllCaps,
    double? FontSizePt,
    string? Alignment);

/// <summary>Immutable numbering/style facts projected from the source authority.</summary>
public sealed record NumberingStyleFeatures(
    IReadOnlyList<ParagraphNumberingFeatures> Numbering,
    IReadOnlyList<ParagraphStyleFeatures> Styles)
{
    public static NumberingStyleFeatures FromSourceDocument(SourceDocument source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new NumberingStyleFeatures(
            source.Paragraphs.Select(paragraph => new ParagraphNumberingFeatures(
                paragraph.SourceId,
                paragraph.Numbering.NumberingId,
                paragraph.Numbering.NumberingLevel,
                paragraph.Numbering.NumberLabel,
                paragraph.Numbering.NumberingFormat)).ToArray(),
            source.Paragraphs.Select(paragraph => new ParagraphStyleFeatures(
                paragraph.SourceId,
                paragraph.Style.StyleId,
                paragraph.Style.StyleName,
                paragraph.Style.OutlineLevel,
                paragraph.Style.Bold,
                paragraph.Style.Italic,
                paragraph.Style.Underline,
                paragraph.Style.AllCaps,
                paragraph.Style.FontSizePt,
                paragraph.Style.Alignment)).ToArray());
    }
}
