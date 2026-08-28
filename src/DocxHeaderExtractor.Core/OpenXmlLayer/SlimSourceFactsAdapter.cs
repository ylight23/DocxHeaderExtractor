using System.Collections.ObjectModel;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.OpenXmlLayer;

/// <summary>
/// One-way compatibility projection from the mixed Slim model to the immutable source contract.
/// Derived, policy, and validated Slim state is intentionally not copied.
/// </summary>
public static class SlimSourceFactsAdapter
{
    public static SourceDocument Adapt(SlimDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        return new SourceDocument
        {
            DocumentId = document.SourcePath,
            FileName = document.FileName,
            SourcePath = document.SourcePath,
            SourceKind = "docx",
            Paragraphs = ReadOnly(document.Paragraphs.Select(MapParagraph)),
            PageHeaders = ReadOnly(document.PageHeaders),
            PageFooters = ReadOnly(document.PageFooters),
        };
    }

    private static SourceParagraph MapParagraph(SlimParagraph paragraph) => new()
    {
        SourceId = string.IsNullOrWhiteSpace(paragraph.StableId)
            ? $"p:{paragraph.Index}"
            : paragraph.StableId,
        SourceOrdinal = paragraph.Index,
        Text = paragraph.Text,
        TextSpans = ReadOnly(paragraph.TextSpans.Select(span => new SourceTextRunSpan(
            span.Start, span.End, span.Bold, span.Italic, span.Underline, span.FontSizePt))),
        LineBreakOffsets = ReadOnly(paragraph.LineBreakOffsets),
        SourceSegments = ReadOnly(paragraph.SourceSegments.Select(segment => new SourceSegment(
            segment.Start, segment.End, segment.RunIndex, segment.RawStart))),
        Style = new SourceStyleFacts
        {
            StyleId = paragraph.StyleId,
            StyleName = paragraph.StyleName,
            BuiltInHeadingStyleLevel = BuiltInHeadingStyleIdentity.LevelFromResolvedStyle(
                paragraph.StyleName, paragraph.StyleId),
            OutlineLevel = paragraph.OutlineLevel,
            Bold = paragraph.Bold,
            Italic = paragraph.Italic,
            Underline = paragraph.Underline,
            AllCaps = paragraph.AllCaps,
            FontSizePt = paragraph.FontSizePt,
            Alignment = paragraph.Alignment,
        },
        Numbering = new SourceNumberingFacts
        {
            NumberingId = paragraph.NumberingId,
            NumberingLevel = paragraph.NumberingLevel,
            NumberLabel = paragraph.NumberLabel,
            NumberingFormat = paragraph.NumberingFormat,
        },
        Layout = new SourceLayoutFacts
        {
            InContentControl = paragraph.InContentControl,
            KeepNext = paragraph.KeepNext,
            PageBreakBefore = paragraph.PageBreakBefore,
            TableDepth = paragraph.TableDepth,
            SectionIndex = paragraph.SectionIndex,
        },
    };

    private static ReadOnlyCollection<T> ReadOnly<T>(IEnumerable<T> values) =>
        Array.AsReadOnly(values.ToArray());

    private static ReadOnlyCollection<T> ReadOnly<T>(IReadOnlyList<T> values) =>
        Array.AsReadOnly(values.ToArray());
}
