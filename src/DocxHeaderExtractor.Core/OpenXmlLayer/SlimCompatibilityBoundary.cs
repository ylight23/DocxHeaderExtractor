using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Core.OpenXmlLayer;

/// <summary>
/// Narrow transitional view of mutable Slim policy state. Source facts are intentionally absent;
/// normal authority code must read those from SourceDocument.
/// </summary>
internal sealed class SlimCompatibilityContext
{
    public SlimCompatibilityContext(
        IReadOnlyDictionary<string, SlimCompatibilityParagraph> paragraphs,
        SlimDocument legacyDocument)
    {
        Paragraphs = paragraphs;
        _legacyDocument = legacyDocument;
    }

    private readonly SlimDocument _legacyDocument;

    public IReadOnlyDictionary<string, SlimCompatibilityParagraph> Paragraphs { get; }

    internal SlimDocument ForLegacyCompatibility() => _legacyDocument;

    public bool TryGet(string sourceId, out SlimCompatibilityParagraph paragraph) =>
        Paragraphs.TryGetValue(sourceId, out paragraph!);

    public PdfMarkerFact? MarkerFor(string sourceId, string sourceText)
    {
        if (!Paragraphs.TryGetValue(sourceId, out var paragraph)) return null;
        return paragraph.MarkerFor(sourceText);
    }
}

internal sealed record SlimCompatibilityParagraph(
    int Index,
    ParagraphRole Role,
    bool InTableOfContents,
    bool HasBuiltInHeadingStyle,
    int? NumberingId,
    int? NumberingStyleLevel,
    SlimParagraph Original)
{
    public PdfMarkerFact? MarkerFor(string sourceText) =>
        NumberingAudit.ParseParagraph(Original, sourceText) is { } strict
            ? new PdfMarkerFact(strict.Signature, strict.Depth, strict.Kind.ToString().ToLowerInvariant(), strict.Kind == NumberKind.Arabic)
            : PdfMarkerFactsParser.Parse(sourceText);
}

internal static class SlimCompatibilityBoundary
{
    public static SlimCompatibilityContext Capture(SlimDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var paragraphs = document.Paragraphs.ToDictionary(
            SourceId,
            paragraph => new SlimCompatibilityParagraph(
                paragraph.Index,
                paragraph.Role,
                paragraph.InTableOfContents,
                paragraph.HasBuiltInHeadingStyle,
                paragraph.NumberingId,
                paragraph.NumberingStyleLevel,
                paragraph),
            StringComparer.Ordinal);
        return new SlimCompatibilityContext(paragraphs, document);
    }

    private static string SourceId(SlimParagraph paragraph) =>
        string.IsNullOrWhiteSpace(paragraph.StableId) ? $"p{paragraph.Index}" : paragraph.StableId;
}
