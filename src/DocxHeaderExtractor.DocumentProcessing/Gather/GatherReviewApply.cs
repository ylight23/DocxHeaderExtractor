namespace DocxHeaderExtractor.DocumentProcessing.Gather;

/// <summary>A fact the review excluded from a section, with the review's reason, kept for the report: nothing is dropped silently.</summary>
public sealed record GatherExcluded(string Heading, GatheredFact Fact, string Reason);

/// <summary>
/// Applies a semantic review to a section before it is written. EXCLUDE removes the fact (and records it); UNSURE keeps it with a flag; a fact
/// with no verdict, or a review that failed, keeps the fact with a flag. KEEP changes nothing. The review never adds a fact.
/// </summary>
public static class GatherReviewApply
{
    public static (GatherSection Section, IReadOnlyList<GatherExcluded> Excluded) Apply(GatherSection section, GatherReviewResult review)
    {
        var verdicts = review.Verdicts.GroupBy(v => v.Index).ToDictionary(g => g.Key, g => g.First());
        var kept = new List<GatheredFact>();
        var excluded = new List<GatherExcluded>();
        for (var i = 0; i < section.Run.Facts.Count; i++)
        {
            var fact = section.Run.Facts[i];
            if (review.Error is not null) { kept.Add(fact with { Flags = [.. fact.Flags, "REVIEW_FAILED:" + review.Error] }); continue; }
            if (!verdicts.TryGetValue(i, out var verdict)) { kept.Add(fact with { Flags = [.. fact.Flags, "REVIEW_MISSING"] }); continue; }
            switch (verdict.Decision)
            {
                case "KEEP": kept.Add(fact with { Flags = fact.Flags.Where(f => f != "SCOPE_UNREVIEWED").ToArray() }); break;
                case "EXCLUDE": excluded.Add(new GatherExcluded(section.Heading, fact, verdict.Reason)); break;
                default: kept.Add(fact with { Flags = [.. fact.Flags, "REVIEW_UNSURE: " + verdict.Reason] }); break;
            }
        }
        var run = section.Run with { Facts = kept };
        return (section with { Run = run, Conflicts = GatherDocument.Conflicts(kept) }, excluded);
    }

    /// <summary>Word-token similarity at or above which a fact that differs from a target line is flagged as a possible conflict with it.</summary>
    public const double NearTargetThreshold = 0.8;

    /// <summary>A fact shorter than this (whitespace removed) counts as a repeat only when it is a whole line of the target.</summary>
    public const int MinContainedChars = 20;

    /// <summary>
    /// Compares each fact with the lines the target section already has. A fact whose text is already in a line (whitespace and case ignored) is
    /// excluded as ALREADY_IN_TARGET. A fact that is close to a line but not the same (for example another date) is kept and flagged
    /// POSSIBLE_CONFLICT with that line: a different value is information for the reader, never a duplicate to drop.
    /// </summary>
    public static (GatherSection Section, IReadOnlyList<GatherExcluded> Excluded) RemoveAlreadyInTarget(GatherSection section)
    {
        var lines = section.Goal.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var kept = new List<GatheredFact>();
        var excluded = new List<GatherExcluded>();
        foreach (var fact in section.Run.Facts)
        {
            var text = fact.Render();
            // A short fragment ("on", "the") is inside almost any line; it repeats a line only when it is that whole line.
            var compact = Compact(text);
            var same = lines.FirstOrDefault(line => Compact(line).Equals(compact, StringComparison.OrdinalIgnoreCase)
                || (compact.Length >= MinContainedChars && Compact(line).Contains(compact, StringComparison.OrdinalIgnoreCase)));
            if (same is not null)
            {
                excluded.Add(new GatherExcluded(section.Heading, fact, "ALREADY_IN_TARGET: " + Short(same)));
                continue;
            }
            var tokens = Changes.TextDiff.WordTokens(text.ToLowerInvariant());
            var near = lines.FirstOrDefault(line => Changes.DocxComparer.Dice(tokens, Changes.TextDiff.WordTokens(line.ToLowerInvariant())) >= NearTargetThreshold);
            kept.Add(near is null ? fact : fact with { Flags = [.. fact.Flags, "POSSIBLE_CONFLICT: " + Short(near)] });
        }
        return (section with { Run = section.Run with { Facts = kept }, Conflicts = GatherDocument.Conflicts(kept) }, excluded);
    }

    static string Short(string line) => line.Length > 80 ? line[..80] + "…" : line;

    static string Compact(string text) => new(text.Where(c => !char.IsWhiteSpace(c)).ToArray());

    /// <summary>
    /// A fact whose source spans are kept under more than one heading is flagged under each, naming the other headings. Neither copy is removed:
    /// which heading it belongs to is the reader's decision.
    /// </summary>
    public static IReadOnlyList<GatherSection> FlagAcrossHeadings(IReadOnlyList<GatherSection> sections)
    {
        static string Key(GatheredFact f) => string.Join("|", f.Parts.Select(p => $"{p.UnitId}:{p.Start}-{p.End}"));
        var owners = sections.SelectMany(s => s.Run.Facts.Select(f => (Key: Key(f), s.Heading)))
            .GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.Select(x => x.Heading).Distinct().ToList());
        return sections.Select(s => s with
        {
            Run = s.Run with
            {
                Facts = s.Run.Facts.Select(f => owners[Key(f)] is { Count: > 1 } heads
                    ? f with { Flags = [.. f.Flags, "ALSO_IN: " + string.Join("; ", heads.Where(h => h != s.Heading))] }
                    : f).ToList(),
            },
        }).ToList();
    }
}
