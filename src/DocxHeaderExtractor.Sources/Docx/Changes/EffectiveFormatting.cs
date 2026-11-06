using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace DocxHeaderExtractor.Sources.Docx.Changes;

/// <summary>
/// Formatting a run shows after the style layers this reader resolves. A null value means no resolved layer sets it; it never means "off".
/// <see cref="Complete"/> is false when a layer that could change the result was not resolved (<see cref="Gaps"/> names it), so the values
/// are then evidence, not what Word shows. <see cref="FontAscii"/> is the only font slot resolved here; direct formatting keeps all four.
/// </summary>
public sealed record EffectiveFormat(bool? Bold, bool? Italic, string? FontAscii, string? FontSize, string? Color,
    bool Complete, IReadOnlyList<string> Gaps);

/// <summary>
/// Paragraph properties a paragraph gets from its paragraph style and that style's basedOn chain (first hit wins), then the document's default
/// paragraph properties: the list it is numbered in (numId, level), its alignment and its outline level. Direct paragraph properties are not
/// included here. <see cref="Gaps"/> names what could not be read (no styles part, a missing style, a basedOn cycle).
/// </summary>
public sealed record StyleParagraphFormat(string? NumId, int? Ilvl, string? Alignment, int? OutlineLevel, IReadOnlyList<string> Gaps);

/// <summary>
/// Resolves style inheritance once per document. Precedence, first hit wins for each property: the run's direct formatting, its character
/// style and that style's basedOn chain, the paragraph style and its basedOn chain, then the document defaults. Not resolved, and reported as
/// a gap when they apply: table styles (conditional formatting by table region), theme fonts, a missing style and a basedOn cycle. Numbering
/// level formatting applies to the list label, not to the paragraph text, so it is not a gap for run text.
/// </summary>
public sealed class StyleContext
{
    readonly Dictionary<string, Style> byId = new(StringComparer.Ordinal);
    readonly RunPropertiesBaseStyle? defaults;
    readonly ParagraphPropertiesBaseStyle? paragraphDefaults;
    readonly bool hasStylesPart;
    // The Latin typefaces of the theme's major (headings) and minor (body) fonts, which asciiTheme/hAnsiTheme name.
    readonly string? majorLatin;
    readonly string? minorLatin;

    public StyleContext(MainDocumentPart main)
    {
        var fontScheme = main.ThemePart?.Theme?.ThemeElements?.FontScheme;
        majorLatin = fontScheme?.MajorFont?.LatinFont?.Typeface?.Value is { Length: > 0 } major ? major : null;
        minorLatin = fontScheme?.MinorFont?.LatinFont?.Typeface?.Value is { Length: > 0 } minor ? minor : null;
        var styles = main.StyleDefinitionsPart?.Styles;
        if (styles is null) return;
        hasStylesPart = true;
        foreach (var style in styles.Elements<Style>())
            if (style.StyleId?.Value is { Length: > 0 } id) byId.TryAdd(id, style);
        defaults = styles.DocDefaults?.RunPropertiesDefault?.RunPropertiesBaseStyle;
        paragraphDefaults = styles.DocDefaults?.ParagraphPropertiesDefault?.ParagraphPropertiesBaseStyle;
    }

    /// <summary>The paragraph properties a paragraph style gives, along its basedOn chain, then the document defaults.</summary>
    public StyleParagraphFormat ResolveParagraph(string? paragraphStyleId)
    {
        var gaps = new List<string>();
        if (!hasStylesPart && paragraphStyleId is not null) gaps.Add("styles_part_missing");
        var layers = new List<OpenXmlElement>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var current = paragraphStyleId; current is not null;)
        {
            if (!seen.Add(current)) { gaps.Add("style_cycle:" + current); break; }
            if (!byId.TryGetValue(current, out var style)) { if (hasStylesPart) gaps.Add("style_missing:" + current); break; }
            if (style.StyleParagraphProperties is { } properties) layers.Add(properties);
            current = style.BasedOn?.Val?.Value;
        }
        if (paragraphDefaults is not null) layers.Add(paragraphDefaults);
        // Numbering comes as a whole from the nearest layer that sets it. Its level may be left out: the list then names the style at a level.
        var numbering = layers.Select(l => l.GetFirstChild<NumberingProperties>()).FirstOrDefault(n => n?.NumberingId is not null);
        var alignment = layers.Select(l => l.GetFirstChild<Justification>()?.Val?.InnerText).FirstOrDefault(v => v is { Length: > 0 });
        var outline = layers.Select(l => l.GetFirstChild<OutlineLevel>()?.Val?.Value).FirstOrDefault(v => v is not null);
        return new StyleParagraphFormat(numbering?.NumberingId?.Val?.Value.ToString(),
            numbering?.NumberingLevelReference?.Val?.Value, alignment, outline, gaps);
    }

    /// <summary>
    /// What this reader takes from each style, normalized so that rewriting the XML without changing these values keeps it: the style type,
    /// basedOn, the run properties (bold, italic, ASCII font or theme font, size, color) and the paragraph properties (numbering, alignment,
    /// outline level). Keyed by style id; the document defaults are under "(defaults)".
    /// </summary>
    public IReadOnlyDictionary<string, string> Definitions()
    {
        static string Run(OpenXmlElement? r) => r is null ? "" :
            $"b={r.GetFirstChild<Bold>()?.Val?.Value.ToString() ?? (r.GetFirstChild<Bold>() is null ? "" : "True")}|" +
            $"i={r.GetFirstChild<Italic>()?.Val?.Value.ToString() ?? (r.GetFirstChild<Italic>() is null ? "" : "True")}|" +
            $"font={r.GetFirstChild<RunFonts>()?.Ascii?.Value}|theme={r.GetFirstChild<RunFonts>()?.AsciiTheme?.InnerText}|" +
            $"sz={r.GetFirstChild<FontSize>()?.Val?.Value}|color={r.GetFirstChild<Color>()?.Val?.Value}";
        static string Paragraph(OpenXmlElement? p) => p is null ? "" :
            $"num={p.GetFirstChild<NumberingProperties>()?.NumberingId?.Val?.Value}/{p.GetFirstChild<NumberingProperties>()?.NumberingLevelReference?.Val?.Value}|" +
            $"jc={p.GetFirstChild<Justification>()?.Val?.InnerText}|outline={p.GetFirstChild<OutlineLevel>()?.Val?.Value}";
        var result = byId.ToDictionary(kv => kv.Key, kv => $"type={kv.Value.Type?.InnerText}|basedOn={kv.Value.BasedOn?.Val?.Value}|" +
            $"run[{Run(kv.Value.StyleRunProperties)}]|paragraph[{Paragraph(kv.Value.StyleParagraphProperties)}]", StringComparer.Ordinal);
        if (hasStylesPart) result["(defaults)"] = $"run[{Run(defaults)}]|paragraph[{Paragraph(paragraphDefaults)}]";
        // Theme fonts reach every run that names them: a change of the theme's major or minor typeface is a change of definition too.
        result["(theme)"] = $"major={majorLatin}|minor={minorLatin}";
        return result;
    }

    public EffectiveFormat Resolve(Run run, string? paragraphStyleId)
    {
        var gaps = new List<string>();
        var layers = new List<OpenXmlElement>();
        if (run.RunProperties is { } direct) layers.Add(direct);
        layers.AddRange(Chain(run.RunProperties?.RunStyle?.Val?.Value, gaps));
        layers.AddRange(Chain(paragraphStyleId, gaps));
        if (defaults is not null) layers.Add(defaults);

        if (!hasStylesPart) gaps.Add("styles_part_missing");
        if (run.Ancestors<Table>().FirstOrDefault()?.GetFirstChild<TableProperties>()?.TableStyle?.Val?.Value is { Length: > 0 } tableStyle)
            gaps.Add("table_style_not_resolved:" + tableStyle);
        var fonts = layers.Select(layer => layer.GetFirstChild<RunFonts>()).FirstOrDefault(f => f?.Ascii is not null || f?.AsciiTheme is not null);
        // A theme font is resolved from the theme part (its Latin typeface); one that cannot be resolved stays a gap.
        var font = fonts?.Ascii?.Value;
        if (font is null && fonts?.AsciiTheme?.InnerText is { Length: > 0 } theme)
        {
            font = theme is "majorAscii" or "majorHAnsi" ? majorLatin
                : theme is "minorAscii" or "minorHAnsi" ? minorLatin : null;
            if (font is null) gaps.Add("theme_font_not_resolved:" + theme);
        }

        return new EffectiveFormat(
            Flag(layers, layer => layer.GetFirstChild<Bold>() is { } bold ? bold.Val?.Value ?? true : null),
            Flag(layers, layer => layer.GetFirstChild<Italic>() is { } italic ? italic.Val?.Value ?? true : null),
            font,
            First(layers, layer => layer.GetFirstChild<FontSize>()?.Val?.Value),
            First(layers, layer => layer.GetFirstChild<Color>()?.Val?.Value),
            gaps.Count == 0,
            gaps);
    }

    /// <summary>Run-properties layers along a style's basedOn chain, nearest first. A missing style or a cycle ends the chain and is a gap.</summary>
    List<OpenXmlElement> Chain(string? styleId, List<string> gaps)
    {
        var layers = new List<OpenXmlElement>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var current = styleId; current is not null;)
        {
            if (!seen.Add(current)) { gaps.Add("style_cycle:" + current); break; }
            if (!byId.TryGetValue(current, out var style)) { if (hasStylesPart) gaps.Add("style_missing:" + current); break; }
            if (style.StyleRunProperties is { } properties) layers.Add(properties);
            current = style.BasedOn?.Val?.Value;
        }
        return layers;
    }

    static bool? Flag(IEnumerable<OpenXmlElement> layers, Func<OpenXmlElement, bool?> read)
    {
        foreach (var layer in layers)
            if (read(layer) is { } value) return value;
        return null;
    }

    static string? First(IEnumerable<OpenXmlElement> layers, Func<OpenXmlElement, string?> read)
    {
        foreach (var layer in layers)
            if (read(layer) is { Length: > 0 } value) return value;
        return null;
    }
}
