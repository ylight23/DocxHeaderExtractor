using DocxHeaderExtractor.DocumentProcessing.Output;

namespace DocxHeaderExtractor.DocumentProcessing.Gather;

/// <summary>
/// Offline preflight for a possible regional extraction. This does not run SELECT, GROUP or PLACE and is not wired into production.
/// A REGION is a candidate for further validation, not proof that carried headings or their parents are correct.
/// </summary>
public static class ExperimentalOutlineRegion
{
    public const int Context = 20;

    public static RegionPlan Plan(StoredOutline previous, IReadOnlyList<StoredParagraph> current, IReadOnlyList<string> unsupported,
        string? previousDependencyHash, string? currentDependencyHash, bool completeHeadingBindings)
    {
        RegionPlan Full(string reason) => new("FULL", reason, [], [], previous.Headings, 0, Math.Max(previous.Paragraphs.Count, current.Count));
        if (string.IsNullOrWhiteSpace(previousDependencyHash) || string.IsNullOrWhiteSpace(currentDependencyHash))
            return Full("DEPENDENCIES_NOT_VERIFIED");
        if (!StringComparer.Ordinal.Equals(previousDependencyHash, currentDependencyHash)) return Full("DEPENDENCIES_CHANGED");
        if (!completeHeadingBindings) return Full("HEADING_BINDINGS_INCOMPLETE");
        if (!previous.UnsupportedRegions.Order(StringComparer.Ordinal).SequenceEqual(unsupported.Order(StringComparer.Ordinal)))
            return Full("UNSUPPORTED_REGIONS_CHANGED");
        var plan = OutlineRegion.Plan(previous, current, unsupported);
        if (plan.Status != "REGION") return plan;
        var spans = new List<RegionSpan>();
        foreach (var s in plan.Spans)
        {
            var expanded = new RegionSpan(Math.Max(0, s.Start - (Context - OutlineRegion.Context)),
                Math.Min(current.Count, s.End + (Context - OutlineRegion.Context)));
            if (spans.Count > 0 && expanded.Start <= spans[^1].End) spans[^1] = new RegionSpan(spans[^1].Start, Math.Max(spans[^1].End, expanded.End));
            else spans.Add(expanded);
        }
        var position = current.Select((p, i) => (p.Location, i)).ToDictionary(x => x.Location, x => x.i, StringComparer.Ordinal);
        var oldCounts = previous.Paragraphs.GroupBy(p => p.Signature).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var newCounts = current.GroupBy(p => p.Signature).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var oldByLocation = previous.Paragraphs.ToDictionary(p => p.Location, StringComparer.Ordinal);
        var headingSignatures = previous.Headings.Where(h => h.StableId is not null && oldByLocation.ContainsKey(h.StableId))
            .Select(h => oldByLocation[h.StableId!].Signature).ToHashSet(StringComparer.Ordinal);
        var duplicates = oldCounts.Keys.Union(newCounts.Keys, StringComparer.Ordinal)
            .Where(k => oldCounts.GetValueOrDefault(k) > 1 || newCounts.GetValueOrDefault(k) > 1).ToHashSet(StringComparer.Ordinal);
        // A duplicate whose count changed, is a heading, or touches the affected region cannot be mapped uniquely by signature.
        if (duplicates.Any(k => oldCounts.GetValueOrDefault(k) != newCounts.GetValueOrDefault(k) || headingSignatures.Contains(k)) ||
            current.Select((p, i) => (p, i)).Any(x => duplicates.Contains(x.p.Signature) && spans.Any(s => s.Start <= x.i && x.i < s.End)))
            return Full("REPEATED_PARAGRAPH_ALIGNMENT_UNCERTAIN");
        bool InSpan(HeadingRecord h) => h.StableId is { } id && position.TryGetValue(id, out var i) && spans.Any(s => s.Start <= i && i < s.End);
        var toDecide = plan.HeadingsToDecide.Concat(plan.CarriedHeadings.Where(InSpan)).ToArray();
        return plan with { Spans = spans, CarriedHeadings = plan.CarriedHeadings.Where(h => !InSpan(h)).ToArray(), HeadingsToDecide = toDecide };
    }
}
