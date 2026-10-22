using System.Security.Cryptography;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Dw = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;
using DocxHeaderExtractor.Sources.Snapshot;

namespace DocxHeaderExtractor.Sources.Docx;

/// <summary>What a DOCX carries its content as.</summary>
public enum DocxSourceKind
{
    /// <summary>Text the OOXML holds as text; no image covers a page.</summary>
    NativeText,
    /// <summary>Pages are images (a scan placed in a document), and the OOXML holds next to no text: the content is not readable as text.</summary>
    ScannedImageOnly,
    /// <summary>Native text, and some images that cover a page (a cover, a chart, or a scanned page among text pages).</summary>
    Mixed,
}

/// <summary>One image of the body in document order: the part it comes from and how much of the page it covers.</summary>
/// <param name="Ordinal">Its place among the body's images, from 0.</param>
/// <param name="RelationshipId">The r:embed of its blip.</param>
/// <param name="CoversPage">Whether its extent covers at least <see cref="DocxSourceIntegrity.PageCoverage"/> of the page's width and height.</param>
public sealed record DocxBodyImage(int Ordinal, string RelationshipId, string? ContentType, long WidthEmu, long HeightEmu, bool CoversPage);

/// <summary>
/// The measured integrity of a DOCX: how much text the OOXML holds, how many images the body places and how many of them cover a page, and
/// the kind that follows. A measurement of the package, not of what the text means: no word is read. It says whether a reader of the OOXML can
/// see the content at all, so that a scanned document is reported as such instead of being read as its one signature paragraph.
/// </summary>
public sealed record DocxSourceIntegrity(
    string SourceSha256,
    DocxSourceKind Kind,
    int Paragraphs,
    int ParagraphsWithText,
    long TextChars,
    int Images,
    int PageImages,
    int LegacyPictures,
    IReadOnlyList<DocxBodyImage> BodyImages)
{
    public const string Version = "DOCX_SOURCE_INTEGRITY_V1";

    /// <summary>An image covers the page when its extent is at least this share of the page's width and of its height.</summary>
    public const double PageCoverage = 0.6;

    /// <summary>A document whose page images carry fewer than this many characters of OOXML text each is image only. Measured on the corpus
    /// (todo10_8/generated-docx, 74 files): the six scans carry 0 to 4 characters per page image (a signature block), the native documents
    /// with page images carry 60,000 and more.</summary>
    public const int TextCharsPerPageImage = 200;

    public static DocxSourceIntegrity Read(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        using var stream = new MemoryStream(bytes, writable: false);
        using var doc = WordprocessingDocument.Open(stream, false);
        var main = doc.MainDocumentPart ?? throw new InvalidOperationException("DOCX_WITHOUT_MAIN_PART");
        var body = main.Document?.Body;
        if (body is null) return new(sha, DocxSourceKind.NativeText, 0, 0, 0, 0, 0, 0, []);

        var (pageW, pageH) = PageSize(body);
        int paragraphs = 0, withText = 0;
        long chars = 0;
        foreach (var p in body.Descendants<Paragraph>())
        {
            paragraphs++;
            var n = p.Descendants<Text>().Sum(t => (long)t.Text.Length);
            if (n > 0) withText++;
            chars += n;
        }

        var images = new List<DocxBodyImage>();
        foreach (var drawing in body.Descendants<Drawing>())
        {
            var blip = drawing.Descendants<A.Blip>().FirstOrDefault();
            var rid = blip?.Embed?.Value;
            if (rid is null) continue;
            var extent = drawing.Descendants<Dw.Extent>().FirstOrDefault();
            long cx = extent?.Cx?.Value ?? 0, cy = extent?.Cy?.Value ?? 0;
            string? type = null;
            try { type = main.GetPartById(rid).ContentType; } catch (ArgumentOutOfRangeException) { }
            images.Add(new DocxBodyImage(images.Count, rid, type, cx, cy, cx >= PageCoverage * pageW && cy >= PageCoverage * pageH));
        }
        var legacy = body.Descendants<Picture>().Count();
        var pageImages = images.Count(i => i.CoversPage);
        var kind = pageImages == 0 ? DocxSourceKind.NativeText
            : chars < (long)TextCharsPerPageImage * pageImages ? DocxSourceKind.ScannedImageOnly
            : DocxSourceKind.Mixed;
        return new(sha, kind, paragraphs, withText, chars, images.Count, pageImages, legacy, images);
    }

    /// <summary>The largest page size the sections declare, in EMU (a twip is 635 EMU); US Letter when none does.</summary>
    static (double W, double H) PageSize(Body body)
    {
        var sizes = body.Descendants<PageSize>().Select(s => ((double)(s.Width?.Value ?? 0) * 635, (double)(s.Height?.Value ?? 0) * 635))
            .Where(s => s.Item1 > 0 && s.Item2 > 0).ToArray();
        return sizes.Length == 0 ? (7772400, 10058400) : (sizes.Max(s => s.Item1), sizes.Max(s => s.Item2));
    }
}
