using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Application.Policy;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Repair;

public sealed record TextLayoutLineProbeReport(
    int TextParagraphs,
    int HardLines,
    int RecoveredLines,
    int LongParagraphs);

/// <summary>
/// Counts the line signal still visible in PDF-converted DOCX text-layout files. Hard lines come
/// from OOXML breaks; recovered lines additionally split places where conversion glued a line end
/// directly to the next capitalized line.
/// </summary>
public static class TextLayoutLineProbe
{
    private static readonly Regex GluedLineBoundaryRx = new(
        @"(?<=[a-z\)\]\.:;,%\d])(?=[A-Z])",
        RegexOptions.Compiled);

    public static TextLayoutLineProbeReport Analyze(DocxPolicyState policyState)
    {
        ArgumentNullException.ThrowIfNull(policyState);
        var textParagraphs = 0;
        var hardLines = 0;
        var recoveredLines = 0;
        var longParagraphs = 0;

        foreach (var paragraph in policyState.Paragraphs)
        {
            if (string.IsNullOrWhiteSpace(paragraph.Text)) continue;
            textParagraphs++;
            if (paragraph.Text.Length >= 500) longParagraphs++;

            var hard = SplitHardLines(paragraph.Source.Text, paragraph.Source.LineBreakOffsets).ToList();
            hardLines += hard.Count;
            recoveredLines += hard.Sum(RecoverLines);
        }

        return new TextLayoutLineProbeReport(textParagraphs, hardLines, recoveredLines, longParagraphs);
    }

    private static IEnumerable<string> SplitHardLines(string text, IReadOnlyList<int> lineBreakOffsets)
    {
        if (lineBreakOffsets.Count == 0)
        {
            yield return text;
            yield break;
        }

        var start = 0;
        foreach (var rawBreak in lineBreakOffsets.Order().Distinct())
        {
            var at = Math.Clamp(rawBreak, 0, text.Length);
            var line = text[start..at].Trim();
            if (line.Length > 0) yield return line;
            start = at;
        }
        var tail = text[start..].Trim();
        if (tail.Length > 0) yield return tail;
    }

    private static int RecoverLines(string hardLine) =>
        GluedLineBoundaryRx
            .Split(hardLine)
            .Count(x => !string.IsNullOrWhiteSpace(x));
}
