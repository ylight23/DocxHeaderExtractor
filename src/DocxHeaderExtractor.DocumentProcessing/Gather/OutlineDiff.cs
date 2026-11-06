using DocxHeaderExtractor.DocumentProcessing.Changes;
using DocxHeaderExtractor.DocumentProcessing.Output;
using DocxHeaderExtractor.Contracts.Models;
using System.Security.Cryptography;
using System.Text;

namespace DocxHeaderExtractor.DocumentProcessing.Gather;

/// <summary>
/// One difference between an old and a new outline, to be reviewed. <see cref="Kind"/>:
/// ADDED (a heading only the new outline has, on unchanged text), REMOVED (a heading only the old one has, on unchanged text),
/// TEXT (same start paragraph, other heading text), SPLIT (the old heading's text runs into a new heading), MERGED (an old heading is now part of
/// the text of another one), LEVEL, PARENT, ON_CHANGED_TEXT (a new heading on a paragraph that changed) or SOURCE_CHANGED (an old heading whose
/// paragraph changed or was removed). <see cref="OldApproved"/>: the old decision was reviewed. <see cref="ContextChanged"/>: a changed paragraph
/// lies within <see cref="OutlineDiff.Context"/> paragraphs. <see cref="Uncertain"/>: the paragraphs could not be paired for sure (repeated text).
/// Nothing here decides which side is right.
/// </summary>
public sealed record OutlineDifference(string Kind, string? OldLocation, string? NewLocation, string? OldText, string? NewText,
    int? OldLevel, int? NewLevel, string? OldParent, string? NewParent, bool OldApproved, bool ContextChanged, bool Uncertain)
{
    public TextOffsetSpan? OldHeadingSpan { get; init; }
    public TextOffsetSpan? NewHeadingSpan { get; init; }
    public TextOffsetSpan? OldInlineBodySpan { get; init; }
    public TextOffsetSpan? NewInlineBodySpan { get; init; }
    public IReadOnlyList<HeadingSourcePart>? OldSourceParts { get; init; }
    public IReadOnlyList<HeadingSourcePart>? NewSourceParts { get; init; }
    /// <summary>Individual differences retained inside a display group; no decision is applied to them.</summary>
    public IReadOnlyList<OutlineDifference> Details { get; init; } = [];
    /// <summary>ContextChanged only measures nearby paragraph signatures; style, numbering and tool evidence are not verified here.</summary>
    public bool ContextEvidenceVerified => false;
    /// <summary>Ordered paragraph-signature hash for the direct section: CHANGED, UNCHANGED, or UNAVAILABLE. This is not proof of complete context.</summary>
    public string SectionSignal { get; init; } = "UNAVAILABLE";
}

/// <summary>The differences, how many headings are the same on both sides, and how many paragraphs changed between the two files.</summary>
public sealed record OutlineComparison(IReadOnlyList<OutlineDifference> Differences, int SameHeadings, int ChangedParagraphs);

/// <summary>
/// Compares two outlines without a model: of the same file (two runs) or of two versions of a file. The paragraphs of both are paired by Myers on
/// their signatures; a heading is identified by the paragraph it starts on, through that pairing. Every difference is listed for review with the
/// evidence that matters (approved or not, text changed nearby, pairing certain or not); none is resolved here.
/// </summary>
public static class OutlineDiff
{
    /// <summary>Paragraphs on each side of a difference within which a changed paragraph marks its context as changed.</summary>
    public const int Context = 20;

    /// <summary>Known parents only. A null/invalid level or unbound heading makes following ancestry unknown until a known level-one heading.</summary>
    public static IReadOnlyDictionary<string, string?> Parents(IReadOnlyList<HeadingRecord> headings)
    {
        var parents = new Dictionary<string, string?>(StringComparer.Ordinal);
        var open = new List<(string Location, int Level)>();
        var uncertain = false;
        foreach (var h in headings.OrderBy(h => h.Index))
        {
            if (h.Level is null && h.HierarchyResolution == "model-out-of-hierarchy" && h.StableId is not null) continue;
            if (h.StableId is null || h.Level is not (>= 1 and <= 9)) { uncertain = true; open.Clear(); continue; }
            var level = h.Level.Value;
            if (level == 1) { uncertain = false; open.Clear(); }
            if (uncertain) continue;
            while (open.Count > 0 && open[^1].Level >= level) open.RemoveAt(open.Count - 1);
            if (level > 1 && open.Count == 0) { uncertain = true; continue; }
            parents[h.StableId!] = open.Count > 0 ? open[^1].Location : null;
            open.Add((h.StableId!, level));
        }
        return parents;
    }

    static string Compact(string text) => string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    static bool ValidBoundary(HeadingRecord h)
    {
        if (h.HeadingSpan is null || h.SourceParts is not { Count: > 0 } parts || h.ValidatedSourcePartCount != parts.Count) return false;
        if (parts[0] is not { OriginalText: { } text }) return false;
        static bool Valid(string s, TextOffsetSpan span) => span.Start >= 0 && span.End > span.Start && span.End <= s.Length &&
            !(span.Start > 0 && span.Start < s.Length && char.IsHighSurrogate(s[span.Start - 1]) && char.IsLowSurrogate(s[span.Start])) &&
            !(span.End > 0 && span.End < s.Length && char.IsHighSurrogate(s[span.End - 1]) && char.IsLowSurrogate(s[span.End]));
        return Valid(text, h.HeadingSpan) && parts[0].Span == h.HeadingSpan && (h.InlineBodySpan is null ? h.InlineBody is null : Valid(text, h.InlineBodySpan) &&
            h.HeadingSpan.End <= h.InlineBodySpan.Start && h.InlineBody == text[h.InlineBodySpan.Start..h.InlineBodySpan.End]);
    }

    public static OutlineComparison Compare(StoredOutline old, StoredOutline next, IReadOnlySet<string>? approvedOld = null)
    {
        approvedOld ??= new HashSet<string>();
        var edits = Myers.Diff(old.Paragraphs, next.Paragraphs, StoredParagraph.Same, Myers.Limit, out var aligned);
        // Outlines stored by different signature code cannot be aligned by signature: nothing can read as unchanged.
        var complete = aligned && old.SignatureVersion == next.SignatureVersion;
        var oldToNew = new Dictionary<int, int>();
        var newToOld = new Dictionary<int, int>();
        var marks = new List<int>();   // new positions of changed or removed paragraphs
        var pos = 0;
        foreach (var e in edits)
        {
            if (e.Kind == EditKind.Equal) { oldToNew[e.OldIndex] = e.NewIndex; newToOld[e.NewIndex] = e.OldIndex; pos = e.NewIndex + 1; }
            else if (e.Kind == EditKind.Insert) { marks.Add(e.NewIndex); pos = e.NewIndex + 1; }
            else marks.Add(pos);
        }
        bool ChangedNear(int newIndex) => marks.Any(m => Math.Abs(m - newIndex) <= Context);

        var oldIndex = old.Paragraphs.Select((p, i) => (p.Location, i)).ToDictionary(x => x.Location, x => x.i, StringComparer.Ordinal);
        var newIndex = next.Paragraphs.Select((p, i) => (p.Location, i)).ToDictionary(x => x.Location, x => x.i, StringComparer.Ordinal);
        var oldRepeated = old.Paragraphs.GroupBy(p => p.Signature).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        var newRepeated = next.Paragraphs.GroupBy(p => p.Signature).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        bool Repeated(int? o, int? n) => (o is { } a && oldRepeated.Contains(old.Paragraphs[a].Signature))
            || (n is { } b && newRepeated.Contains(next.Paragraphs[b].Signature));

        var oldGroups = old.Headings.Where(h => h.StableId is not null && oldIndex.ContainsKey(h.StableId)).GroupBy(h => h.StableId!, StringComparer.Ordinal).ToArray();
        var newGroups = next.Headings.Where(h => h.StableId is not null && newIndex.ContainsKey(h.StableId)).GroupBy(h => h.StableId!, StringComparer.Ordinal).ToArray();
        var oldHeadings = oldGroups.Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single(), StringComparer.Ordinal);
        var newHeadings = newGroups.Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single(), StringComparer.Ordinal);
        var oldParents = Parents(old.Headings);
        var newParents = Parents(next.Headings);
        string? NewLocationOf(string? oldLocation) =>
            oldLocation is not null && oldIndex.TryGetValue(oldLocation, out var o) && oldToNew.TryGetValue(o, out var n) ? next.Paragraphs[n].Location : null;

        // This is a direct-section fingerprint, not an approved outline or a model-evidence cache. Refuse to infer section boundaries
        // when any heading is unbound, duplicated, or lacks its source-bound span. The next heading must map to the same boundary.
        var oldBoundaries = old.Headings.Where(h => h.StableId is not null && oldHeadings.ContainsKey(h.StableId))
            .OrderBy(h => oldIndex[h.StableId!]).ToArray();
        var newBoundaries = next.Headings.Where(h => h.StableId is not null && newHeadings.ContainsKey(h.StableId))
            .OrderBy(h => newIndex[h.StableId!]).ToArray();
        bool validOutlines = oldBoundaries.Length == old.Headings.Count && newBoundaries.Length == next.Headings.Count
            && oldBoundaries.All(ValidBoundary) && newBoundaries.All(ValidBoundary);
        var oldBoundaryIndex = oldBoundaries.Select((h, i) => (h.StableId!, i)).ToDictionary(x => x.Item1, x => x.i, StringComparer.Ordinal);
        var newBoundaryIndex = newBoundaries.Select((h, i) => (h.StableId!, i)).ToDictionary(x => x.Item1, x => x.i, StringComparer.Ordinal);
        bool SameBinding(HeadingRecord a, HeadingRecord b) => ValidBoundary(a) && ValidBoundary(b)
            && a.SourceParts!.Count == b.SourceParts!.Count && a.HeadingSpan == b.HeadingSpan && a.InlineBodySpan == b.InlineBodySpan
            && a.SourceParts.Select((p, i) => NewLocationOf(p.SourceId) == b.SourceParts[i].SourceId && p.Span == b.SourceParts[i].Span
                && p.OriginalText == b.SourceParts[i].OriginalText).All(x => x);
        static string SectionHash(IReadOnlyList<StoredParagraph> paragraphs, int start, int end)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (var i = start; i < end; i++)
            {
                var bytes = Encoding.UTF8.GetBytes(paragraphs[i].Signature);
                hash.AppendData(BitConverter.GetBytes(bytes.Length));
                hash.AppendData(bytes);
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        static bool PartsWithin(HeadingRecord heading, IReadOnlyDictionary<string, int> indexes, int start, int end)
        {
            var previous = start - 1;
            foreach (var part in heading.SourceParts!)
            {
                if (part.SourceId is null || !indexes.TryGetValue(part.SourceId, out var at) || at < start || at >= end || at <= previous
                    || part.OriginalText is null || part.Span is null || part.Span.Start < 0 || part.Span.End <= part.Span.Start
                    || part.Span.End > part.OriginalText.Length) return false;
                previous = at;
            }
            return true;
        }
        string SectionSignal(HeadingRecord? a, HeadingRecord? b, int? oi, int? ni)
        {
            if (!validOutlines || a?.StableId is not { } al || b?.StableId is not { } bl || oi is null || ni is null
                || Repeated(oi, ni) || !oldToNew.TryGetValue(oi.Value, out var mapped) || mapped != ni || !SameBinding(a, b)
                || !oldBoundaryIndex.TryGetValue(al, out var ap) || !newBoundaryIndex.TryGetValue(bl, out var bp)) return "UNAVAILABLE";
            var hasOldNext = ap + 1 < oldBoundaries.Length;
            var hasNewNext = bp + 1 < newBoundaries.Length;
            if (hasOldNext != hasNewNext) return "UNAVAILABLE";
            var oldEnd = old.Paragraphs.Count;
            var newEnd = next.Paragraphs.Count;
            if (hasOldNext)
            {
                var oldNext = oldBoundaries[ap + 1]; var newNext = newBoundaries[bp + 1];
                oldEnd = oldIndex[oldNext.StableId!]; newEnd = newIndex[newNext.StableId!];
                if (Repeated(oldEnd, newEnd) || !oldToNew.TryGetValue(oldEnd, out var endMapped) || endMapped != newEnd
                    || !SameBinding(oldNext, newNext)) return "UNAVAILABLE";
            }
            if (oldEnd <= oi || newEnd <= ni || !PartsWithin(a, oldIndex, oi.Value, oldEnd)
                || !PartsWithin(b, newIndex, ni.Value, newEnd)) return "UNAVAILABLE";
            return SectionHash(old.Paragraphs, oi.Value, oldEnd) == SectionHash(next.Paragraphs, ni.Value, newEnd) ? "UNCHANGED" : "CHANGED";
        }

        var differences = new List<OutlineDifference>();
        var same = 0;
        var handledNew = new HashSet<string>(StringComparer.Ordinal);
        OutlineDifference Item(string kind, HeadingRecord? o, HeadingRecord? n, int? oi, int? ni) => new(kind,
            o?.StableId, n?.StableId ?? (ni is { } x ? next.Paragraphs[x].Location : null), o?.Text, n?.Text, o?.Level, n?.Level,
            o?.StableId is { } ol ? oldParents.GetValueOrDefault(ol) : null, n?.StableId is { } nl ? newParents.GetValueOrDefault(nl) : null,
            o?.StableId is { } al && approvedOld.Contains(al),
            ni is { } c ? ChangedNear(c) : oi is { } d && oldToNew.TryGetValue(d, out var m) ? ChangedNear(m) : true,
            Repeated(oi, ni) || kind is "PARENT_UNRESOLVED" or "BOUNDARY_UNRESOLVED" or "BINDING_UNRESOLVED" or "SPLIT" or "MERGED")
        { OldHeadingSpan = o?.HeadingSpan, NewHeadingSpan = n?.HeadingSpan, OldInlineBodySpan = o?.InlineBodySpan, NewInlineBodySpan = n?.InlineBodySpan,
            OldSourceParts = o?.SourceParts, NewSourceParts = n?.SourceParts, SectionSignal = SectionSignal(o, n, oi, ni) };

        foreach (var h in old.Headings.Where(h => h.StableId is null || !oldIndex.ContainsKey(h.StableId) || !oldHeadings.ContainsKey(h.StableId)))
            differences.Add(new("UNBOUND_OLD", h.StableId, null, h.Text, null, h.Level, null, null, null, false, true, true)
                { OldHeadingSpan = h.HeadingSpan, OldInlineBodySpan = h.InlineBodySpan, OldSourceParts = h.SourceParts });
        foreach (var h in next.Headings.Where(h => h.StableId is null || !newIndex.ContainsKey(h.StableId) || !newHeadings.ContainsKey(h.StableId)))
            differences.Add(new("UNBOUND_NEW", null, h.StableId, null, h.Text, null, h.Level, null, null, false, true, true)
                { NewHeadingSpan = h.HeadingSpan, NewInlineBodySpan = h.InlineBodySpan, NewSourceParts = h.SourceParts });

        // Source bindings are checked per heading only when both outlines carry them; an outline stored without them (every outline stored
        // before bindings were kept) gives one EVIDENCE_MISSING item, not one item per heading.
        var oldEvidence = old.Headings.Any(h => h.SourceParts is { Count: > 0 });
        var newEvidence = next.Headings.Any(h => h.SourceParts is { Count: > 0 });
        var evidence = oldEvidence && newEvidence;
        var pairs = new List<(HeadingRecord O, HeadingRecord N, int Oi, int Ni, List<string> Kinds)>();
        foreach (var (location, o) in oldHeadings)
        {
            var oi = oldIndex[location];
            if (!oldToNew.TryGetValue(oi, out var ni)) { differences.Add(Item("SOURCE_CHANGED", o, null, oi, null)); continue; }
            var newLocation = next.Paragraphs[ni].Location;
            if (newHeadings.TryGetValue(newLocation, out var n))
            {
                handledNew.Add(newLocation);
                var a = Compact(o.Text); var b = Compact(n.Text);
                var kinds = new List<string>();
                if (a != b) kinds.Add(a.StartsWith(b, StringComparison.Ordinal) ? "SPLIT" : b.StartsWith(a, StringComparison.Ordinal) ? "MERGED" : "TEXT");
                else if (!StringComparer.Ordinal.Equals(o.Text, n.Text)) kinds.Add("TEXT_FORMAT");
                if (!evidence) { }
                else if (!ValidBoundary(o) || !ValidBoundary(n)) kinds.Add("BOUNDARY_UNRESOLVED");
                else if (o.HeadingSpan != n.HeadingSpan || o.InlineBodySpan != n.InlineBodySpan) kinds.Add("BOUNDARY");
                if (!evidence) { }
                else if (o.SourceParts is not { Count: > 0 } op || n.SourceParts is not { Count: > 0 } np ||
                    o.ValidatedSourcePartCount != op.Count || n.ValidatedSourcePartCount != np.Count ||
                    op.Any(p => p is null || p.Span is null || p.OriginalText is null || NewLocationOf(p.SourceId) is null) ||
                    np.Any(p => p is null || p.Span is null || p.OriginalText is null)) kinds.Add("BINDING_UNRESOLVED");
                else if (op.Count != np.Count || op.Where((p, i) => NewLocationOf(p.SourceId) != np[i].SourceId ||
                    p.Span != np[i].Span || p.OriginalText != np[i].OriginalText).Any()) kinds.Add("BINDING");
                if (o.Level != n.Level) kinds.Add("LEVEL");
                if (!oldParents.TryGetValue(location, out var oldParent) || !newParents.TryGetValue(newLocation, out var newParent) ||
                    (oldParent is not null && NewLocationOf(oldParent) is null)) kinds.Add("PARENT_UNRESOLVED");
                else if (NewLocationOf(oldParent) != newParent) kinds.Add("PARENT");
                if (Repeated(oi, ni)) kinds.Add("ALIGNMENT_UNRESOLVED");
                pairs.Add((o, n, oi, ni, kinds));
                continue;
            }
            // Not a heading start in the new outline: part of a heading that starts earlier (merged into it), or no longer a heading.
            var into = newHeadings.Values.FirstOrDefault(h => newIndex[h.StableId!] < ni && newIndex[h.StableId!] >= ni - 2
                && Compact(h.Text).Contains(Compact(o.Text), StringComparison.Ordinal));
            differences.Add(into is not null ? Item("MERGED", o, into, oi, newIndex[into.StableId!]) : Item("REMOVED", o, null, oi, ni));
            if (into is not null) handledNew.Add(into.StableId!);
        }
        foreach (var (location, n) in newHeadings)
        {
            if (handledNew.Contains(location)) continue;
            var ni = newIndex[location];
            if (!newToOld.TryGetValue(ni, out var oi)) { differences.Add(Item("ON_CHANGED_TEXT", null, n, null, ni)); continue; }
            // A new start inside the text of an old heading that starts just before: the old heading was split.
            var from = oldHeadings.Values.FirstOrDefault(h => oldIndex[h.StableId!] < oi && oldIndex[h.StableId!] >= oi - 2
                && Compact(h.Text).Contains(Compact(n.Text), StringComparison.Ordinal));
            differences.Add(from is not null ? Item("SPLIT", from, n, oldIndex[from.StableId!], ni) : Item("ADDED", null, n, oi, ni));
        }
        // A level change shared by most paired headings (a title heading added above everything) is one LEVEL_SHIFT item, not one per heading.
        var leveled = pairs.Where(p => p.O.Level is not null && p.N.Level is not null).ToList();
        var common = leveled.GroupBy(p => p.N.Level!.Value - p.O.Level!.Value).Where(g => g.Key != 0).OrderByDescending(g => g.Count()).FirstOrDefault();
        int? shift = common is not null && common.Count() >= 3 && 3 * common.Count() >= 2 * leveled.Count ? common.Key : null;
        foreach (var p in pairs)
        {
            var groupedLevel = shift is { } d && p.N.Level - p.O.Level == d && p.Kinds.Contains("LEVEL");
            var kinds = groupedLevel ? p.Kinds.Where(k => k != "LEVEL").ToList() : p.Kinds;
            if (kinds.Count == 0 && !groupedLevel && evidence) same++;
            foreach (var k in kinds) differences.Add(Item(k, p.O, p.N, p.Oi, p.Ni));
        }
        if (shift is { } delta)
            differences.Add(new("LEVEL_SHIFT", null, null, $"{common!.Count()} heading", null, 0, delta, null, null, false, false, true)
            { Details = pairs.Where(p => p.N.Level - p.O.Level == delta && p.Kinds.Contains("LEVEL"))
                .Select(p => Item("LEVEL", p.O, p.N, p.Oi, p.Ni)).ToArray() });
        if (!evidence && (old.Headings.Count > 0 || next.Headings.Count > 0))
            differences.Add(new("EVIDENCE_MISSING", null, null, oldEvidence ? null : "outline cũ không có source parts",
                newEvidence ? null : "outline mới không có source parts", null, null, null, null, false, false, true)
            { Details = pairs.Select(p => Item("BINDING_UNRESOLVED", p.O, p.N, p.Oi, p.Ni)).ToArray() });
        var changed = Math.Max(next.Paragraphs.Count - newToOld.Count, old.Paragraphs.Count - oldToNew.Count);
        var ordered = differences.OrderBy(d => d.NewLocation is { } l && newIndex.TryGetValue(l, out var i) ? i : int.MaxValue).ToList();
        // Beyond the diff limit the middle of the two files was not aligned: say so first, so no part reads as unchanged.
        if (!complete) ordered.Insert(0, new OutlineDifference("COMPARISON_INCOMPLETE", null, null, null, null, null, null, null, null, false, false, true));
        return new OutlineComparison(ordered, same, changed);
    }
}
