namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>One non-overlapping, parser-owned character range within a source occurrence.</summary>
public sealed record LunaOwnedCharacterRange(
    string SourceOccurrenceId,
    string SourceId,
    int SourceOrdinal,
    int Start,
    int End)
{
    public int Length => End - Start;
    public string CanonicalKey => $"{SourceId}:{Start}:{End}";
}

/// <summary>A segmented model request plan. Visible ranges may overlap; owned ranges may not.</summary>
public sealed record LunaSegmentPlan(
    string SegmentId,
    int Ordinal,
    IReadOnlyList<LunaOwnedCharacterRange> VisibleRanges,
    IReadOnlyList<LunaOwnedCharacterRange> OwnedRanges,
    int VisibleCharacters,
    int OwnedCharacters)
{
    public IReadOnlyList<string> VisibleOccurrenceIds => VisibleRanges
        .Select(x => x.SourceOccurrenceId)
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    public IReadOnlyList<string> OwnedOccurrenceIds => OwnedRanges
        .Select(x => x.SourceOccurrenceId)
        .Distinct(StringComparer.Ordinal)
        .ToArray();
}

/// <summary>
/// Deterministic character ownership for Luna recovery. It never consults Gold or another
/// model. Large occurrences are split into adjacent ranges and every source character is owned
/// exactly once; visible windows are allowed to overlap around those owned ranges.
/// </summary>
public static class LunaSegmentPlanner
{
    public static IReadOnlyList<LunaSegmentPlan> Plan(
        IReadOnlyList<ReasoningSourceOccurrence> occurrences,
        int ownedCharacterBudget = 32_000,
        int visibleCharacterBudget = 48_000,
        int overlapCharacterBudget = 8_000,
        int perRangeOverhead = 0)
    {
        ArgumentNullException.ThrowIfNull(occurrences);
        if (ownedCharacterBudget < 1 || visibleCharacterBudget < ownedCharacterBudget)
            throw new ArgumentOutOfRangeException(nameof(ownedCharacterBudget));
        if (overlapCharacterBudget < 0 || overlapCharacterBudget >= visibleCharacterBudget)
            throw new ArgumentOutOfRangeException(nameof(overlapCharacterBudget));
        if (perRangeOverhead < 0) throw new ArgumentOutOfRangeException(nameof(perRangeOverhead));

        int Weight(LunaOwnedCharacterRange range) => range.Length + perRangeOverhead;

        var chunks = new List<LunaOwnedCharacterRange>();
        foreach (var occurrence in occurrences.OrderBy(x => x.SourceOrdinal))
        {
            for (var start = 0; start < occurrence.RawText.Length;)
            {
                var end = Math.Min(occurrence.RawText.Length, start + ownedCharacterBudget);
                chunks.Add(new LunaOwnedCharacterRange(occurrence.SourceOccurrenceId, occurrence.SourceId, occurrence.SourceOrdinal, start, end));
                start = end;
            }
        }
        if (chunks.Count == 0) return [];

        var ownedGroups = new List<(int Start, int End)>();
        var groupStart = 0;
        var groupCharacters = 0;
        for (var i = 0; i < chunks.Count; i++)
        {
            var length = Weight(chunks[i]);
            if (groupCharacters > 0 && groupCharacters + length > ownedCharacterBudget)
            {
                ownedGroups.Add((groupStart, i));
                groupStart = i;
                groupCharacters = 0;
            }
            groupCharacters += length;
        }
        ownedGroups.Add((groupStart, chunks.Count));

        var plans = new List<LunaSegmentPlan>(ownedGroups.Count);
        for (var groupOrdinal = 0; groupOrdinal < ownedGroups.Count; groupOrdinal++)
        {
            var group = ownedGroups[groupOrdinal];
            var visibleStart = group.Start;
            var visibleEnd = group.End;
            var visibleCharacters = chunks.Skip(visibleStart).Take(visibleEnd - visibleStart).Sum(Weight);
            while (visibleStart > 0 && visibleCharacters + Weight(chunks[visibleStart - 1]) <= visibleCharacterBudget)
            {
                visibleStart--;
                visibleCharacters += Weight(chunks[visibleStart]);
            }
            while (visibleEnd < chunks.Count && visibleCharacters + Weight(chunks[visibleEnd]) <= visibleCharacterBudget)
            {
                visibleCharacters += Weight(chunks[visibleEnd]);
                visibleEnd++;
            }

            // Keep a bounded, deterministic overlap even when a very small document would fit
            // into one request. The overlap is contextual, never owned twice.
            if (groupOrdinal > 0 && visibleStart == group.Start)
            {
                var budget = Math.Min(overlapCharacterBudget, visibleCharacterBudget - chunks.Skip(group.Start).Take(group.End - group.Start).Sum(Weight));
                for (var i = group.Start - 1; i >= 0 && budget >= Weight(chunks[i]); i--)
                {
                    visibleStart = i;
                    budget -= Weight(chunks[i]);
                }
            }
            if (groupOrdinal + 1 < ownedGroups.Count && visibleEnd == group.End)
            {
                var budget = Math.Min(overlapCharacterBudget, visibleCharacterBudget - chunks.Skip(group.Start).Take(group.End - group.Start).Sum(Weight));
                for (var i = group.End; i < chunks.Count && budget >= Weight(chunks[i]); i++)
                {
                    visibleEnd = i + 1;
                    budget -= Weight(chunks[i]);
                }
            }

            var visible = chunks.GetRange(visibleStart, visibleEnd - visibleStart);
            var owned = chunks.GetRange(group.Start, group.End - group.Start);
            plans.Add(new LunaSegmentPlan(
                $"segment-{groupOrdinal + 1:0000}-owned-{owned[0].SourceOrdinal}-{owned[^1].SourceOrdinal}",
                groupOrdinal + 1,
                visible,
                owned,
                visible.Sum(Weight),
                owned.Sum(Weight)));
        }
        return plans;
    }
}

public sealed record LunaCanonicalProposal(
    string SourceId,
    int Start,
    int End,
    string SemanticRole,
    string? ParentKey,
    double Confidence)
{
    public string CanonicalKey => $"{SourceId}:{Start}:{End}";
}

public sealed record LunaMergeResult(
    IReadOnlyList<LunaCanonicalProposal> Proposals,
    IReadOnlyList<string> Conflicts);

public static class LunaSegmentMerger
{
    public static LunaMergeResult Merge(IEnumerable<LunaCanonicalProposal> proposals)
    {
        var grouped = proposals.GroupBy(x => x.CanonicalKey, StringComparer.Ordinal);
        var merged = new List<LunaCanonicalProposal>();
        var conflicts = new List<string>();
        foreach (var group in grouped.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var semanticPayloads = group
                .GroupBy(x => $"{x.SemanticRole}|{x.ParentKey}", StringComparer.Ordinal)
                .Select(x => x.OrderByDescending(y => y.Confidence).First())
                .ToArray();
            if (semanticPayloads.Length > 1) conflicts.Add(group.Key);
            // Keep every distinct payload. A conflict is an explicit validation outcome, not a
            // license to silently select the first or highest-confidence proposal.
            merged.AddRange(semanticPayloads
                .OrderByDescending(x => x.Confidence)
                .ThenBy(x => x.SemanticRole, StringComparer.Ordinal));
        }
        return new LunaMergeResult(merged, conflicts);
    }

    public static IReadOnlyList<LunaCanonicalProposal> ResolveParents(
        IReadOnlyList<LunaCanonicalProposal> proposals,
        IReadOnlySet<string> knownKeys)
    {
        return proposals.Select(p => p with { ParentKey = p.ParentKey is not null && knownKeys.Contains(p.ParentKey) ? p.ParentKey : null }).ToArray();
    }
}

public static class LunaDocumentBarrier
{
    public static bool IsComplete(IReadOnlyList<LunaSegmentPlan> required, IReadOnlySet<string> completedSegmentIds) =>
        required.All(x => completedSegmentIds.Contains(x.SegmentId));
}
