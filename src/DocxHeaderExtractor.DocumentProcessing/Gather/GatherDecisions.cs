namespace DocxHeaderExtractor.DocumentProcessing.Gather;

/// <summary>
/// The reader's decision on one uncertain section (<see cref="GatherHeadingPlan.Uncertain"/>, by index): MERGE into a candidate (its index in
/// <see cref="GatherUncertain.Candidates"/>), NEW (a heading of its own), or SKIP (not written). A section without a decision stays unwritten.
/// </summary>
public sealed record GatherDecision(int Index, string Decision, int? Candidate = null);

/// <summary>What one decision did: the facts written, and the facts not written because the heading already had the same text.</summary>
public sealed record GatherDecisionOutcome(int Index, string Decision, string? Heading, int Written, IReadOnlyList<string> AlreadyThere, string? Error);

public sealed record GatherDecisionResult(IReadOnlyList<GatherSection> Sections, IReadOnlyList<GatherDecisionOutcome> Outcomes, IReadOnlyList<int> Undecided);

/// <summary>
/// Applies the reader's decisions on uncertain sections to the sections decided by the heading chain. Only a person's decision is applied here: a
/// model's classification is advice shown to that person and is never passed in by the code. Every written fact carries the decision as a flag.
/// </summary>
public static class GatherDecisions
{
    public static GatherDecisionResult Apply(IReadOnlyList<GatherSection> decided, IReadOnlyList<GatherUncertain> uncertain,
        IReadOnlyList<GatherDecision> decisions)
    {
        ArgumentNullException.ThrowIfNull(decided);
        ArgumentNullException.ThrowIfNull(uncertain);
        ArgumentNullException.ThrowIfNull(decisions);
        var sections = decided.ToList();
        var outcomes = new List<GatherDecisionOutcome>();
        var gaps = decided.FirstOrDefault()?.Run.Gaps ?? [];
        var byIndex = new Dictionary<int, GatherDecision>();
        foreach (var d in decisions)
        {
            if (d.Index < 0 || d.Index >= uncertain.Count || !byIndex.TryAdd(d.Index, d))
                outcomes.Add(new(d.Index, d.Decision, null, 0, [], "INVALID_INDEX"));
        }
        var newHeadings = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (index, d) in byIndex.OrderBy(x => x.Key))
        {
            var u = uncertain[index];
            if ((u.Kind is "UNCONFIRMED_HIERARCHY" or "SOURCE_OUTLINE_REVIEW_REQUIRED" or "TARGET_OUTLINE_INCOMPLETE") && d.Decision != "SKIP")
            {
                outcomes.Add(new(index, d.Decision, null, 0, [], "OUTLINE_REVIEW_REQUIRED"));
                continue;
            }
            switch (d.Decision)
            {
                case "SKIP":
                    outcomes.Add(new(index, "SKIP", null, 0, [], null));
                    break;
                case "MERGE" when d.Candidate is { } c && c >= 0 && c < u.Candidates.Count:
                {
                    var candidate = u.Candidates[c];
                    var at = sections.FindIndex(s => s.NewHeadingDepth is null && s.TargetLocation == candidate.Location);
                    if (at < 0) { outcomes.Add(new(index, "MERGE", candidate.Heading, 0, [], "CANDIDATE_NOT_FOUND")); break; }
                    var section = sections[at];
                    var present = candidate.Content.Concat(section.Run.Facts.Select(f => f.Render())).Select(Key).ToHashSet(StringComparer.Ordinal);
                    var (keep, there) = Split(u.Facts, present);
                    var flagged = keep.Select(f => f with { Flags = [.. f.Flags, $"USER_DECISION: gộp từ \"{u.SourceHeading}\" ({u.Source})"] });
                    var facts = section.Run.Facts.Concat(flagged).ToList();
                    sections[at] = section with { Run = section.Run with { Facts = facts }, Conflicts = GatherDocument.Conflicts(facts) };
                    outcomes.Add(new(index, "MERGE", candidate.Heading, keep.Count, there, null));
                    break;
                }
                case "MERGE":
                    outcomes.Add(new(index, "MERGE", null, 0, [], "INVALID_CANDIDATE"));
                    break;
                case "NEW":
                {
                    if (u.ProposedParentIndex is { } parentIndex && !outcomes.Any(o => o.Index == parentIndex && o.Decision == "NEW" && o.Error is null))
                    {
                        outcomes.Add(new(index, "NEW", null, 0, [], "PARENT_DECISION_REQUIRED"));
                        break;
                    }
                    var facts = u.Facts.Select(f => f with { Flags = [.. f.Flags.Where(x => x != "SCOPE_UNREVIEWED"), "USER_DECISION: heading mới"] }).ToList();
                    // A child whose parent the reader also made a new heading is written right after it, one level deeper.
                    var depth = u.ProposedNewDepth ?? (u.SourceParent is not null && newHeadings.Contains(u.SourceParent) ? 2 : 1);
                    newHeadings.Add(u.SourceHeading);
                    sections.Add(new GatherSection("", u.SourceHeading, u.SourceHeading, new GatherRun(facts, [], [], [], [], gaps, 1),
                        GatherDocument.Conflicts(facts), u.NewHeadingNextLocation)
                    {
                        NewHeadingDepth = depth,
                        NewHeadingParent = depth == 2 ? u.SourceParent : u.NewHeadingParent,
                        NewHeadingSource = u.Source,
                    });
                    outcomes.Add(new(index, "NEW", u.SourceHeading, facts.Count, [], null));
                    break;
                }
                default:
                    outcomes.Add(new(index, d.Decision, null, 0, [], "UNKNOWN_DECISION"));
                    break;
            }
        }
        var undecided = Enumerable.Range(0, uncertain.Count)
            .Where(i => !outcomes.Any(o => o.Index == i && o.Error is null)).ToList();
        return new GatherDecisionResult(sections, outcomes, undecided);
    }

    static (List<GatheredFact> Keep, List<string> There) Split(IReadOnlyList<GatheredFact> facts, HashSet<string> present)
    {
        var keep = new List<GatheredFact>();
        var there = new List<string>();
        foreach (var f in facts)
        {
            var text = f.Render();
            if (present.Add(Key(text))) keep.Add(f);
            else there.Add(text);
        }
        return (keep, there);
    }

    /// <summary>
    /// The facts of an uncertain section whose text is already in the direct content of one candidate, compared after Unicode NFC and with runs of
    /// white space made one space (the same comparison as <see cref="Apply"/>). Evidence for the reader only: whether the candidate is the
    /// corresponding section is not established by this (two sections of one name under different parents may be distinct), so nothing is removed
    /// or written because of it. It is not a forecast of a MERGE: a MERGE also leaves out text already received from another source and repeats
    /// inside the section itself.
    /// </summary>
    public static IReadOnlyList<int> SameAsCandidate(GatherUncertain uncertain, int candidate)
    {
        ArgumentNullException.ThrowIfNull(uncertain);
        if (candidate < 0 || candidate >= uncertain.Candidates.Count) return [];
        var present = uncertain.Candidates[candidate].Content.Select(Key).ToHashSet(StringComparer.Ordinal);
        return Enumerable.Range(0, uncertain.Facts.Count).Where(i => present.Contains(Key(uncertain.Facts[i].Render()))).ToList();
    }

    static string Key(string text) => string.Join(' ', text.Normalize(System.Text.NormalizationForm.FormC).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
