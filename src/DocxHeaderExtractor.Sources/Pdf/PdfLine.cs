using MessagePack;

namespace DocxHeaderExtractor.Sources.Pdf;

/// <summary>
/// Dòng PDF dựng lại từ glyph theo toạ độ và khoảng cách (không phải dòng logic OOXML).
/// Typography như <see cref="BoldRatio"/> và <see cref="LeadingBoldPrefix"/> là source facts
/// cho layout/context; parser không quyết định heading membership.
/// </summary>
[MessagePackObject(AllowPrivate = true)]
internal sealed record PdfLine(
    [property: Key(0)] int Page, [property: Key(1)] double Y, [property: Key(2)] double FontSize, [property: Key(3)] string Text,
    [property: Key(4)] double BoldRatio, [property: Key(5)] string LeadingBoldPrefix,
    [property: Key(6)] double ItalicRatio, [property: Key(7)] double Left, [property: Key(8)] double Right,
    [property: Key(9)] string FontName, [property: Key(10)] string FillColorKey,
    [property: Key(11)] string? CanonicalMatchText = null, [property: Key(12)] string? MatchText = null,
    [property: Key(13)] double? Bottom = null, [property: Key(14)] double? Top = null)
{
    [IgnoreMember]
    private readonly PdfSourceTextProjection? _projection;

    /// <summary>
    /// The canonical text of this line and the way back to its glyphs. <see cref="Text"/> stays the
    /// raw parser concatenation for audit; the projection is what the model and the binder use.
    /// <para>
    /// A line built from a string rather than from glyphs has no reconstruction to declare, so it
    /// falls back to itself. That keeps the text intact instead of blanking it, and the empty span
    /// map says plainly that no glyph provenance is available.
    /// </para>
    /// </summary>
    [Key(15)]
    public PdfSourceTextProjection Projection
    {
        get => _projection ?? PdfSourceTextProjection.Identity(Text);
        init => _projection = value;
    }

    /// <summary>
    /// What the glyphs say about type, each fact with its origin, whichever version fed
    /// <see cref="FontSize"/> and <see cref="BoldRatio"/>. Null on a line built from a string.
    /// </summary>
    [Key(16)]
    public PdfLineTypography? Typography { get; init; }
}
