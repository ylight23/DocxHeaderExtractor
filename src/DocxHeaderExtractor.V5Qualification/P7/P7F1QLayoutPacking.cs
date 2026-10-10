using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Semantics.Canonical;
using DocxHeaderExtractor.DocumentProcessing.Source.Common;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;

namespace DocxHeaderExtractor.V5Qualification.P7;

/// <summary>
/// Experiment arm "layout-aware chunking" (P7-F1Q held-out, 2026-10-11). Changes ONLY where packs and chunks are cut,
/// relative to P05 + balanced 48-chunks: a pack is a run of whole pages (owned atoms &lt;= the P05 owned bound); a page that
/// alone exceeds the bound is cut at parser layout-block boundaries; chunks (&lt;= the protocol chunk size) are cut at
/// layout-block boundaries. Halo, owned/visible bounds, prompt, tools, schema, model and reasoning are unchanged.
/// Boundaries use page numbers and parser layout-block ids only: never a heading predicate, salience, provider
/// output or Gold.
/// </summary>
internal static class P7F1QLayoutPacking
{
    public const string Version = "P7_F1Q_LAYOUT_AWARE_PAGE_BLOCK_PACKING_V1";
    public const int MaxOwned = 96, Halo = 8;

    public static IReadOnlyList<SemanticEvidencePack> BuildPacks(DocumentSourceSnapshot source, PdfSourceDetails details)
    {
        var evidence = source.Evidence;
        var page = source.Atoms.ToDictionary(a => a.Alias, a => a.Page, StringComparer.Ordinal);
        var units = Units(evidence, i => page[evidence[i].SourceAlias], i => Block(details, evidence[i]), MaxOwned);
        var packs = new List<SemanticEvidencePack>();
        for (var u = 0; u < units.Count;)
        {
            var start = units[u].Start; var end = units[u].End; u++;
            while (u < units.Count && units[u].End - start <= MaxOwned) { end = units[u].End; u++; }
            var from = Math.Max(0, start - Halo); var to = Math.Min(evidence.Count, end + Halo);
            packs.Add(new SemanticEvidencePack($"{Version}:PACK_{packs.Count + 1:000}", packs.Count + 1,
                evidence.Skip(start).Take(end - start).ToArray(), evidence.Skip(from).Take(to - from).ToArray()));
        }
        if (packs.Sum(p => p.Owned.Count) != evidence.Count || packs.SelectMany(p => p.Owned).Select(e => e.SourceAlias).Distinct().Count() != evidence.Count)
            throw new InvalidOperationException("layout-packing-conservation");
        return packs;
    }

    /// <summary>Same selection rule as P05: packs that own any atom on an approved page.</summary>
    public static IReadOnlyList<SemanticEvidencePack> SelectPacks(DocumentSourceSnapshot source, PdfSourceDetails details, IReadOnlyList<int> pages)
    {
        var page = source.Atoms.ToDictionary(a => a.Alias, a => a.Page, StringComparer.Ordinal);
        return BuildPacks(source, details).Where(p => p.Owned.Any(a => pages.Contains(page[a.SourceAlias]))).ToArray();
    }

    /// <summary>Chunks of at most <paramref name="max"/> issued occurrences, cut at page and layout-block boundaries.</summary>
    public static IReadOnlyList<IReadOnlyList<T>> Chunks<T>(IReadOnlyList<T> issued, Func<T, int> pageOf, Func<T, string> blockOf, int max)
    {
        var units = Units(issued, i => pageOf(issued[i]), i => blockOf(issued[i]), max);
        var chunks = new List<IReadOnlyList<T>>();
        for (var u = 0; u < units.Count;)
        {
            var start = units[u].Start; var end = units[u].End; u++;
            while (u < units.Count && units[u].End - start <= max) { end = units[u].End; u++; }
            chunks.Add(issued.Skip(start).Take(end - start).ToArray());
        }
        return chunks;
    }

    public static string Block(PdfSourceDetails details, CanonicalSemanticSourceEvidence e) =>
        details.LayoutBlockByAtom.GetValueOrDefault(e.SourceId) ?? "NO_BLOCK:" + e.SourceId;

    // Indivisible units in reading order: a whole page when it fits, else layout blocks of that page, else count slices.
    static List<(int Start, int End)> Units<T>(IReadOnlyList<T> items, Func<int, int> pageOf, Func<int, string> blockOf, int max)
    {
        var units = new List<(int, int)>();
        for (var s = 0; s < items.Count;)
        {
            var e = s; while (e < items.Count && pageOf(e) == pageOf(s)) e++;
            if (e - s <= max) { units.Add((s, e)); s = e; continue; }
            for (var b = s; b < e;)
            {
                var be = b; while (be < e && blockOf(be) == blockOf(b)) be++;
                for (var k = b; k < be; k += max) units.Add((k, Math.Min(be, k + max)));
                b = be;
            }
            s = e;
        }
        return units;
    }
}
