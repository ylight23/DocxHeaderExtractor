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
/// Resolves style inheritance once per document. Precedence, first hit wins for each property: the run's direct formatting, its character
/// style and that style's basedOn chain, the paragraph style and its basedOn chain, then the document defaults. Not resolved, and reported as
/// a gap when they apply: table styles (conditional formatting by table region), theme fonts, a missing style and a basedOn cycle. Numbering
/// level formatting applies to the list label, not to the paragraph text, so it is not a gap for run text.
/// </summary>
public sealed class StyleContext
{
    readonly Dictionary<string, Style> byId = new(StringComparer.Ordinal);
    readonly RunPropertiesBaseStyle? defaults;
    readonly bool hasStylesPart;

    public StyleContext(MainDocumentPart main)
    {
        var styles = main.StyleDefinitionsPart?.Styles;
        if (styles is null) return;
        hasStylesPart = true;
        foreach (var style in styles.Elements<Style>())
            if (style.StyleId?.Value is { Length: > 0 } id) byId.TryAdd(id, style);
        defaults = styles.DocDefaults?.RunPropertiesDefault?.RunPropertiesBaseStyle;
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
        if (fonts?.Ascii is null && fonts?.AsciiTheme is not null) gaps.Add("theme_font_not_resolved");

        return new EffectiveFormat(
            Flag(layers, layer => layer.GetFirstChild<Bold>() is { } bold ? bold.Val?.Value ?? true : null),
            Flag(layers, layer => layer.GetFirstChild<Italic>() is { } italic ? italic.Val?.Value ?? true : null),
            fonts?.Ascii?.Value,
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
