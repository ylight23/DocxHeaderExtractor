using System.Text;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Outline;
using UglyToad.PdfPig.Tokens;

namespace DocxHeaderExtractor.Core.Pipeline;

internal sealed record PdfBookmarkOutlineResult(
    bool Accepted,
    IReadOnlyList<HeadingRecord> Headings,
    string Reason,
    int BookmarkCount,
    int PageAnchored,
    int DocxAligned)
{
    public static PdfBookmarkOutlineResult NotApplicable(string reason) => new(false, [], reason, 0, 0, 0);
}

/// <summary>
/// Reads the PDF's own outline tree. Bookmark titles and levels are author-declared; DOCX is used
/// solely to ground each title to a stable paragraph/span for writeback.
/// </summary>
internal static class PdfBookmarkOutline
{
    public const string Basis = "pdf_bookmarks";

    public static PdfBookmarkOutlineResult TryBuild(string originalInputPath, SlimDocument slim)
    {
        var pdf = PdfTextbookOutline.FindSiblingPdf(originalInputPath);
        if (pdf is null) return PdfBookmarkOutlineResult.NotApplicable("no-pdf");

        List<PdfBookmarkEntry> entries;
        int pages;
        try
        {
            using var document = PdfDocument.Open(pdf);
            pages = document.NumberOfPages;
            if (!document.TryGetBookmarks(out var bookmarks))
                return PdfBookmarkOutlineResult.NotApplicable("no-pdf-bookmarks");
            entries = Flatten(bookmarks.Roots).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return PdfBookmarkOutlineResult.NotApplicable("pdf-bookmarks-read-failed");
        }

        var usable = entries.Where(e => IsUsable(e, pages)).ToList();
        if (usable.Count < 5 || usable.Count != entries.Count)
            return new PdfBookmarkOutlineResult(false, [], $"invalid-bookmark-tree:{usable.Count}/{entries.Count}", entries.Count, 0, 0);

        var headings = AlignToDocx(usable, slim);
        var ratio = headings.Count / (double)usable.Count;
        if (headings.Count < 5 || ratio < 0.70)
            return new PdfBookmarkOutlineResult(false, headings,
                $"low-docx-bookmark-alignment:{headings.Count}/{usable.Count}", entries.Count, usable.Count, headings.Count);

        return new PdfBookmarkOutlineResult(true, headings,
            $"pdf={Path.GetFileName(pdf)}, bookmarks={usable.Count}, docxAligned={headings.Count}",
            entries.Count, usable.Count, headings.Count);
    }

    private static IEnumerable<PdfBookmarkEntry> Flatten(IEnumerable<BookmarkNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (node is DocumentBookmarkNode documentNode)
                yield return new PdfBookmarkEntry(documentNode.Title, documentNode.Level, documentNode.PageNumber);
            foreach (var child in Flatten(node.Children)) yield return child;
        }
    }

    // PdfPig's high-level bookmark provider can return a plain BookmarkNode for valid /Dest
    // entries. Read the PDF outline tree directly so that author-declared destinations remain
    // available even when that projection has no DocumentBookmarkNode.
    internal static IEnumerable<PdfBookmarkEntry> ReadOutlineTree(PdfDocument document)
    {
        var structure = document.Structure;
        if (!structure.Catalog.CatalogDictionary.TryGet(NameToken.Create("Outlines"), out var outlinesToken))
            yield break;

        var pages = PageReferences(document, structure);
        var outlines = Resolve(outlinesToken, structure) as DictionaryToken;
        if (outlines is null || !outlines.TryGet(NameToken.Create("First"), out var first))
            yield break;

        foreach (var entry in WalkOutlineSiblings(first, 1, structure, pages, new HashSet<IndirectReference>()))
            yield return entry;
    }

    private static IEnumerable<PdfBookmarkEntry> WalkOutlineSiblings(
        IToken first,
        int level,
        Structure structure,
        IReadOnlyDictionary<IndirectReference, int> pages,
        HashSet<IndirectReference> visited)
    {
        IToken? current = first;
        while (current is not null)
        {
            var reference = current as IndirectReferenceToken;
            if (reference is not null && !visited.Add(reference.Data)) yield break;
            var node = Resolve(current, structure) as DictionaryToken;
            if (node is null) yield break;

            var title = TitleOf(node, structure);
            var page = PageOfDestination(node, structure, pages);
            if (!string.IsNullOrWhiteSpace(title) && page is not null)
                yield return new PdfBookmarkEntry(title, level, page.Value);

            if (node.TryGet(NameToken.Create("First"), out var child))
                foreach (var descendant in WalkOutlineSiblings(child, level + 1, structure, pages, visited))
                    yield return descendant;

            current = node.TryGet(NameToken.Create("Next"), out var next) ? next : null;
        }
    }

    private static string TitleOf(DictionaryToken node, Structure structure)
    {
        if (!node.TryGet(NameToken.Create("Title"), out var token)) return "";
        var value = Resolve(token, structure);
        return value is StringToken text ? text.Data : "";
    }

    private static int? PageOfDestination(
        DictionaryToken node,
        Structure structure,
        IReadOnlyDictionary<IndirectReference, int> pages)
    {
        if (node.TryGet(NameToken.Create("Dest"), out var destination))
            return PageOfDestinationToken(destination, structure, pages);

        if (!node.TryGet(NameToken.Create("A"), out var action)) return null;
        var actionDictionary = Resolve(action, structure) as DictionaryToken;
        return actionDictionary is not null && actionDictionary.TryGet(NameToken.Create("D"), out var actionDestination)
            ? PageOfDestinationToken(actionDestination, structure, pages)
            : null;
    }

    private static int? PageOfDestinationToken(
        IToken token,
        Structure structure,
        IReadOnlyDictionary<IndirectReference, int> pages)
    {
        var destination = Resolve(token, structure) as ArrayToken;
        if (destination is null || destination.Data.Count == 0) return null;
        return destination.Data[0] is IndirectReferenceToken pageReference && pages.TryGetValue(pageReference.Data, out var page)
            ? page
            : null;
    }

    private static IReadOnlyDictionary<IndirectReference, int> PageReferences(PdfDocument document, Structure structure)
    {
        var result = new Dictionary<IndirectReference, int>();
        if (!structure.Catalog.CatalogDictionary.TryGet(NameToken.Create("Pages"), out var root)) return result;
        var nextPage = 1;
        WalkPageTree(root, structure, result, ref nextPage);
        return result;
    }

    private static void WalkPageTree(IToken token, Structure structure, Dictionary<IndirectReference, int> pages, ref int nextPage)
    {
        var reference = token as IndirectReferenceToken;
        var node = Resolve(token, structure) as DictionaryToken;
        if (node is null) return;
        var type = node.TryGet(NameToken.Create("Type"), out NameToken typeToken) ? typeToken.Data : "";
        if (string.Equals(type, "Page", StringComparison.Ordinal))
        {
            if (reference is not null) pages[reference.Data] = nextPage;
            nextPage++;
            return;
        }
        if (!node.TryGet(NameToken.Create("Kids"), out ArrayToken kids)) return;
        foreach (var child in kids.Data) WalkPageTree(child, structure, pages, ref nextPage);
    }

    private static IToken? Resolve(IToken token, Structure structure)
    {
        var resolved = token is IndirectReferenceToken reference ? structure.GetObject(reference.Data) : token;
        return resolved is ObjectToken indirectObject ? indirectObject.Data : resolved;
    }

    private static bool IsUsable(PdfBookmarkEntry entry, int pages) =>
        entry.Level is >= 1 and <= 9 &&
        entry.Page >= 1 && entry.Page <= pages &&
        CanonicalMap.For(entry.Title).Canonical.Length >= 3 &&
        entry.Title.Length <= 240;

    private static List<HeadingRecord> AlignToDocx(IReadOnlyList<PdfBookmarkEntry> entries, SlimDocument slim)
    {
        var paragraphs = slim.Paragraphs
            .Where(p => p.Role != ParagraphRole.Empty && !p.InTableOfContents && !string.IsNullOrWhiteSpace(p.Text))
            .OrderBy(p => p.Index)
            .Select(p => new CanonParagraph(p, CanonicalMap.For(p.Text)))
            .Where(p => p.Map.Canonical.Length > 0)
            .ToList();

        var result = new List<HeadingRecord>();
        var cursor = 0;
        foreach (var entry in entries)
        {
            var needle = CanonicalMap.For(entry.Title).Canonical;
            var match = paragraphs
                .Where(p => p.Paragraph.Index >= cursor)
                .Select(p => (Paragraph: p, At: p.Map.Canonical.IndexOf(needle, StringComparison.Ordinal)))
                .FirstOrDefault(x => x.At >= 0);
            if (match.Paragraph is null) continue;

            var start = match.Paragraph.Map.SourceIndexes[match.At];
            var end = match.Paragraph.Map.SourceIndexes[match.At + needle.Length - 1] + 1;
            result.Add(new HeadingRecord
            {
                Index = match.Paragraph.Paragraph.Index,
                StableId = match.Paragraph.Paragraph.StableId,
                Level = entry.Level,
                Text = CleanTitle(entry.Title),
                OriginalText = match.Paragraph.Paragraph.Text,
                HeadingSpan = new TextOffsetSpan(start, end),
                BoundarySource = "PdfBookmark",
                StyleId = match.Paragraph.Paragraph.StyleId,
                Source = HeadingSource.Structure,
                Confidence = 0.99,
                DecisionStatus = HeadingDecisionStatus.AutoAcceptedEvidence,
                ConfidenceBasis = Basis,
            });
            cursor = match.Paragraph.Paragraph.Index;
        }

        return result;
    }

    private static string CleanTitle(string value) => PdfTextUtilities.HeadingReadable(value).Trim();

    internal sealed record PdfBookmarkEntry(string Title, int Level, int Page);
    private sealed record CanonParagraph(SlimParagraph Paragraph, CanonicalMap Map);

    private sealed record CanonicalMap(string Canonical, IReadOnlyList<int> SourceIndexes)
    {
        public static CanonicalMap For(string text)
        {
            var canonical = new StringBuilder(text.Length);
            var indexes = new List<int>(text.Length);
            for (var index = 0; index < text.Length; index++)
            {
                if (!char.IsLetterOrDigit(text[index])) continue;
                canonical.Append(char.ToLowerInvariant(text[index]));
                indexes.Add(index);
            }
            return new CanonicalMap(canonical.ToString(), indexes);
        }
    }
}

/// <summary>
/// Read-only audit for the PDF outline tree. It follows raw /Outlines destinations because some
/// valid files are projected by PdfPig as plain BookmarkNode instances without a page number.
/// The probe deliberately has no production routing decision.
/// </summary>
public static class PdfBookmarkProbe
{
    public static PdfBookmarkProbeReport Analyze(string pdfPath)
    {
        try
        {
            using var document = PdfDocument.Open(pdfPath);
            var candidates = PdfBookmarkOutline.ReadOutlineTree(document)
                .Select(e => new PdfBookmarkCandidate(e.Title, e.Level, e.Page))
                .ToList();
            return new PdfBookmarkProbeReport(pdfPath, document.NumberOfPages, "ok", candidates);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new PdfBookmarkProbeReport(pdfPath, 0, $"pdf-read-failed:{ex.GetType().Name}", []);
        }
    }
}

public sealed record PdfBookmarkProbeReport(
    string Pdf,
    int Pages,
    string Status,
    IReadOnlyList<PdfBookmarkCandidate> Candidates);

public sealed record PdfBookmarkCandidate(string Title, int Level, int Page);
