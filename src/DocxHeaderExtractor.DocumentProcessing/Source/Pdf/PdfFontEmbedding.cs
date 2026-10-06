using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Tokens;

namespace DocxHeaderExtractor.DocumentProcessing.Source.Pdf;

/// <summary>Where a document's glyph geometry may come from.</summary>
internal enum PdfGeometryMode
{
    /// <summary>
    /// The parser's own glyph boxes. Safe only when every font that could be drawn is either embedded
    /// in the PDF or one of the fourteen standard fonts: then the boxes come from data the PDF
    /// (or PdfPig itself) carries and are identical on every host.
    /// </summary>
    Parser,

    /// <summary>
    /// <see cref="PdfGlyph"/> geometry built from advances and font size only. Used when any font
    /// would be resolved through the host's installed fonts, whose outlines differ between machines.
    /// </summary>
    FontIndependent,
}

/// <summary>
/// Decides the geometry mode for one PDF from the PDF alone, never from the host. The decision reads
/// the document's font dictionaries: a font with no embedded font program that is not a standard-14
/// font is one PdfPig resolves through the machine's installed fonts.
/// </summary>
internal static class PdfFontEmbedding
{
    private static readonly HashSet<string> Standard14 = new(StringComparer.Ordinal)
    {
        "Courier", "Courier-Bold", "Courier-Oblique", "Courier-BoldOblique",
        "Helvetica", "Helvetica-Bold", "Helvetica-Oblique", "Helvetica-BoldOblique",
        "Times-Roman", "Times-Bold", "Times-Italic", "Times-BoldItalic",
        "Symbol", "ZapfDingbats",
    };

    public static PdfGeometryMode ModeFor(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return HostResolvedFonts(document).Count > 0 ? PdfGeometryMode.FontIndependent : PdfGeometryMode.Parser;
    }

    /// <summary>Base names of every font dictionary whose glyph outlines the host would have to supply.</summary>
    public static IReadOnlyList<string> HostResolvedFonts(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var number in document.Structure.CrossReferenceTable.ObjectOffsets.Keys.OrderBy(key => key.ObjectNumber))
        {
            if (Read(document, number) is not DictionaryToken dictionary || !IsFont(dictionary)) continue;
            // A Type0 font only points at its descendant; the descendant is the font that is judged.
            if (dictionary.TryGet(NameToken.Subtype, out var subtype) && subtype is NameToken { Data: "Type0" }) continue;
            if (IsEmbedded(document, dictionary)) continue;
            var name = BaseFontName(dictionary);
            if (Standard14.Contains(name)) continue;
            names.Add(name);
        }
        return names.ToArray();
    }

    private static bool IsFont(DictionaryToken dictionary) =>
        dictionary.TryGet(NameToken.Type, out var type) && type is NameToken { Data: "Font" };

    private static bool IsEmbedded(PdfDocument document, DictionaryToken font)
    {
        if (!font.TryGet(NameToken.FontDescriptor, out var descriptorToken)) return false;
        return Resolve(document, descriptorToken) is DictionaryToken descriptor &&
               (descriptor.TryGet(NameToken.FontFile, out _) ||
                descriptor.TryGet(NameToken.FontFile2, out _) ||
                descriptor.TryGet(NameToken.FontFile3, out _));
    }

    private static string BaseFontName(DictionaryToken font)
    {
        var name = font.TryGet(NameToken.BaseFont, out var token) && token is NameToken baseFont ? baseFont.Data : "";
        var plus = name.IndexOf('+');
        return plus == 6 ? name[(plus + 1)..] : name;
    }

    private static IToken? Read(PdfDocument document, IndirectReference reference)
    {
        try
        {
            return document.Structure.GetObject(reference).Data;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An unreadable object cannot be a font the parser used.
            return null;
        }
    }

    private static IToken? Resolve(PdfDocument document, IToken token) =>
        token is IndirectReferenceToken reference ? Read(document, reference.Data) : token;
}
