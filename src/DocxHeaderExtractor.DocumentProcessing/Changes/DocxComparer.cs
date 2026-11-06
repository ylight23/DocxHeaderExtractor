using DocxHeaderExtractor.Sources.Docx.Changes;

namespace DocxHeaderExtractor.DocumentProcessing.Changes;

/// <summary>One side of a change: the paragraph's walker location and order, its raw text, and the span inside it when there is one.</summary>
public sealed record ChangeSide(string Location, int Ordinal, string Text, TextSpan? Span);

/// <summary>
/// One difference between the template and the document. <see cref="Kind"/>: paragraph_only_in_template, paragraph_only_in_document,
/// paragraph_split, paragraph_merged, text_modified, text_inserted, text_deleted, paired_uncertain. <see cref="MatchStatus"/>: matched (same
/// matching key), paired_by_similarity, unmatched. <see cref="Uncertain"/> marks a pairing or an absence another reading could explain.
/// <see cref="Note"/> carries labels separated by ';': whitespace_or_unicode_only, possible_split, possible_merge, spans_paragraph_boundary.
/// A split or merge lists every paragraph of the grouped side in <see cref="TemplateGroup"/> or <see cref="DocumentGroup"/>.
/// </summary>
public sealed record Change(string Kind, string MatchStatus, bool Uncertain, string? Note, ChangeSide? Template, ChangeSide? Document,
    string Heading = "unknown", IReadOnlyList<ChangeSide>? TemplateGroup = null, IReadOnlyList<ChangeSide>? DocumentGroup = null)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<FormatChange>? FormatChanges { get; init; }
}

/// <summary>
/// Result of comparing a document with a template. Unsupported regions of each side are listed so that nothing unread is presented as unchanged.
/// <see cref="Scope"/> states what was compared.
/// </summary>
public sealed record ComparisonResult(string TemplateSha256, string DocumentSha256, IReadOnlyList<Change> Changes,
    IReadOnlyList<string> TemplateUnsupported, IReadOnlyList<string> DocumentUnsupported, string Scope,
    string GroupRule, bool FlagRejectedGroups)
{
    /// <summary>Optional offline candidates; original changes are preserved and movement direction is not inferred.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ReorderAnalysis? Reorders { get; init; }
}

/// <summary>
/// How a split or merge candidate is accepted. <see cref="ExplainsLeftover"/> (default in experimental P1): the weaker part must add, in order,
/// matched tokens worth at least <see cref="DocxComparer.PartContainment"/> of its own length. <see cref="Advantage"/> (kept for comparison):
/// the joined parts must beat the better one-to-one pairing by <see cref="DocxComparer.GroupAdvantage"/>, which rejects short parts.
/// Measured in tools/qualification/DocxPairingEval.
/// </summary>
public enum GroupRule { Advantage, ExplainsLeftover }

/// <param name="FlagRejectedGroups">When a split or merge candidate was rejected but the chosen result looks like that split or merge (the whole
/// is one-sided or paired with one part, and the other part is one-sided), mark those paragraphs uncertain with the rejection reason.</param>
public sealed record ComparerOptions(GroupRule GroupRule = GroupRule.ExplainsLeftover, bool FlagRejectedGroups = true, bool DetectReorders = false, bool CompareDirectFormatting = false);

/// <summary>
/// Diagnostic record of one split or merge candidate whose joined similarity reached the pair threshold: its scores, the token gain of the
/// weaker part, and why it was accepted or rejected (accepted, part_not_contained, no_advantage, leftover_not_explained, empty_part).
/// </summary>
public sealed record GroupDecision(string Kind, string WholeLocation, IReadOnlyList<string> PartLocations, double FirstPair, double SecondPair,
    double Joined, double FirstContainment, double SecondContainment, int Gain, bool Accepted, string Reason);

/// <summary>
/// P1 (experimental): pairs paragraphs and diffs their text. Deterministic code, no model call. Paragraphs are first aligned by Myers on their
/// matching keys. Each block left unaligned is then aligned as a whole, in order, by dynamic programming over one-to-one pairs, splits (one
/// template paragraph to two document paragraphs) and merges (two to one), so an early choice cannot lock out a better later one.
/// Formatting is not compared here (P2).
/// </summary>
public static class DocxComparer
{
    /// <summary>Minimum token similarity to pair paragraphs; to be measured on real pairs.</summary>
    public const double PairThreshold = 0.5;
    /// <summary>A rival candidate this close to the chosen one makes a one-to-one pairing uncertain.</summary>
    public const double RivalMargin = 0.1;
    /// <summary>A split or merge must beat the best one-to-one pairing of its parts by this much.</summary>
    public const double GroupAdvantage = 0.05;
    /// <summary>Each part of a split or merge must have at least this share of its tokens in the paragraph on the other side.</summary>
    public const double PartContainment = 0.5;
    /// <summary>Blocks larger than this (template × document paragraphs) are not paired; their paragraphs are reported one-sided and uncertain.</summary>
    public const int MaxBlockCells = 250_000;
    public const string Scope = "text_of_paragraphs_read_by_docx_version_reader; formatting_not_compared";

    public static ComparisonResult Compare(string templatePath, string documentPath) =>
        Compare(DocxVersionReader.Read(templatePath), DocxVersionReader.Read(documentPath));

    /// <param name="options">Grouping rule; the default keeps the current rule.</param>
    /// <param name="decisions">When given, receives every split or merge candidate that reached the pair threshold, with its scores and reason.</param>
    public static ComparisonResult Compare(DocxVersion template, DocxVersion document, ComparerOptions? options = null, List<GroupDecision>? decisions = null)
    {
        options ??= new ComparerOptions();
        var changes = new List<Change>();
        var context = new Context(
            template.Paragraphs.Select(p => p.KeyHash).ToHashSet(StringComparer.Ordinal),
            document.Paragraphs.Select(p => p.KeyHash).ToHashSet(StringComparer.Ordinal),
            changes, options, decisions);
        // The similarity helpers report an incomplete token diff into this comparison, whichever step calls them.
        var outer = CurrentAlignment.Value;
        CurrentAlignment.Value = context.Alignment;
        try { return CompareIn(template, document, options, context, changes); }
        finally { CurrentAlignment.Value = outer; }
    }

    /// <summary>The alignment state of the comparison running in this async flow, if any.</summary>
    static readonly AsyncLocal<AlignmentState?> CurrentAlignment = new();

    static ComparisonResult CompareIn(DocxVersion template, DocxVersion document, ComparerOptions options, Context context, List<Change> changes)
    {

        // Units: each body paragraph, and each top-level table as a whole (P1b). Without tables this is the paragraph alignment of P1.
        var a = TableDiff.Units(template.Paragraphs);
        var b = TableDiff.Units(document.Paragraphs);
        var deleted = new List<object>();
        var inserted = new List<object>();
        var aligned = Myers.Diff(a, b, TableDiff.SameUnit, Myers.Limit, out var complete);
        if (!complete) context.Alignment.Incomplete = true;
        foreach (var edit in aligned)
        {
            switch (edit.Kind)
            {
                case EditKind.Equal:
                    Flush(deleted, inserted, context);
                    if (a[edit.OldIndex] is ParagraphFingerprint p) Matched(p, (ParagraphFingerprint)b[edit.NewIndex], "matched", false, changes, compareFormatting: options.CompareDirectFormatting);
                    else TableDiff.CompareTables((TableShape)a[edit.OldIndex], (TableShape)b[edit.NewIndex], "matched", false, context);
                    break;
                case EditKind.Delete: deleted.Add(a[edit.OldIndex]); break;
                case EditKind.Insert: inserted.Add(b[edit.NewIndex]); break;
            }
        }
        Flush(deleted, inserted, context);
        // An alignment that stopped at the diff limit: its unpaired blocks went through the pairing steps on partial evidence. Every change is then
        // marked for review (not only the middle: a block pairing can reach any part), and the scope says the comparison is partial.
        var incomplete = context.Alignment.Incomplete;
        var reported = incomplete ? changes.Select(c => c with { Uncertain = true, Note = Join(c.Note, "alignment_incomplete") }).ToList() : changes;
        return new ComparisonResult(template.FileSha256, document.FileSha256, reported, template.UnsupportedRegions, document.UnsupportedRegions,
            (options.CompareDirectFormatting ? "text_and_selected_direct_formatting; effective_formatting_and_unread_xml_not_compared; run_formatting_only_for_identical_text" : Scope)
                + (incomplete ? "; alignment_incomplete_diff_limit_exceeded" : ""),
            options.GroupRule.ToString(), options.FlagRejectedGroups)
        { Reorders = options.DetectReorders ? ReorderDetector.Analyze(template, document) : null };
    }

    /// <summary>What one comparison carries through its steps: both documents' matching keys, the change list, the options and the decision log.</summary>
    internal sealed record Context(HashSet<string> TemplateKeys, HashSet<string> DocumentKeys, List<Change> Changes, ComparerOptions Options,
        List<GroupDecision>? Decisions)
    {
        /// <summary>Set when any alignment of this comparison stopped at the diff limit.</summary>
        public AlignmentState Alignment { get; } = new();
    }

    internal sealed class AlignmentState { public bool Incomplete; }

    /// <summary>One unaligned block of units: its paragraphs are aligned by <see cref="Block"/>, its tables by <see cref="TableDiff.Block"/>.</summary>
    static void Flush(List<object> deleted, List<object> inserted, Context context)
    {
        if (deleted.Count == 0 && inserted.Count == 0) return;
        Block(deleted.OfType<ParagraphFingerprint>().ToArray(), inserted.OfType<ParagraphFingerprint>().ToArray(), context);
        TableDiff.Block(deleted.OfType<TableShape>().ToList(), inserted.OfType<TableShape>().ToList(), context);
        deleted.Clear();
        inserted.Clear();
    }

    /// <summary>The P1 paragraph alignment of two paragraph sequences (used for the paragraphs inside a pair of table cells).</summary>
    internal static void CompareSequence(IReadOnlyList<ParagraphFingerprint> a, IReadOnlyList<ParagraphFingerprint> b, Context context)
    {
        var deleted = new List<ParagraphFingerprint>();
        var inserted = new List<ParagraphFingerprint>();
        var edits = Myers.Diff(a, b, (x, y) => x.KeyHash == y.KeyHash, Myers.Limit, out var complete);
        if (!complete) context.Alignment.Incomplete = true;
        foreach (var edit in edits)
        {
            switch (edit.Kind)
            {
                case EditKind.Equal:
                    Block(deleted.ToArray(), inserted.ToArray(), context);
                    deleted.Clear();
                    inserted.Clear();
                    Matched(a[edit.OldIndex], b[edit.NewIndex], "matched", false, context.Changes, compareFormatting: context.Options.CompareDirectFormatting);
                    break;
                case EditKind.Delete: deleted.Add(a[edit.OldIndex]); break;
                case EditKind.Insert: inserted.Add(b[edit.NewIndex]); break;
            }
        }
        Block(deleted.ToArray(), inserted.ToArray(), context);
    }

    enum Step { SkipTemplate, SkipDocument, Pair, Split, Merge }

    /// <summary>Aligns one unaligned block of paragraphs as a whole and emits its changes in order.</summary>
    static void Block(ParagraphFingerprint[] t, ParagraphFingerprint[] d, Context context)
    {
        if (t.Length == 0 && d.Length == 0) return;
        var (templateKeys, documentKeys, changes, options, decisions) = (context.TemplateKeys, context.DocumentKeys, context.Changes, context.Options, context.Decisions);
        int n = t.Length, m = d.Length;
        if ((long)n * m > MaxBlockCells)
        {
            foreach (var p in t) OnlyIn(p, null, documentKeys, changes, forceUncertain: true);
            foreach (var p in d) OnlyIn(null, p, templateKeys, changes, forceUncertain: true);
            return;
        }

        var tTokens = t.Select(p => TextDiff.WordTokens(p.NormalizedKey)).ToArray();
        var dTokens = d.Select(p => TextDiff.WordTokens(p.NormalizedKey)).ToArray();
        var sim = new double[n, m];
        for (var i = 0; i < n; i++)
            for (var j = 0; j < m; j++)
                sim[i, j] = Dice(tTokens[i], dTokens[j]);

        var score = new double[n + 1, m + 1];
        var step = new Step[n + 1, m + 1];
        // Candidates that reached the pair threshold but were rejected: (kind, whole index, first part index, second part index, reason).
        var rejected = new List<(string Kind, int Whole, int First, int Second, string Reason)>();
        for (var i = 0; i <= n; i++)
            for (var j = 0; j <= m; j++)
            {
                if (i == 0 && j == 0) continue;
                var best = double.NegativeInfinity;
                void Offer(double value, Step s) { if (value > best) { best = value; step[i, j] = s; } }
                if (i > 0) Offer(score[i - 1, j], Step.SkipTemplate);
                if (j > 0) Offer(score[i, j - 1], Step.SkipDocument);
                if (i > 0 && j > 0 && sim[i - 1, j - 1] >= PairThreshold) Offer(score[i - 1, j - 1] + sim[i - 1, j - 1], Step.Pair);
                if (i > 0 && j > 1)
                {
                    var (split, why) = Group("split", t[i - 1], d[j - 2], d[j - 1], tTokens[i - 1], dTokens[j - 2], dTokens[j - 1],
                        sim[i - 1, j - 2], sim[i - 1, j - 1], options, decisions);
                    if (split >= 0) Offer(score[i - 1, j - 2] + 1.5 * split, Step.Split);
                    else if (why is not null) rejected.Add(("split", i - 1, j - 2, j - 1, why));
                }
                if (i > 1 && j > 0)
                {
                    var (merge, why) = Group("merge", d[j - 1], t[i - 2], t[i - 1], dTokens[j - 1], tTokens[i - 2], tTokens[i - 1],
                        sim[i - 2, j - 1], sim[i - 1, j - 1], options, decisions);
                    if (merge >= 0) Offer(score[i - 2, j - 1] + 1.5 * merge, Step.Merge);
                    else if (why is not null) rejected.Add(("merge", j - 1, i - 2, i - 1, why));
                }
                score[i, j] = best;
            }

        var path = new List<(Step Step, int I, int J)>();
        for (int i = n, j = m; i > 0 || j > 0;)
        {
            var s = step[i, j];
            path.Add((s, i, j));
            (i, j) = s switch
            {
                Step.SkipTemplate => (i - 1, j),
                Step.SkipDocument => (i, j - 1),
                Step.Pair => (i - 1, j - 1),
                Step.Split => (i - 1, j - 2),
                _ => (i - 2, j - 1),
            };
        }
        path.Reverse();

        var (flagT, flagD) = options.FlagRejectedGroups ? RejectedGroupFlags(path, rejected) : (new Dictionary<int, string>(), new Dictionary<int, string>());
        foreach (var (s, i, j) in path)
        {
            switch (s)
            {
                case Step.SkipTemplate: OnlyIn(t[i - 1], null, documentKeys, changes, flagNote: flagT.GetValueOrDefault(i - 1)); break;
                case Step.SkipDocument: OnlyIn(null, d[j - 1], templateKeys, changes, flagNote: flagD.GetValueOrDefault(j - 1)); break;
                case Step.Pair:
                {
                    var chosen = sim[i - 1, j - 1];
                    var rival = Enumerable.Range(0, m).Any(x => x != j - 1 && chosen - sim[i - 1, x] < RivalMargin)
                        || Enumerable.Range(0, n).Any(y => y != i - 1 && chosen - sim[y, j - 1] < RivalMargin);
                    Matched(t[i - 1], d[j - 1], "paired_by_similarity", rival, changes, flagT.GetValueOrDefault(i - 1) ?? flagD.GetValueOrDefault(j - 1), options.CompareDirectFormatting);
                    break;
                }
                case Step.Split: Grouped("paragraph_split", "possible_split", [t[i - 1]], [d[j - 2], d[j - 1]], changes); break;
                case Step.Merge: Grouped("paragraph_merged", "possible_merge", [t[i - 2], t[i - 1]], [d[j - 1]], changes); break;
            }
        }
    }

    /// <summary>
    /// The paragraphs to mark uncertain because a rejected split or merge looks like what the chosen result shows: the whole is one-sided or
    /// paired with one of the parts, and at least one part is one-sided. Only the whole, its partner and the one-sided parts are marked; other
    /// rejected candidates leave no mark. The note names the rejection, e.g. possible_merge_rejected:leftover_not_explained.
    /// </summary>
    static (Dictionary<int, string> Template, Dictionary<int, string> Document) RejectedGroupFlags(
        List<(Step Step, int I, int J)> path, List<(string Kind, int Whole, int First, int Second, string Reason)> rejected)
    {
        var pairT = new Dictionary<int, int>();
        var pairD = new Dictionary<int, int>();
        var oneT = new HashSet<int>();
        var oneD = new HashSet<int>();
        foreach (var (s, i, j) in path)
        {
            if (s == Step.Pair) { pairT[i - 1] = j - 1; pairD[j - 1] = i - 1; }
            else if (s == Step.SkipTemplate) oneT.Add(i - 1);
            else if (s == Step.SkipDocument) oneD.Add(j - 1);
        }
        var flagT = new Dictionary<int, string>();
        var flagD = new Dictionary<int, string>();
        foreach (var (kind, whole, first, second, reason) in rejected)
        {
            var note = $"possible_{kind}_rejected:{reason}";
            // For a split the whole is a template paragraph and the parts are document paragraphs; for a merge the other way round.
            var (wholeOne, wholePair, partOne, wholeFlags, partFlags) = kind == "split"
                ? (oneT, pairT, oneD, flagT, flagD)
                : (oneD, pairD, oneT, flagD, flagT);
            var partner = wholePair.TryGetValue(whole, out var p) ? p : -1;
            var wholeLikeGroup = wholeOne.Contains(whole) || partner == first || partner == second;
            var partLeftOver = partOne.Contains(first) || partOne.Contains(second);
            if (!wholeLikeGroup || !partLeftOver) continue;
            wholeFlags.TryAdd(whole, note);
            if (partner >= 0) partFlags.TryAdd(partner, note);
            if (partOne.Contains(first)) partFlags.TryAdd(first, note);
            if (partOne.Contains(second)) partFlags.TryAdd(second, note);
        }
        return (flagT, flagD);
    }

    /// <summary>
    /// Score of grouping two consecutive paragraphs (<paramref name="first"/>, <paramref name="second"/>) against one paragraph
    /// (<paramref name="whole"/>) under the chosen rule, or -1. The reason is null when the candidate is not plausible at all (an empty part or
    /// below the pair threshold), else the rejection reason, or null with a score when accepted.
    /// </summary>
    static (double Score, string? Reason) Group(string kind, ParagraphFingerprint wholeParagraph, ParagraphFingerprint firstParagraph, ParagraphFingerprint secondParagraph,
        string[] whole, string[] first, string[] second, double firstPair, double secondPair, ComparerOptions options, List<GroupDecision>? decisions)
    {
        if (first.Length == 0 || second.Length == 0) return (-1, null);
        var parts = first.Concat(second).ToArray();
        var value = Dice(whole, parts);
        if (value < PairThreshold) return (-1, null);

        var firstContained = (double)Common(first, whole) / first.Length;
        var secondContained = (double)Common(second, whole) / second.Length;
        // Tokens the weaker part adds, in order, on top of what the stronger part already matches.
        var firstStronger = firstPair >= secondPair;
        var gain = Common(whole, parts) - Common(whole, firstStronger ? first : second);
        var weakerLength = firstStronger ? second.Length : first.Length;

        var reason =
            firstContained < PartContainment || secondContained < PartContainment ? "part_not_contained" :
            options.GroupRule == GroupRule.Advantage && value < Math.Max(firstPair, secondPair) + GroupAdvantage ? "no_advantage" :
            options.GroupRule == GroupRule.ExplainsLeftover && gain < PartContainment * weakerLength ? "leftover_not_explained" :
            "accepted";
        decisions?.Add(new GroupDecision(kind, wholeParagraph.Location, [firstParagraph.Location, secondParagraph.Location],
            firstPair, secondPair, value, firstContained, secondContained, gain, reason == "accepted", reason));
        return reason == "accepted" ? (value, null) : (-1, reason);
    }

    /// <summary>
    /// A paragraph present on one side only. Uncertain when the other side has the same text elsewhere (another copy may be the counterpart)
    /// or when a rejected split or merge could explain it (<paramref name="flagNote"/>).
    /// </summary>
    internal static void OnlyIn(ParagraphFingerprint? template, ParagraphFingerprint? document, HashSet<string> otherKeys, List<Change> changes,
        bool forceUncertain = false, string? flagNote = null)
    {
        var paragraph = template ?? document!;
        var uncertain = forceUncertain || otherKeys.Contains(paragraph.KeyHash) || flagNote is not null;
        var side = new ChangeSide(paragraph.Location, paragraph.Ordinal, paragraph.RawText, new TextSpan(0, paragraph.RawText.Length, paragraph.RawText));
        changes.Add(template is not null
            ? new Change("paragraph_only_in_template", "unmatched", uncertain, flagNote, side, null)
            : new Change("paragraph_only_in_document", "unmatched", uncertain, flagNote, null, side));
    }

    internal static void Matched(ParagraphFingerprint template, ParagraphFingerprint document, string status, bool uncertain, List<Change> changes,
        string? flagNote = null, bool compareFormatting = false)
    {
        uncertain |= flagNote is not null;
        if (compareFormatting)
        {
            var differences = DirectFormatDiff.Compare(template, document);
            if (differences.Count > 0)
                changes.Add(new Change("direct_format_modified", status, uncertain || differences.Any(d => d.Property == "numbering.unverified"),
                    flagNote, Whole(template), Whole(document)) { FormatChanges = differences });
        }
        if (string.Equals(template.RawText, document.RawText, StringComparison.Ordinal))
        {
            if (uncertain) changes.Add(new Change("paired_uncertain", status, true, flagNote, Whole(template), Whole(document)));
            return;
        }
        var note = Join(template.KeyHash == document.KeyHash ? "whitespace_or_unicode_only" : null, flagNote);
        foreach (var hunk in TextDiff.Compare(template.RawText, document.RawText))
            changes.Add(new Change(hunk.Kind, status, uncertain || hunk.Coarse, hunk.Coarse ? Join(note, "text_diff_incomplete") : note,
                new ChangeSide(template.Location, template.Ordinal, template.RawText, hunk.Template),
                new ChangeSide(document.Location, document.Ordinal, document.RawText, hunk.Document)));
    }

    /// <summary>
    /// A split or merge: one structural change listing every paragraph of both sides, then the text differences between the joined sides.
    /// Each text span is mapped back onto the paragraph it falls in; a difference at the joint between paragraphs keeps the paragraph but has no
    /// span and is labelled spans_paragraph_boundary.
    /// </summary>
    static void Grouped(string kind, string label, ParagraphFingerprint[] templates, ParagraphFingerprint[] documents, List<Change> changes)
    {
        var templateGroup = templates.Select(Whole).ToArray();
        var documentGroup = documents.Select(Whole).ToArray();
        changes.Add(new Change(kind, "paired_by_similarity", true, label, templateGroup[0], documentGroup[0],
            TemplateGroup: templateGroup, DocumentGroup: documentGroup));

        var templateJoint = Joint.Of(templates);
        var documentJoint = Joint.Of(documents);
        foreach (var hunk in TextDiff.Compare(templateJoint.Text, documentJoint.Text))
        {
            var (templateSide, templateCrosses) = templateJoint.Map(hunk.Template);
            var (documentSide, documentCrosses) = documentJoint.Map(hunk.Document);
            var note = (templateCrosses || documentCrosses ? label + ";spans_paragraph_boundary" : label) + (hunk.Coarse ? ";text_diff_incomplete" : "");
            changes.Add(new Change(hunk.Kind, "paired_by_similarity", true, note, templateSide, documentSide,
                TemplateGroup: templateGroup, DocumentGroup: documentGroup));
        }
    }

    /// <summary>Paragraphs joined with '\n' for diffing, with each paragraph's offset so a span maps back to it.</summary>
    sealed record Joint(string Text, ParagraphFingerprint[] Parts, int[] Starts)
    {
        public static Joint Of(ParagraphFingerprint[] parts)
        {
            var starts = new int[parts.Length];
            var offset = 0;
            for (var k = 0; k < parts.Length; k++) { starts[k] = offset; offset += parts[k].RawText.Length + 1; }
            return new Joint(string.Join('\n', parts.Select(p => p.RawText)), parts, starts);
        }

        public (ChangeSide Side, bool Crosses) Map(TextSpan span)
        {
            for (var k = 0; k < Parts.Length; k++)
            {
                var start = Starts[k];
                var end = start + Parts[k].RawText.Length;
                if (span.Start >= start && span.End <= end)
                {
                    var raw = Parts[k].RawText;
                    return (new ChangeSide(Parts[k].Location, Parts[k].Ordinal, raw,
                        new TextSpan(span.Start - start, span.End - start, raw[(span.Start - start)..(span.End - start)])), false);
                }
            }
            var owner = Enumerable.Range(0, Parts.Length).LastOrDefault(k => Starts[k] <= span.Start);
            return (new ChangeSide(Parts[owner].Location, Parts[owner].Ordinal, Parts[owner].RawText, null), true);
        }
    }

    internal static ChangeSide Whole(ParagraphFingerprint p) => new(p.Location, p.Ordinal, p.RawText, null);

    internal static string? Join(string? first, string? second) => first is null ? second : second is null ? first : first + ";" + second;

    /// <summary>
    /// Dice similarity of two token sequences, 0 to 1, with the common tokens counted in order. When the token diff stops at its limit the value
    /// is 0 (no evidence for pairing) and the comparison running in this flow is marked incomplete: "not compared in full" is never reported as
    /// "compared and different".
    /// </summary>
    internal static double Dice(string[] x, string[] y)
    {
        var score = Dice(x, y, out var complete);
        if (!complete && CurrentAlignment.Value is { } state) state.Incomplete = true;
        return score;
    }

    /// <summary>
    /// Tokens in common, in order. Beyond the diff limit it is 0 and the comparison running in this flow is marked incomplete, as for
    /// <see cref="Dice(string[], string[])"/>.
    /// </summary>
    internal static int Common(string[] x, string[] y)
    {
        var edits = Myers.Diff(x, y, (p, q) => string.Equals(p, q, StringComparison.Ordinal), Myers.Limit, out var complete);
        if (complete) return edits.Count(e => e.Kind == EditKind.Equal);
        if (CurrentAlignment.Value is { } state) state.Incomplete = true;
        return 0;
    }

    /// <summary>Dice similarity with its completeness, for callers outside a comparison that must act on it themselves.</summary>
    internal static double Dice(string[] x, string[] y, out bool complete)
    {
        complete = true;
        if (x.Length + y.Length == 0) return 1;
        var edits = Myers.Diff(x, y, (p, q) => string.Equals(p, q, StringComparison.Ordinal), Myers.Limit, out complete);
        return complete ? 2.0 * edits.Count(e => e.Kind == EditKind.Equal) / (x.Length + y.Length) : 0;
    }

    /// <summary>Dice similarity of the non-whitespace tokens (as <see cref="TextDiff"/> cuts them) of two matching keys, 0 to 1.</summary>
    internal static double Similarity(string a, string b) => Dice(TextDiff.WordTokens(a), TextDiff.WordTokens(b));

    /// <summary><see cref="Similarity(string, string)"/> with its completeness.</summary>
    internal static double Similarity(string a, string b, out bool complete) => Dice(TextDiff.WordTokens(a), TextDiff.WordTokens(b), out complete);
}
