namespace DocxHeaderExtractor.Agents.Budget;

/// <summary>A task declares legal structural children; the shared planner knows nothing about heading semantics.</summary>
internal sealed record NaturalScope(int Start, int End, string Kind, IReadOnlyList<NaturalScope> Children);
internal sealed record PlannedScope(int Start, int End, string Kind);

internal static class OrderedScopePlanner
{
    public static IReadOnlyList<PlannedScope> Plan(IReadOnlyList<NaturalScope> units, int count,
        Func<int, int, int, bool> fits, int maxPackedSiblings = int.MaxValue)
    {
        if (maxPackedSiblings <= 0) throw new ArgumentOutOfRangeException(nameof(maxPackedSiblings));
        var plan = new List<PlannedScope>();
        void Visit(NaturalScope unit)
        {
            if (fits(unit.Start, unit.End, plan.Count)) { plan.Add(new(unit.Start, unit.End, unit.Kind)); return; }
            if (unit.Children.Count == 0) throw new InvalidOperationException($"UNSPLITTABLE_REQUIRED_SCOPE:{unit.Kind}:{unit.Start}:{unit.End}");
            var start = unit.Start;
            foreach (var child in unit.Children)
            {
                if (child.Start != start || child.End <= child.Start || child.End > unit.End)
                    throw new InvalidOperationException("INVALID_NATURAL_SCOPE_PARTITION");
                start = child.End;
            }
            if (start != unit.End) throw new InvalidOperationException("INVALID_NATURAL_SCOPE_PARTITION");
            // Ordered greedy lookahead over legal child ends. No binary-search monotonic-size assumption.
            for (var i = 0; i < unit.Children.Count;)
            {
                var end = i;
                var lastCandidate = Math.Min(unit.Children.Count - 1, i + maxPackedSiblings - 1);
                for (var j = lastCandidate; j >= i; j--)
                    if (fits(unit.Children[i].Start, unit.Children[j].End, plan.Count)) { end = j + 1; break; }
                if (end > i)
                {
                    plan.Add(new(unit.Children[i].Start, unit.Children[end - 1].End, unit.Children[i].Kind));
                    i = end;
                }
                else Visit(unit.Children[i++]);
            }
        }
        foreach (var unit in units) Visit(unit);
        var next = 0;
        foreach (var scope in plan)
        {
            if (scope.Start != next || scope.End <= scope.Start || scope.End > count)
                throw new InvalidOperationException("SEGMENTATION_NOT_A_PARTITION");
            next = scope.End;
        }
        if (next != count) throw new InvalidOperationException("SEGMENTATION_NOT_A_PARTITION");
        return plan;
    }
}
