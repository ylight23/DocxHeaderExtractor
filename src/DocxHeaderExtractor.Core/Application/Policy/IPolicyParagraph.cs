using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;

namespace DocxHeaderExtractor.Core.Application.Policy;

/// <summary>Common mutable policy surface implemented by legacy and source-native policy state.</summary>
public interface IPolicyParagraph
{
    int Index { get; }
    string Text { get; }
    string? StyleId { get; }
    string? StyleName { get; }
    int? OutlineLevel { get; }
    bool Bold { get; }
    bool Underline { get; }
    bool AllCaps { get; }
    double? FontSizePt { get; }
    double? BodyFontSizePt { get; set; }
    string? Alignment { get; }
    bool InContentControl { get; }
    bool Corrupt { get; set; }
    TableRole TableRole { get; set; }
    int TableDepth { get; }
    int? NumberingId { get; }
    int? NumberingLevel { get; }
    int? NumberingDepth { get; }
    string? NumberingFormat { get; }
    string? NumberLabel { get; }
    int? NumberingStyleLevel { get; set; }
    bool InTableOfContents { get; set; }
    bool PrecedesTable { get; set; }
    bool KeepNext { get; }
    bool PageBreakBefore { get; }
    bool HasBuiltInHeadingStyle { get; set; }
    ParagraphRole Role { get; set; }
    int? GuessedLevel { get; set; }
    double Score { get; set; }
    bool IsCandidate { get; }
}
