using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace DocxHeaderExtractor.Core.OpenXmlLayer;

/// <summary>
/// Thuộc tính đã "làm phẳng" của một style sau khi đi hết chuỗi w:basedOn.
/// </summary>
public sealed record ResolvedStyle(
    string StyleId,
    string? Name,
    int? OutlineLevel,
    bool Bold,
    bool Italic,
    bool Underline,
    bool AllCaps,
    double? FontSizePt,
    string? Alignment,
    bool KeepNext,
    bool PageBreakBefore,
    int? NumberingId,
    int? NumberingLevel);

/// <summary>
/// Đọc styles.xml, giải quyết kế thừa basedOn và docDefaults, có cache theo styleId.
/// </summary>
public sealed class StyleResolver
{
    private readonly Dictionary<string, Style> _styles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ResolvedStyle> _cache = new(StringComparer.OrdinalIgnoreCase);

    public double? DefaultFontSizePt { get; }
    public string? DefaultParagraphStyleId { get; }

    public StyleResolver(MainDocumentPart mainPart)
    {
        var styles = mainPart.StyleDefinitionsPart?.Styles;
        if (styles is null) return;

        foreach (var s in styles.Elements<Style>())
        {
            var id = s.StyleId?.Value;
            if (!string.IsNullOrEmpty(id)) _styles[id] = s;

            if (s.Default?.Value == true &&
                string.Equals(s.Type?.InnerText, "paragraph", StringComparison.OrdinalIgnoreCase))
            {
                DefaultParagraphStyleId ??= id;
            }
        }

        var defRun = styles.DocDefaults?.RunPropertiesDefault?.RunPropertiesBaseStyle;
        DefaultFontSizePt = HalfPointToPt(defRun?.FontSize?.Val?.Value);
    }

    public ResolvedStyle? Resolve(string? styleId)
    {
        if (string.IsNullOrEmpty(styleId)) return null;
        if (_cache.TryGetValue(styleId, out var cached)) return cached;

        // Đi ngược chuỗi basedOn: gốc trước, con sau (con ghi đè cha).
        var chain = new List<Style>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = styleId;
        while (!string.IsNullOrEmpty(current) && seen.Add(current) && _styles.TryGetValue(current, out var st))
        {
            chain.Add(st);
            current = st.BasedOn?.Val?.Value;
        }
        chain.Reverse();

        if (chain.Count == 0)
        {
            var empty = new ResolvedStyle(styleId, null, null, false, false, false, false,
                DefaultFontSizePt, null, false, false, null, null);
            return _cache[styleId] = empty;
        }

        string? name = null;
        int? outline = null;
        bool bold = false, italic = false, underline = false, caps = false;
        double? size = DefaultFontSizePt;
        string? align = null;
        bool keepNext = false, pageBreak = false;
        int? numId = null, numLvl = null;

        foreach (var st in chain)
        {
            var pPr = st.StyleParagraphProperties;
            var rPr = st.StyleRunProperties;

            if (pPr?.OutlineLevel?.Val is { } ol) outline = ol.Value;
            if (pPr?.Justification?.Val is { } j) align = j.InnerText;
            if (OnOff(pPr?.KeepNext) is { } kn) keepNext = kn;
            if (OnOff(pPr?.PageBreakBefore) is { } pb) pageBreak = pb;
            if (pPr?.NumberingProperties?.NumberingId?.Val is { } nid) numId = nid.Value;
            if (pPr?.NumberingProperties?.NumberingLevelReference?.Val is { } nlv) numLvl = nlv.Value;

            if (OnOff(rPr?.Bold) is { } b) bold = b;
            if (OnOff(rPr?.Italic) is { } i) italic = i;
            if (OnOff(rPr?.Caps) is { } c) caps = c;
            if (rPr?.Underline?.Val is { } u) underline = !string.Equals(u.InnerText, "none", StringComparison.OrdinalIgnoreCase);
            if (HalfPointToPt(rPr?.FontSize?.Val?.Value) is { } fs) size = fs;
        }

        // Tên hiển thị lấy từ chính style được hỏi (phần tử cuối của chain sau khi Reverse).
        name = chain[^1].StyleName?.Val?.Value;

        var resolved = new ResolvedStyle(styleId, name, outline, bold, italic, underline, caps,
            size, align, keepNext, pageBreak, numId, numLvl);
        return _cache[styleId] = resolved;
    }

    public static bool? OnOff(OnOffType? element)
    {
        if (element is null) return null;
        // Trong OOXML, phần tử có mặt mà không có @w:val nghĩa là true.
        return element.Val?.Value ?? true;
    }

    public static double? HalfPointToPt(string? halfPoints)
    {
        if (string.IsNullOrWhiteSpace(halfPoints)) return null;
        return double.TryParse(halfPoints, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? v / 2.0
            : null;
    }
}
