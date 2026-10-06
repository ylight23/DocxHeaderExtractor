using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

internal static class SourceMarkerFactsParser
{
    private static readonly Regex SpacedArabicPathRx = new(
        @"^\s*((?:\d{1,3}\s+){1,4}\d{1,3})(?:[.)\-:]?\s+)(?=\p{L})",
        RegexOptions.Compiled);

    public static SourceMarkerFact? Parse(string text)
    {
        // PDF extraction often turns `4.2.2` into `4 2 2`. Check this repairable source shape
        // before the strict parser mistakes only its first component for a level-one marker.
        var spacedPath = SpacedArabicPathRx.Match(text);
        if (spacedPath.Success)
        {
            var parts = Regex.Matches(spacedPath.Groups[1].Value, @"\d{1,3}")
                .Select(match => int.Parse(match.Value, System.Globalization.CultureInfo.InvariantCulture))
                .ToArray();
            if (parts.Length > 0)
                return new SourceMarkerFact($"Arabic:{parts.Length}", parts.Length, "spaced_arabic", true)
                {
                    Components = [.. parts],
                };
        }

        if (ParseStrict(text) is { } strict)
            return new SourceMarkerFact(strict.Signature, strict.Depth, strict.Kind.ToString().ToLowerInvariant(),
                strict.Kind == NumberMarkerKind.Arabic)
            {
                // Only an arabic path has components. Roman/letter/labelled markers stay empty
                // rather than being flattened into a one-element path they never had.
                Components = strict.Kind == NumberMarkerKind.Arabic && ArabicPath(text) is { } strictPath
                    ? [.. strictPath]
                    : ImmutableArray<int>.Empty,
            };

        var looseLabel = LooseLabelledMarkerParser.ParseCanonical(text);
        if (looseLabel is not null)
        {
            var separator = looseLabel.IndexOf(':');
            var label = separator > 0 ? looseLabel[..separator] : looseLabel;
            return new SourceMarkerFact($"label:{label}", 1, "loose_labelled", false);
        }

        return null;
    }

    // Strict parser for author-typed numbering (1., 3.1., II., a), Điều 5.). The patterns are
    // deliberately NARROW: reading "1: 03/04" as item 1 is worse than missing a marker. The
    // label + number form requires an explicit separator and a remainder that starts with a
    // LETTER, otherwise "Bảng 1.2 Đối chiếu…" would split into label "Bảng" + number 1.

    /// <summary><c>1.</c>, <c>3.1.</c>, <c>2.3.4)</c>, including <c>1.MUC</c> without a space.</summary>
    private static readonly Regex ArabicRx = new(
        @"^\s*(\d{1,2}(?:\.\d{1,2}){0,4})(?!\d)\s*(?:[\.\)\-–:]\s*|\s+)(\S.*)$",
        RegexOptions.Compiled);

    private static readonly Regex RomanRx = new(
        @"^\s*([IVXLCDM]{1,7})\s*[\.\)\-–:]\s*(\S.*)$",
        RegexOptions.Compiled);

    private static readonly Regex LetterRx = new(
        @"^\s*([A-Za-zĂÂĐÊÔƠƯăâđêôơư])\s*[\.\)]\s*(\S.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Merged Latin + Vietnamese letter order (Decree 30/2020 puts đ after d): if x precedes y in
    /// either convention, x precedes y here, so letter values stay monotonic for both.
    /// </summary>
    private const string MergedLetterOrder = "aăâbcdđeêfghijklmnoôơpqrstuưvwxyz";

    // A numbering-like prefix is structural only when a linguistic title follows it. This rejects
    // data lines such as "A: 04, B: 04" or "1: 03/04" without hard-coding field names.
    private static readonly Regex TitleWordRx = new(@"\p{L}{2,}", RegexOptions.Compiled);

    /// <summary>
    /// <c>Chương 1. Tổng quan</c>, <c>PHẦN I. Cơ sở</c>, and the separator-less form
    /// <c>Chương II QUY ĐỊNH CHUNG</c> that PDF-to-DOCX conversion produces when the chapter number and
    /// its title were on two lines. The separator-less branch requires NO lowercase letter in the
    /// remainder (Decree 30/2020 sets part and chapter titles in capitals), which also rejects
    /// mid-sentence cross references (<c>Điều 3 của Bộ luật này</c>) and figure/table captions
    /// (<c>Bảng 3 Thống kê số liệu</c>).
    /// </summary>
    private static readonly Regex LabelledRx = new(
        @"^\s*(\p{Lu}[\p{L}]{1,11})\s+(\d{1,3}|[IVXLCDM]{1,7})(?:\s*[\.\):\-–]\s*|\s+(?=[^\p{Ll}]*$))(\p{L}.*)$",
        RegexOptions.Compiled);

    /// <summary>
    /// Leading numbering symbol of a text. Roman is tried before arabic because <c>I.</c>,
    /// <c>V.</c>, <c>X.</c> also match the single-letter pattern.
    /// </summary>
    internal static NumberMarker? ParseStrict(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        if (RomanRx.Match(text) is { Success: true } roman && HasTitleRemainder(roman)
            && RomanToInt(roman.Groups[1].Value) is { } rv)
            return new NumberMarker(NumberMarkerKind.Roman, 1, rv);

        if (ArabicRx.Match(text) is { Success: true } arabic && HasTitleRemainder(arabic))
        {
            var parts = arabic.Groups[1].Value.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0 && int.TryParse(parts[^1], out var last))
                return new NumberMarker(NumberMarkerKind.Arabic, parts.Length, last);
        }

        if (LetterRx.Match(text) is { Success: true } letter && HasTitleRemainder(letter))
        {
            var at = MergedLetterOrder.IndexOf(char.ToLowerInvariant(letter.Groups[1].Value[0]));
            if (at >= 0)
                return new NumberMarker(NumberMarkerKind.Letter, 1, at + 1);
        }

        // Last: the three patterns above start with the numeral itself, so they cannot collide
        // with "label + number".
        if (LabelledRx.Match(text) is { Success: true } labelled && HasTitleRemainder(labelled, 3))
        {
            var label = labelled.Groups[1].Value.ToLowerInvariant();
            var numeral = labelled.Groups[2].Value;
            if (int.TryParse(numeral, out var labelledValue))
                return new NumberMarker(NumberMarkerKind.Labelled, 1, labelledValue, label);
            if (RomanToInt(numeral) is { } labelledRoman)
                return new NumberMarker(NumberMarkerKind.Labelled, 1, labelledRoman, label);
        }

        return null;
    }

    /// <summary>Full arabic path, e.g. "3.1." → [3, 1].</summary>
    internal static int[]? ArabicPath(string text)
    {
        if (ArabicRx.Match(text) is not { Success: true } match || !HasTitleRemainder(match)) return null;
        var parts = match.Groups[1].Value.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var path = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], out path[i])) return null;
        return path;
    }

    private static bool HasTitleRemainder(Match match) => HasTitleRemainder(match, 2);

    /// <summary>
    /// The title sits in group 2 for the plain numeral patterns and in group 3 for "label + number".
    /// Using group 2 there would test the NUMERAL: <c>II</c> passes <c>TitleWordRx</c> and <c>I</c>
    /// does not, so <c>PHẦN I.</c> and <c>PHẦN II.</c> would get different signatures.
    /// </summary>
    private static bool HasTitleRemainder(Match match, int titleGroup) =>
        match.Groups.Count > titleGroup && TitleWordRx.IsMatch(match.Groups[titleGroup].Value);

    /// <summary>Null when the string is not a canonically spelled roman numeral (e.g. "IIII", "VV").</summary>
    private static int? RomanToInt(string s)
    {
        var map = new Dictionary<char, int>
        {
            ['I'] = 1, ['V'] = 5, ['X'] = 10, ['L'] = 50, ['C'] = 100, ['D'] = 500, ['M'] = 1000,
        };

        var total = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var v = map[s[i]];
            total += i + 1 < s.Length && map[s[i + 1]] > v ? -v : v;
        }

        return total is > 0 and < 40 && ToRoman(total) == s.ToUpperInvariant() ? total : null;
    }

    private static string ToRoman(int n)
    {
        int[] values = [10, 9, 5, 4, 1];
        string[] symbols = ["X", "IX", "V", "IV", "I"];
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < values.Length; i++)
            while (n >= values[i]) { sb.Append(symbols[i]); n -= values[i]; }
        return sb.ToString();
    }
}

public enum NumberMarkerKind
{
    None,
    Arabic,
    Roman,
    Letter,

    /// <summary>"Label + number": <c>Chương 1.</c>, <c>PHẦN I.</c>. The label is read from the document.</summary>
    Labelled,
}

/// <summary>
/// Parsed numbering symbol: <c>3.1.</c> → Arabic, depth 2, value 1. For <see cref="NumberMarkerKind.Labelled"/>
/// the label is part of the signature, so <c>Chương 1.</c> and a bare <c>1.</c> never share one.
/// </summary>
internal readonly record struct NumberMarker(NumberMarkerKind Kind, int Depth, int Value, string Label = "")
{
    public string Signature => Kind == NumberMarkerKind.Labelled
        ? $"Labelled({Label}):{Depth}"
        : $"{Kind}:{Depth}";
}
