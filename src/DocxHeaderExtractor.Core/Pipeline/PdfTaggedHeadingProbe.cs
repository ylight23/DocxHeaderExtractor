using System.Text;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Tokens;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>
/// Read-only audit for tagged PDF headings. Each candidate comes directly from a marked-content
/// element whose tag is /H or /H1..H6; MCID and PDF text are retained before any DOCX alignment.
/// It deliberately does not make a production routing decision.
/// </summary>
public static class PdfTaggedHeadingProbe
{
    private static readonly Regex HeadingTag = new(@"^H(?<level>[1-6])?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static PdfTaggedHeadingProbeReport Analyze(string pdfPath, SlimDocument? docx = null)
    {
        try
        {
            using var document = PdfDocument.Open(pdfPath);
            var pages = document.GetPages().ToList();
            var all = new List<PdfTaggedHeadingCandidate>();
            var markedCount = 0;
            var markedByPageAndMcid = new Dictionary<(int Page, int Mcid), MarkedContentElement>();
            foreach (var page in pages)
            {
                foreach (var element in Flatten(page.GetMarkedContents()))
                {
                    markedCount++;
                    markedByPageAndMcid.TryAdd((page.Number, element.MarkedContentIdentifier), element);
                    var tag = element.Tag.ToString().TrimStart('/');
                    var match = HeadingTag.Match(tag);
                    if (!match.Success) continue;

                    var text = TextOf(element);
                    if (PdfTextUtilities.CanonicalForMatch(text).Length < 3) continue;
                    var level = match.Groups["level"].Success
                        ? int.Parse(match.Groups["level"].Value)
                        : 1;
                    all.Add(new PdfTaggedHeadingCandidate(
                        page.Number,
                        element.MarkedContentIdentifier,
                        tag,
                        level,
                        text,
                        PdfTextUtilities.CanonicalForMatch(text),
                        null,
                        null,
                        null));
                }
            }

            // The /H* semantic tag may live solely in StructTreeRoot while the page content is
            // tagged as /Span. Follow StructTreeRoot -> /K -> MCID and use page marked content only
            // to recover the source letters. This is distinct from the direct-tag fast path above.
            var structureCandidates = ReadStructureTree(document, pages, markedByPageAndMcid, out var structureTrace);
            if (structureCandidates.Count > 0)
                all = structureCandidates;

            var aligned = docx is null ? all : Align(all, docx);
            return new PdfTaggedHeadingProbeReport(
                pdfPath, document.NumberOfPages, markedCount, aligned.Count,
                aligned.Count(c => c.DocxParagraphIndex is not null), "ok", structureTrace, aligned);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new PdfTaggedHeadingProbeReport(pdfPath, 0, 0, 0, 0,
                $"pdf-read-failed:{ex.GetType().Name}", TaggedStructureTrace.Empty, []);
        }
    }

    private static IEnumerable<MarkedContentElement> Flatten(IEnumerable<MarkedContentElement> elements)
    {
        foreach (var element in elements)
        {
            yield return element;
            foreach (var child in Flatten(element.Children)) yield return child;
        }
    }

    private static List<PdfTaggedHeadingCandidate> ReadStructureTree(
        PdfDocument document,
        IReadOnlyList<Page> pages,
        IReadOnlyDictionary<(int Page, int Mcid), MarkedContentElement> markedByPageAndMcid,
        out TaggedStructureTrace trace)
    {
        var counts = new TaggedStructureTraceBuilder();
        if (!document.Structure.Catalog.CatalogDictionary.TryGet(NameToken.Create("StructTreeRoot"), out var rootToken))
        {
            trace = counts.Build();
            return [];
        }

        var structure = document.Structure;
        var scanner = structure.TokenScanner;
        counts.StructTreeRootTokenType = rootToken.GetType().Name;
        var resolvedRoot = Resolve(rootToken, structure);
        counts.StructTreeRootResolvedTokenType = resolvedRoot?.GetType().Name ?? "";
        var root = resolvedRoot as DictionaryToken;
        counts.StructTreeRootResolved = root is not null;
        IToken? rootKids = null;
        counts.StructTreeRootHasKids = root is not null && root.TryGet(NameToken.Create("K"), out rootKids);
        counts.StructTreeRootKidsTokenType = rootKids?.GetType().Name ?? "";
        var pageByReference = PageReferences(document, structure);
        var byDictionary = pages.ToDictionary(p => p.Dictionary, p => p.Number);
        var result = new List<PdfTaggedHeadingCandidate>();
        Walk(root, null, structure, pageByReference, byDictionary, markedByPageAndMcid, result, counts);
        trace = counts.Build();
        return result;
    }

    private static void Walk(
        DictionaryToken? node,
        int? inheritedPage,
        Structure structure,
        IReadOnlyDictionary<IndirectReference, int> pageByReference,
        IReadOnlyDictionary<DictionaryToken, int> pageByDictionary,
        IReadOnlyDictionary<(int Page, int Mcid), MarkedContentElement> markedByPageAndMcid,
        List<PdfTaggedHeadingCandidate> result,
        TaggedStructureTraceBuilder trace)
    {
        if (node is null) return;
        var page = PageOf(node, inheritedPage, structure, pageByReference, pageByDictionary);
        var tag = node.TryGet(NameToken.Create("S"), out NameToken tagToken) ? tagToken.Data : "";
        var match = HeadingTag.Match(tag);
        if (match.Success)
        {
            trace.HeadingNodes++;
            if (page is null) trace.HeadingNodesWithoutPage++;
        }
        if (match.Success && page is not null && node.TryGet(NameToken.Create("K"), out var content))
        {
            var level = match.Groups["level"].Success ? int.Parse(match.Groups["level"].Value) : 1;
            foreach (var mcid in Mcids(content, structure))
            {
                trace.Mcids++;
                if (!markedByPageAndMcid.TryGetValue((page.Value, mcid), out var marked)) { trace.McidsWithoutMarkedContent++; continue; }
                var text = TextOf(marked);
                if (PdfTextUtilities.CanonicalForMatch(text).Length < 3) continue;
                trace.TextResolved++;
                result.Add(new PdfTaggedHeadingCandidate(
                    page.Value, mcid, tag, level, text, PdfTextUtilities.CanonicalForMatch(text), null, null, null));
            }
        }

        if (!node.TryGet(NameToken.Create("K"), out var children)) return;
        foreach (var child in ChildStructElements(children, structure))
            Walk(child, page, structure, pageByReference, pageByDictionary, markedByPageAndMcid, result, trace);
    }

    private static IEnumerable<DictionaryToken> ChildStructElements(
        IToken token,
        Structure structure)
    {
        var resolved = Resolve(token, structure);
        if (resolved is ArrayToken array)
        {
            foreach (var item in array.Data)
                foreach (var child in ChildStructElements(item, structure)) yield return child;
        }
        else if (resolved is DictionaryToken dictionary)
        {
            yield return dictionary;
        }
    }

    private static IEnumerable<int> Mcids(
        IToken token,
        Structure structure)
    {
        var resolved = Resolve(token, structure);
        if (resolved is NumericToken number)
        {
            yield return number.Int;
            yield break;
        }
        if (resolved is ArrayToken array)
        {
            foreach (var item in array.Data)
                foreach (var mcid in Mcids(item, structure)) yield return mcid;
            yield break;
        }
        if (resolved is DictionaryToken dictionary && dictionary.TryGet(NameToken.Create("MCID"), out NumericToken markedContentId))
            yield return markedContentId.Int;
    }

    private static int? PageOf(
        DictionaryToken node,
        int? inheritedPage,
        Structure structure,
        IReadOnlyDictionary<IndirectReference, int> pageByReference,
        IReadOnlyDictionary<DictionaryToken, int> pageByDictionary)
    {
        if (!node.TryGet(NameToken.Create("Pg"), out var pageToken)) return inheritedPage;
        if (pageToken is IndirectReferenceToken reference && pageByReference.TryGetValue(reference.Data, out var directPage))
            return directPage;
        var dictionary = Resolve(pageToken, structure) as DictionaryToken;
        return dictionary is not null && pageByDictionary.TryGetValue(dictionary, out var page) ? page : inheritedPage;
    }

    private static IReadOnlyDictionary<IndirectReference, int> PageReferences(
        PdfDocument document,
        Structure structure)
    {
        var map = new Dictionary<IndirectReference, int>();
        if (!document.Structure.Catalog.CatalogDictionary.TryGet(NameToken.Create("Pages"), out var pages)) return map;
        var nextPage = 1;
        WalkPageTree(pages, structure, map, ref nextPage);
        return map;
    }

    private static void WalkPageTree(
        IToken token,
        Structure structure,
        Dictionary<IndirectReference, int> map,
        ref int nextPage)
    {
        var reference = token as IndirectReferenceToken;
        var node = Resolve(token, structure) as DictionaryToken;
        if (node is null) return;
        var type = node.TryGet(NameToken.Create("Type"), out NameToken typeToken) ? typeToken.Data : "";
        if (string.Equals(type, "Page", StringComparison.Ordinal))
        {
            if (reference is not null) map[reference.Data] = nextPage;
            nextPage++;
            return;
        }
        if (!node.TryGet(NameToken.Create("Kids"), out ArrayToken kids)) return;
        foreach (var child in kids.Data)
            WalkPageTree(child, structure, map, ref nextPage);
    }

    private static IToken? Resolve(IToken token, Structure structure)
    {
        var resolved = token is IndirectReferenceToken reference
            ? structure.GetObject(reference.Data)
            : token;

        // Structure.GetObject preserves the indirect-object wrapper. The logical structure tree
        // lives in its Data token, unlike the page API which already exposes direct dictionaries.
        return resolved is ObjectToken indirectObject ? indirectObject.Data : resolved;
    }

    private static string TextOf(MarkedContentElement element)
    {
        if (!string.IsNullOrWhiteSpace(element.ActualText))
            return PdfTextUtilities.HeadingReadable(element.ActualText);

        var letters = element.Letters.OrderBy(l => l.BoundingBox.Left).ToList();
        if (letters.Count == 0) return "";
        var text = new StringBuilder();
        Letter? previous = null;
        foreach (var letter in letters)
        {
            if (previous is not null &&
                letter.BoundingBox.Left - previous.BoundingBox.Right > Math.Max(1.2, previous.FontSize * 0.18))
                text.Append(' ');
            text.Append(letter.Value);
            previous = letter;
        }
        return PdfTextUtilities.HeadingReadable(text.ToString());
    }

    private static List<PdfTaggedHeadingCandidate> Align(
        IReadOnlyList<PdfTaggedHeadingCandidate> candidates,
        SlimDocument docx)
    {
        var paragraphs = docx.Paragraphs
            .Where(p => p.Role != ParagraphRole.Empty && !p.InTableOfContents && !string.IsNullOrWhiteSpace(p.Text))
            .OrderBy(p => p.Index)
            .Select(p => new { Paragraph = p, Map = CanonicalMap.For(p.Text) })
            .ToList();
        var cursor = 0;
        var result = new List<PdfTaggedHeadingCandidate>();
        foreach (var candidate in candidates)
        {
            var match = paragraphs
                .Where(p => p.Paragraph.Index >= cursor)
                .Select(p => new { p.Paragraph, p.Map, At = p.Map.Canonical.IndexOf(candidate.CanonicalText, StringComparison.Ordinal) })
                .FirstOrDefault(p => p.At >= 0);
            if (match is null)
            {
                result.Add(candidate);
                continue;
            }

            var start = match.Map.SourceIndexes[match.At];
            var end = match.Map.SourceIndexes[match.At + candidate.CanonicalText.Length - 1] + 1;
            result.Add(candidate with
            {
                DocxParagraphIndex = match.Paragraph.Index,
                DocxStableId = match.Paragraph.StableId,
                HeadingSpan = new TextOffsetSpan(start, end),
            });
            cursor = match.Paragraph.Index;
        }
        return result;
    }

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

public sealed record PdfTaggedHeadingProbeReport(
    string Pdf,
    int Pages,
    int MarkedContentElements,
    int HeadingElements,
    int DocxAligned,
    string Status,
    TaggedStructureTrace StructureTree,
    IReadOnlyList<PdfTaggedHeadingCandidate> Candidates);

public sealed record TaggedStructureTrace(
    string StructTreeRootTokenType,
    string StructTreeRootResolvedTokenType,
    bool StructTreeRootResolved,
    bool StructTreeRootHasKids,
    string StructTreeRootKidsTokenType,
    int HeadingNodes,
    int HeadingNodesWithoutPage,
    int Mcids,
    int McidsWithoutMarkedContent,
    int TextResolved)
{
    public static TaggedStructureTrace Empty { get; } = new("", "", false, false, "", 0, 0, 0, 0, 0);
}

internal sealed class TaggedStructureTraceBuilder
{
    public string StructTreeRootTokenType { get; set; } = "";
    public string StructTreeRootResolvedTokenType { get; set; } = "";
    public bool StructTreeRootResolved { get; set; }
    public bool StructTreeRootHasKids { get; set; }
    public string StructTreeRootKidsTokenType { get; set; } = "";
    public int HeadingNodes { get; set; }
    public int HeadingNodesWithoutPage { get; set; }
    public int Mcids { get; set; }
    public int McidsWithoutMarkedContent { get; set; }
    public int TextResolved { get; set; }
    public TaggedStructureTrace Build() => new(StructTreeRootTokenType, StructTreeRootResolvedTokenType, StructTreeRootResolved, StructTreeRootHasKids,
        StructTreeRootKidsTokenType, HeadingNodes, HeadingNodesWithoutPage, Mcids, McidsWithoutMarkedContent, TextResolved);
}

public sealed record PdfTaggedHeadingCandidate(
    int Page,
    int MarkedContentIdentifier,
    string Tag,
    int Level,
    string Text,
    string CanonicalText,
    int? DocxParagraphIndex,
    string? DocxStableId,
    TextOffsetSpan? HeadingSpan);
