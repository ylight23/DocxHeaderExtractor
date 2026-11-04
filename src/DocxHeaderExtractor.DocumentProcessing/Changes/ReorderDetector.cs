using DocxHeaderExtractor.Sources.Docx.Changes;

namespace DocxHeaderExtractor.DocumentProcessing.Changes;

/// <summary>A candidate block whose relative order changed. Either block in a swap could have moved; this does not infer the edit action.</summary>
public sealed record ReorderBlock(IReadOnlyList<ChangeSide> Before, IReadOnlyList<ChangeSide> After, IReadOnlyList<string> MatchEvidence,
    bool Uncertain = true);

/// <summary>Optional evidence only; original text changes remain intact. Table/text-box moves and modified paragraph moves are not resolved.</summary>
public sealed record ReorderAnalysis(IReadOnlyList<ReorderBlock> Blocks, int UnmatchedOrAmbiguousBodyParagraphs, int NonBodyParagraphsSkipped);

/// <summary>
/// An offline candidate analysis of relative ordering, not a reconstruction of user actions. Identical text and unique paraId, or identical text
/// unique on both sides, supplies an anchor; a conflicting ID blocks text-only inference. Crossing anchors identify both sides of a reorder.
/// </summary>
public static class ReorderDetector
{
    sealed record Pair(int OldIndex, int NewIndex, ParagraphFingerprint Old, ParagraphFingerprint New, string Evidence);

    public static ReorderAnalysis Analyze(DocxVersion old, DocxVersion current)
    {
        ArgumentNullException.ThrowIfNull(old);
        ArgumentNullException.ThrowIfNull(current);
        var oldIds = old.Paragraphs.Where(p => !string.IsNullOrEmpty(p.ParaId)).GroupBy(p => p.ParaId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var newIds = current.Paragraphs.Where(p => !string.IsNullOrEmpty(p.ParaId)).GroupBy(p => p.ParaId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var oldText = old.Paragraphs.Where(p => !string.IsNullOrWhiteSpace(p.RawText)).GroupBy(p => p.RawText, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var newText = current.Paragraphs.Where(p => !string.IsNullOrWhiteSpace(p.RawText)).GroupBy(p => p.RawText, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var newIndexes = current.Paragraphs.Select((p, i) => (p.Location, i)).ToDictionary(x => x.Location, x => x.i, StringComparer.Ordinal);
        var pairs = new List<Pair>();
        var unresolved = 0;
        foreach (var (p, index) in old.Paragraphs.Select((p, i) => (p, i)).Where(x => x.p.Container == "body" && !string.IsNullOrWhiteSpace(x.p.RawText)))
        {
            ParagraphFingerprint? idMatch = null;
            if (p.ParaId is { Length: > 0 } id && oldIds[id].Length == 1 && newIds.TryGetValue(id, out var ns) && ns.Length == 1)
                idMatch = ns[0];
            var textMatch = oldText[p.RawText].Length == 1 && newText.TryGetValue(p.RawText, out var matches) && matches.Length == 1 ? matches[0] : null;
            // An ID that now points at different text is not proof of a move, and must not silently be overridden by another text match.
            if (idMatch is not null && (!StringComparer.Ordinal.Equals(idMatch.RawText, p.RawText) ||
                textMatch is not null && textMatch.Location != idMatch.Location)) { unresolved++; continue; }
            var candidate = idMatch ?? textMatch;
            if (candidate is null || candidate.Container != "body") { unresolved++; continue; }
            pairs.Add(new(index, newIndexes[candidate.Location], p, candidate, idMatch is null ? "UNIQUE_EXACT_TEXT" : "UNIQUE_PARA_ID_AND_EXACT_TEXT"));
        }

        var n = pairs.Count;
        var prefixMax = new int[n]; var suffixMin = new int[n];
        for (var i = 0; i < n; i++) prefixMax[i] = i == 0 ? -1 : Math.Max(prefixMax[i - 1], pairs[i - 1].NewIndex);
        for (var i = n - 1; i >= 0; i--) suffixMin[i] = i == n - 1 ? int.MaxValue : Math.Min(suffixMin[i + 1], pairs[i + 1].NewIndex);
        var crossing = pairs.Where((p, i) => p.NewIndex < prefixMax[i] || p.NewIndex > suffixMin[i]).ToArray();
        var groups = new List<List<Pair>>();
        foreach (var p in crossing)
        {
            if (groups.Count == 0 || groups[^1][^1].OldIndex + 1 != p.OldIndex || groups[^1][^1].NewIndex + 1 != p.NewIndex)
                groups.Add([]);
            groups[^1].Add(p);
        }
        static ChangeSide Side(ParagraphFingerprint p) => new(p.Location, p.Ordinal, p.RawText, null);
        var blocks = groups.Select(g => new ReorderBlock(g.Select(p => Side(p.Old)).ToArray(), g.Select(p => Side(p.New)).ToArray(),
            g.Select(p => p.Evidence).ToArray())).ToArray();
        return new(blocks, unresolved, old.Paragraphs.Count(p => p.Container != "body") + current.Paragraphs.Count(p => p.Container != "body"));
    }
}
