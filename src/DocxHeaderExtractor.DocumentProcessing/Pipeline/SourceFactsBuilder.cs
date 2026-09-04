using System.Text.RegularExpressions;
using DocxHeaderExtractor.DocumentProcessing.Policy;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>Builds immutable parser facts before any LLM/VLM call.</summary>
public static class SourceFactsBuilder
{
    private static readonly Regex Decimal = new(
        @"^\s*(?<value>\d{1,3}(?:\.\d{1,3}){0,4})\s*[\.)](?=\s*\S)",
        RegexOptions.Compiled);
    private static readonly Regex Roman = new(
        @"^\s*(?<value>[IVXLCDM]{1,8})\s*[\.)](?=\s*\S)", RegexOptions.Compiled);
    private static readonly Regex Alpha = new(
        @"^\s*(?<value>\p{L})\s*[\.)](?=\s*\S)", RegexOptions.Compiled);

    public static SourceFacts FromParagraph(IPolicyParagraph paragraph)
    {
        var observed = new List<ObservedEvidence>();
        if (paragraph.HasBuiltInHeadingStyle)
            observed.Add(new(ObservedEvidenceKind.BuiltInHeadingStyle, paragraph.StyleId ?? "Heading", EvidenceOrigin.DocxParser));
        if (paragraph.OutlineLevel is { } outline)
            observed.Add(new(ObservedEvidenceKind.OutlineLevel, outline.ToString(), EvidenceOrigin.DocxParser));
        if (paragraph.Bold)
            observed.Add(new(ObservedEvidenceKind.FontWeight, "bold", EvidenceOrigin.DocxParser));
        if (paragraph.FontSizePt is { } size)
            observed.Add(new(ObservedEvidenceKind.FontSize, size.ToString("R", System.Globalization.CultureInfo.InvariantCulture), EvidenceOrigin.DocxParser));
        if (!string.IsNullOrWhiteSpace(paragraph.Alignment))
            observed.Add(new(ObservedEvidenceKind.Alignment, paragraph.Alignment, EvidenceOrigin.DocxParser));
        if (paragraph.TableDepth > 0)
            observed.Add(new(ObservedEvidenceKind.TableMembership, paragraph.TableRole.ToString(), EvidenceOrigin.DocxParser));
        if (paragraph.InContentControl)
            observed.Add(new(ObservedEvidenceKind.ContentControl, "true", EvidenceOrigin.DocxParser));
        if (paragraph.PageBreakBefore)
            observed.Add(new(ObservedEvidenceKind.PageBreakBefore, "true", EvidenceOrigin.DocxParser));
        if (paragraph.KeepNext)
            observed.Add(new(ObservedEvidenceKind.KeepNext, "true", EvidenceOrigin.DocxParser));
        var lineBreakOffsets = (paragraph as DocxPolicyParagraph)?.Source.LineBreakOffsets ?? [];
        if (lineBreakOffsets.Count > 0)
            observed.Add(new(ObservedEvidenceKind.LineBreak, string.Join(',', lineBreakOffsets), EvidenceOrigin.DocxParser));

        var marker = ParseMarker(paragraph);
        if (marker is not null)
            observed.Add(new(ObservedEvidenceKind.NumberingMarker, marker.Raw, EvidenceOrigin.MarkerParser));

        return new SourceFacts
        {
            SourceId = string.IsNullOrWhiteSpace(paragraph.StableId) ? $"p:{paragraph.Index}" : paragraph.StableId,
            RawText = paragraph.Text,
            RawSpan = new SourceTextSpan(0, paragraph.Text.Length),
            Source = new SourceAnchor
            {
                SourceType = "docx",
                ParagraphId = paragraph.StableId,
                ParagraphIndex = paragraph.Index,
                SourceSegments = paragraph is DocxPolicyParagraph native ? native.Source.SourceSegments : [],
            },
            Marker = marker,
            ObservedEvidence = observed,
            ParserBoundaries = SourceTextBoundaryMap.For(paragraph.Text),
        };
    }

    public static MarkerFacts? ParseMarker(IPolicyParagraph paragraph)
    {
        if (paragraph.NumberingId is { } numId && paragraph.NumberingLevel is { } ilvl)
            return new MarkerFacts
            {
                Kind = MarkerKind.DocxNumbering,
                Raw = paragraph.NumberLabel ?? "",
                Normalized = paragraph.NumberLabel?.TrimEnd('.', ')'),
                Depth = paragraph.NumberingDepth ?? ilvl + 1,
                NumId = numId,
                Ilvl = ilvl,
            };

        return ParseMarkerText(paragraph.Text);
    }

    internal static SourceFacts FromPdfBlock(PdfSemanticBlock block)
    {
        var observed = new List<ObservedEvidence>
        {
            new(ObservedEvidenceKind.FontSize, block.PrimaryStyle.FontSizeBucket.ToString(), EvidenceOrigin.PdfParser),
            new(ObservedEvidenceKind.FontWeight, block.Lines.Count > 0 && block.Lines.Average(line => line.BoldRatio) >= 0.5 ? "bold" : "normal", EvidenceOrigin.PdfParser),
        };
        var marker = ParseMarkerText(block.Text);
        if (marker is not null)
            observed.Add(new(ObservedEvidenceKind.NumberingMarker, marker.Raw, EvidenceOrigin.MarkerParser));
        return new SourceFacts
        {
            SourceId = block.Id,
            RawText = block.Text,
            RawSpan = new SourceTextSpan(0, block.Text.Length),
            Source = new SourceAnchor
            {
                SourceType = "pdf",
                Page = block.Page,
                RenderBlockId = block.Id,
                RenderLineIds = block.Lines.Select((_, index) => $"{block.Id}:l{index + 1}").ToArray(),
                BoundingBox = new PdfBoundingBox(block.Left, block.BottomY, block.Right, block.TopY),
            },
            Marker = marker,
            ObservedEvidence = observed,
            ParserBoundaries = SourceTextBoundaryMap.For(block.Text),
        };
    }

    private static MarkerFacts? ParseMarkerText(string text)
    {
        var decimalMatch = Decimal.Match(text);
        if (decimalMatch.Success)
        {
            var value = decimalMatch.Groups["value"].Value;
            var components = value.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
            return new MarkerFacts
            {
                Kind = components.Length == 1 ? MarkerKind.Decimal : MarkerKind.DecimalDotted,
                Raw = decimalMatch.Value.Trim(), Normalized = string.Join('.', components), Depth = components.Length, Components = components,
            };
        }

        var roman = Roman.Match(text);
        if (roman.Success)
            return new MarkerFacts { Kind = MarkerKind.RomanUpper, Raw = roman.Value.Trim(), Normalized = roman.Groups["value"].Value, Depth = 1 };

        var alpha = Alpha.Match(text);
        if (alpha.Success)
        {
            var value = alpha.Groups["value"].Value;
            return new MarkerFacts { Kind = char.IsLower(value[0]) ? MarkerKind.AlphaLower : MarkerKind.AlphaUpper, Raw = alpha.Value.Trim(), Normalized = value, Depth = 1 };
        }
        return null;
    }
}
