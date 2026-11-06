using DocxHeaderExtractor.DocumentProcessing.Output;

namespace DocxHeaderExtractor.DocumentProcessing.Gather;

/// <summary>A source document of a gathering run: its name (used in the unit ids), its path, and its pipeline outline when there is one.</summary>
public sealed record GatherSourceFile(string Name, string Path, IReadOnlyList<HeadingRecord>? Outline)
{
    /// <summary>The caller has not confirmed this exact source outline; retain its contents for review instead of matching.</summary>
    public bool OutlineReviewRequired { get; init; }
    /// <summary>False when extraction did not decide every source unit; that source must not be placed automatically.</summary>
    public bool SelectionComplete { get; init; } = true;
}

/// <summary>
/// The result for one heading of the target: the goal (the heading and the text under it), the run, and the conflicts among the kept facts. The
/// facts are not chosen between: a conflict keeps every value and says so.
/// </summary>
/// <param name="NextHeadingLocation">
/// The location of the next heading after this heading's text (a sub-heading or the next section). The result goes before it, after the direct
/// content of this heading. Null when there is no later heading.
/// </param>
public sealed record GatherSection(string TargetLocation, string Heading, string Goal, GatherRun Run, IReadOnlyList<GatherConflict> Conflicts,
    string? NextHeadingLocation = null)
{
    /// <summary>Why the section has no facts when that is not a failure, for the report (for example: no source heading has the same name).</summary>
    public string? Note { get; init; }

    /// <summary>
    /// Set when the section is a heading the target does not have: it is written as a new heading (depth 1 directly under its parent in the target,
    /// 2 under another new heading, and so on) before <see cref="NextHeadingLocation"/>, the end of its parent's subtree, or at the end of the document.
    /// </summary>
    public int? NewHeadingDepth { get; init; }

    /// <summary>For a new heading: the heading it is placed under (in the target, or another new one); null at the top of the document.</summary>
    public string? NewHeadingParent { get; init; }

    /// <summary>For a new heading: the source and place it was taken from.</summary>
    public string? NewHeadingSource { get; init; }
}

/// <summary>Kept facts that give the same label different values. Each value is kept; the reader decides which holds.</summary>
public sealed record GatherConflict(string Label, IReadOnlyList<int> FactIndexes, IReadOnlyList<string> Values);

/// <summary>
/// Runs gathering for every heading of the target. The goal of a heading is its text plus the text directly under it, so the model sees what the
/// section already says. Sources are read once. Calls no model itself; it passes the model to <see cref="GatherRunner"/>.
/// </summary>
public static class GatherDocument
{
    public static async Task<IReadOnlyList<GatherSection>> RunAsync(string targetPath, IReadOnlyList<HeadingRecord> targetOutline,
        IReadOnlyList<GatherSourceFile> sources, IGatherModel model, CancellationToken ct = default, GatherCheckpointStore? checkpoints = null,
        Func<HeadingRecord, bool>? include = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentNullException.ThrowIfNull(targetOutline);
        ArgumentNullException.ThrowIfNull(sources);
        var target = GatherUnits.Read(targetPath, "target", targetOutline);
        var read = sources.Select(s => GatherUnits.Read(s.Path, s.Name, s.Outline)).ToList();
        var targetBoundaryInvalid = target.Gaps.Any(g => g.Reason == "heading_boundary_unverified" ||
            g.Reason.StartsWith("heading_unmatched:", StringComparison.Ordinal) ||
            g.Reason.StartsWith("heading_located_by_text:", StringComparison.Ordinal));
        var sections = new List<GatherSection>();
        var cursor = 0;
        foreach (var heading in targetOutline.Where(h => h.Level is not null).OrderBy(h => h.Index))
        {
            ct.ThrowIfCancellationRequested();
            // A heading left out by the caller is still matched, so the next heading is searched for after it.
            var headingText = heading.Text.Trim();
            var at = FindHeading(target.Units, cursor, headingText);
            if (include is not null && !include(heading))
            {
                if (at >= 0) cursor = HeadingEnd(target.Units, at, headingText) + 1;
                continue;
            }
            var location = "";
            string? next = null;
            var under = Array.Empty<string>();
            if (at >= 0)
            {
                location = target.Units[at].Location;
                var last = HeadingEnd(target.Units, at, headingText);
                cursor = last + 1;
                // The text of this heading is the units from its end up to the next heading, by position: a heading with the same text elsewhere has its own.
                var nextIndex = NextHeadingIndex(target.Units, last + 1);
                next = nextIndex < 0 ? null : target.Units[nextIndex].Location;
                // Content written in the heading's own paragraph ("Opening: The meeting was called…") is part of the section, before the paragraphs after it.
                var inline = target.Units.Skip(at).Take(last - at + 1).Select(u => u.InlineBody).Where(t => t.Length > 0);
                under = inline.Concat(target.Units.Skip(last + 1).Take((nextIndex < 0 ? target.Units.Count : nextIndex) - last - 1)
                    .Where(u => !u.IsHeading).Select(u => u.Text.Trim())).ToArray();
            }
            var goal = string.Join("\n", new[] { headingText }.Concat(under));
            var run = targetBoundaryInvalid
                ? new GatherRun([], [], [], ["TARGET_BOUNDARY_UNVERIFIED"], read.SelectMany(s => s.Units).Select(u => u.Id).ToArray(),
                    target.Gaps.Concat(read.SelectMany(s => s.Gaps)).ToArray(), 0)
                : await GatherRunner.RunAsync(goal, read, model, ct, checkpoints).ConfigureAwait(false);
            if (!targetBoundaryInvalid) run = run with { Gaps = run.Gaps.Concat(target.Gaps).ToArray() };
            sections.Add(new GatherSection(location, headingText, goal, run, Conflicts(run.Facts), next));
        }
        return sections;
    }

    internal static int NextHeadingIndex(IReadOnlyList<GatherUnit> units, int from)
    {
        for (var i = from; i < units.Count; i++)
            if (units[i].IsHeading) return i;
        return -1;
    }

    /// <summary>The index of the first heading unit from <paramref name="from"/> whose text is the start of the heading's text (or the reverse), or -1.</summary>
    internal static int FindHeading(IReadOnlyList<GatherUnit> units, int from, string text)
    {
        var target = Compact(text);
        for (var i = from; i < units.Count; i++)
        {
            if (!units[i].IsHeading) continue;
            var unit = Compact(units[i].Text);
            if (unit.Length > 0 && (target.StartsWith(unit, StringComparison.Ordinal) || unit.StartsWith(target, StringComparison.Ordinal))) return i;
        }
        return -1;
    }

    /// <summary>The last heading unit of a heading that runs over several units (the same rule the writeback uses to find its end).</summary>
    internal static int HeadingEnd(IReadOnlyList<GatherUnit> units, int first, string text)
    {
        var target = Compact(text);
        var joined = Compact(units[first].Text);
        var last = first;
        while (joined.Length < target.Length && last + 1 < units.Count && units[last + 1].IsHeading && target.StartsWith(joined, StringComparison.Ordinal))
        {
            var candidate = joined + Compact(units[last + 1].Text);
            if (candidate.StartsWith(target, StringComparison.Ordinal)) return last + 1;
            if (!target.StartsWith(candidate, StringComparison.Ordinal)) break;
            joined = candidate;
            last++;
        }
        return last;
    }

    static string Compact(string raw) => new(raw.Where(c => !char.IsWhiteSpace(c)).ToArray());

    /// <summary>
    /// The table that ends the document body, when the last element with text is a body-level table (in administrative documents, the signature
    /// and recipients block). Null when the body ends with a paragraph.
    /// </summary>
    /// <summary>The location prefix ("body[1]/tbl[2]/") of the units inside the <see cref="EndTable"/> of a file, or null when it has none.</summary>
    internal static string? EndTablePrefix(string path)
    {
        using var document = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(path, isEditable: false);
        var body = document.MainDocumentPart?.Document?.Body;
        if (body is null || EndTable(body) is not { } table) return null;
        var number = body.Elements<DocumentFormat.OpenXml.Wordprocessing.Table>().TakeWhile(t => !ReferenceEquals(t, table)).Count() + 1;
        return $"body[1]/tbl[{number}]/";
    }

    internal static DocumentFormat.OpenXml.Wordprocessing.Table? EndTable(DocumentFormat.OpenXml.Wordprocessing.Body body) =>
        body.ChildElements.LastOrDefault(e => e is not DocumentFormat.OpenXml.Wordprocessing.SectionProperties && e.InnerText.Trim().Length > 0)
            as DocumentFormat.OpenXml.Wordprocessing.Table;

    /// <summary>
    /// Kept facts whose label is the same and whose value differs. The label is compared without case or whitespace. A fact without a label is never a conflict.
    /// </summary>
    public static IReadOnlyList<GatherConflict> Conflicts(IReadOnlyList<GatheredFact> facts)
    {
        var groups = new Dictionary<string, (string Label, List<int> Facts)>(StringComparer.Ordinal);
        for (var i = 0; i < facts.Count; i++)
        {
            var label = facts[i].Parts.FirstOrDefault(p => p.Role == "label")?.Text;
            if (label is null) continue;
            var key = string.Concat(label.Where(c => !char.IsWhiteSpace(c))).ToLowerInvariant();
            if (!groups.TryGetValue(key, out var group)) groups[key] = group = (label, []);
            group.Facts.Add(i);
        }
        var conflicts = new List<GatherConflict>();
        foreach (var (_, group) in groups)
        {
            if (group.Facts.Count < 2) continue;
            var values = group.Facts.Select(i => facts[i].Parts.FirstOrDefault(p => p.Role == "value")?.Text ?? "").ToList();
            if (values.Distinct(StringComparer.Ordinal).Count() > 1)
                conflicts.Add(new GatherConflict(group.Label, group.Facts, values));
        }
        return conflicts;
    }
}
