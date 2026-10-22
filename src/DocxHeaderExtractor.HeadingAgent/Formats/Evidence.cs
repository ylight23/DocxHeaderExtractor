using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.HeadingAgent.Protocol;
using DocxHeaderExtractor.HeadingAgent.Runtime;
using DocxHeaderExtractor.HeadingAgent.Tools;

namespace DocxHeaderExtractor.HeadingAgent.Formats;

/// <summary>The parser observations the tools and facts read (from the qualified P7F1QEvidenceTools): page statistics, the spatial relation of
/// two lines, compact layout, font size, bold ratio, edges, and the repeat normalization. Observations only; no label.</summary>
internal static class Evidence
{
    public const string RepeatNormalization = "LOWER_INVARIANT_DOT_LEADERS_REMOVED_TRAILING_PAGE_NUMBER_REMOVED_DIGITS_MASKED_WHITESPACE_COLLAPSED_V1";
    public const string LayoutBasis = "bbox=[left,right,bottom,top] in PDF points, bottom-left origin; font={size,name,boldRatio,italicRatio}; parser observations, not labels";
    public const double SameBaselineTolerance = 2.0;

    public static object PageStats(IReadOnlyList<AgentSourceLine> lines, int page)
    {
        var onPage = lines.Where(l => l.Page == page).ToArray();
        var boxes = onPage.Select(l => Box(l.Bbox)).Where(b => b is not null).Select(b => b!.Value).ToArray();
        var sizes = onPage.Select(l => FontSizeOf(l.Typography)).Where(s => s is not null).Select(s => s!.Value).Order().ToArray();
        return new
        {
            page, lineCount = onPage.Length,
            textExtent = boxes.Length == 0 ? null : new[] { R(boxes.Min(b => b.Left)), R(boxes.Max(b => b.Right)), R(boxes.Min(b => b.Bottom)), R(boxes.Max(b => b.Top)) },
            medianFontSize = sizes.Length == 0 ? (double?)null : R(sizes[sizes.Length / 2]),
        };
    }

    public static object RelationOf(AgentSourceLine target, AgentSourceLine other)
    {
        var a = Box(target.Bbox); var b = Box(other.Bbox);
        if (a is null || b is null || target.Page != other.Page) return new { samePage = target.Page == other.Page };
        double overlap = Math.Max(0, Math.Min(a.Value.Right, b.Value.Right) - Math.Max(a.Value.Left, b.Value.Left));
        double narrower = Math.Max(1e-6, Math.Min(a.Value.Right - a.Value.Left, b.Value.Right - b.Value.Left));
        return new
        {
            samePage = true,
            sameBaseline = Math.Abs(a.Value.Bottom - b.Value.Bottom) <= SameBaselineTolerance,
            verticalOffset = R(b.Value.Bottom - a.Value.Bottom),
            horizontalOverlapRatio = Math.Round(overlap / narrower, 2, MidpointRounding.AwayFromZero),
            fontSizeDelta = FontSizeOf(other.Typography) is { } so && FontSizeOf(target.Typography) is { } st ? R(so - st) : (double?)null,
        };
    }

    /// <summary>The compact layout of one line: rounded box and font facts, or null when the source has neither.</summary>
    public static object? CompactLayout(JsonElement? bbox, JsonElement? typography)
    {
        var b = Box(bbox);
        var f = Font(typography);
        if (b is null && f is null) return null;
        return new { bbox = b is null ? null : new { left = R(b.Value.Left), right = R(b.Value.Right), bottom = R(b.Value.Bottom), top = R(b.Value.Top) }, font = f };
    }

    public static double? FontSizeOf(JsonElement? t) =>
        t is { ValueKind: JsonValueKind.Object } v && v.TryGetProperty("fontSize", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : null;
    public static double? Bold(JsonElement? t) =>
        t is { ValueKind: JsonValueKind.Object } v && v.TryGetProperty("boldRatio", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : null;
    public static double? LeftEdgeOf(JsonElement? bbox) => Box(bbox)?.Left;
    public static double? RightEdgeOf(JsonElement? bbox) => Box(bbox)?.Right;

    public static string RepeatKey(string text)
    {
        var s = text.ToLowerInvariant();
        s = Regex.Replace(s, @"(?:\s*[.·…]){2,}", " ");
        s = Regex.Replace(s, @"\s+", " ").Trim();
        s = Regex.Replace(s, @"\s+\d+$", "");
        s = Regex.Replace(s, @"\d", "#");
        return s.Trim();
    }

    /// <summary>The search normalization: diacritics and whitespace removed, đ as d, lower case.</summary>
    public static string Normalize(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            sb.Append(c switch { 'đ' => 'd', 'Đ' => 'D', _ => c });
        }
        return new string(sb.ToString().ToLowerInvariant().Where(c => !char.IsWhiteSpace(c)).ToArray());
    }

    static double R(double v) => Math.Round(v, 1, MidpointRounding.AwayFromZero);

    static (double Left, double Right, double Bottom, double Top)? Box(JsonElement? box) =>
        box is { ValueKind: JsonValueKind.Object } b
            ? (b.GetProperty("left").GetDouble(), b.GetProperty("right").GetDouble(), b.GetProperty("bottom").GetDouble(), b.GetProperty("top").GetDouble())
            : null;

    static object? Font(JsonElement? t)
    {
        if (t is not { ValueKind: JsonValueKind.Object } v) return null;
        double? N(string k) => v.TryGetProperty(k, out var x) && x.ValueKind == JsonValueKind.Number ? R(x.GetDouble()) : null;
        return new
        {
            size = N("fontSize"), name = v.TryGetProperty("fontName", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null,
            boldRatio = N("boldRatio"), italicRatio = N("italicRatio"),
        };
    }
}
