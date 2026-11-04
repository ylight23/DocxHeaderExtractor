using System.Text;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.DocumentProcessing.Output;

namespace DocxHeaderExtractor.DocumentProcessing.Gather;

public sealed record GatherHeadingReview(string Heading, GatherReviewResult Result);

/// <summary>A target heading an uncertain source section could go to: its text, place, chain and the content it already has.</summary>
public sealed record GatherCandidate(string Heading, string Location, string Chain, IReadOnlyList<string> Content);

/// <summary>
/// A source section whose place the code cannot decide, with all its content. Kind: NEAR_NAME (the target has no heading with that name but one
/// with a similar name), PATH_DIFFERS (same name but different parent chain), AMBIGUOUS (several target headings match),
/// PARENT_UNCERTAIN (its parent is uncertain). It is never written until a decision is supplied.
/// </summary>
public sealed record GatherUncertain(string Kind, string SourceHeading, string Source, string? SourceParent,
    IReadOnlyList<GatherCandidate> Candidates, IReadOnlyList<GatheredFact> Facts, string Reason)
{
    /// <summary>
    /// Where the section goes if the reader decides it is a heading of its own: at the end of this target heading's subtree (null: the end of
    /// the document), before <see cref="NewHeadingNextLocation"/>.
    /// </summary>
    public string? NewHeadingParent { get; init; }
    public string? NewHeadingNextLocation { get; init; }
}

/// <summary>The result of matching sources to a target by heading: the sections to write, what is not written, and what is held as uncertain.</summary>
public sealed record GatherHeadingPlan(IReadOnlyList<GatherSection> Sections, IReadOnlyList<GatherExcluded> Excluded, IReadOnlyList<GatherUncertain> Uncertain)
{
    public void Deconstruct(out IReadOnlyList<GatherSection> sections, out IReadOnlyList<GatherExcluded> excluded)
    {
        sections = Sections;
        excluded = Excluded;
    }
}

/// <summary>
/// Moves content by heading name, the contract for one original document and other documents: the headings of every document are taken from its
/// outline, and a section of the original receives the content of each source section whose heading has the same name. A section of the original
/// with no same-named source heading receives nothing. The match is by name only, done in code; no model decides it.
/// <para>
/// A name is the heading text without its numbering or label prefix ("1.", "2.1", "Item 1:", "Mục 2."), without trailing punctuation, in lower case
/// with whitespace collapsed: "1. Thông tin chung", "3. THÔNG TIN CHUNG" and "Thông tin chung:" have the same name. The content of a section is the
/// text after the heading in its own paragraph and the paragraphs up to the next heading. Child sections are matched separately. Content is copied
/// verbatim with its source; only an entire identical normalized content item in the original section is excluded.
/// </para>
/// </summary>
public static class GatherByHeading
{
    static readonly Regex Numbering = new(
        @"^\s*(?:(?:item|mục|muc|chương|chuong|phần|phan|điều|dieu|section|part)\s+(?:\d+(?:\.\d+)*|[IVXLC]+)\s*[.:)\-–]?\s+|\d+(?:\.\d+)*(?:[.:)\-–]\s+|\s+)|(?:[a-zA-Z]|[IVXLC]+)[.)]\s+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    /// <summary>The name two headings are compared by.</summary>
    public static string Name(string heading)
    {
        var text = heading.Normalize(NormalizationForm.FormC).Trim();
        text = Numbering.Replace(text, "", 1);
        text = text.TrimEnd('.', ':', ';', ',', ' ', '\t');
        return Spaces.Replace(text, " ").Trim().ToLowerInvariant();
    }

    static readonly Regex DatePart = new(@"\b(tháng|thang|năm|nam|ngày|ngay|quý|quy|month|year)\s+\d+(?:\s*/\s*\d+)*|\b\d{1,2}\s*/\s*\d{4}\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The key a heading is compared by inside a chain: its <see cref="Name"/> with dates written as "tháng 8/2026", "năm 2026" or "8/2026" replaced
    /// by "#", so "Kế hoạch trọng tâm tháng 9/2026" and "Kế hoạch trọng tâm tháng 11/2026" have the same key. Other numbers are kept: "Phụ lục 1" and
    /// "Phụ lục 2" stay different.
    /// </summary>
    public static string Key(string heading) => DatePart.Replace(Name(heading), m => char.IsDigit(m.Value[0]) ? "#" : m.Groups[1].Value + " #");

    /// <summary>
    /// A heading found in a document: its text, its name, its first and last unit, where its direct content ends (exclusive), its level, the index of
    /// its parent section (-1 at the top) and its chain: the keys of its parents and its own, from the top.
    /// </summary>
    internal sealed record Section(string Heading, string Name, int First, int Last, int End)
    {
        public int Level { get; init; } = 1;
        public int Parent { get; init; } = -1;
        public string Chain { get; init; } = "";
        public string Location { get; init; } = "";
        /// <summary>Part of the document's title (see <see cref="WithoutTitle"/>): left out of every chain, never placed.</summary>
        public bool Title { get; init; }
    }

    /// <summary>
    /// The sections with the document's title left out of their chains. The title is the run of sections at the start whose last one contains
    /// every later section and whose earlier ones have no child ("BÁO CÁO" then "Các hoạt động tháng 9/2026" over the whole report), when the
    /// other side has no heading with any of their keys. One outline may give the title as a heading and another not, or split it over two
    /// headings; left in, it would change the chain of every heading below it. A title the other side also has stays in the chain.
    /// </summary>
    internal static List<Section> WithoutTitle(List<Section> sections, IReadOnlySet<string> otherKeys)
    {
        bool Contains(int ancestor, int i)
        {
            for (var p = sections[i].Parent; p >= 0; p = sections[p].Parent) if (p == ancestor) return true;
            return false;
        }
        for (var k = 0; k < sections.Count - 1; k++)
        {
            if (otherKeys.Contains(Key(sections[k].Heading))) return sections;
            if (Enumerable.Range(k + 1, sections.Count - k - 1).All(i => Contains(k, i)))
            {
                var prefix = sections[k].Chain + ChainSeparator;
                return sections.Select((x, i) => i <= k ? x with { Title = true, Chain = "", Parent = -1 }
                    : x with { Chain = x.Chain[prefix.Length..], Parent = x.Parent == k ? -1 : x.Parent }).ToList();
            }
            if (Enumerable.Range(k + 1, sections.Count - k - 1).Any(i => sections[i].Parent == k)) return sections;
        }
        return sections;
    }

    /// <summary>
    /// The sections of a document, in order, from its outline. A heading of the outline that is not found in the units is left out. A heading
    /// without a level takes the level of the heading before it.
    /// </summary>
    internal static List<Section> Sections(IReadOnlyList<GatherUnit> units, IReadOnlyList<HeadingRecord> outline)
    {
        var found = new List<(string Text, int First, int Last, int Level)>();
        var cursor = 0;
        foreach (var heading in outline.OrderBy(h => h.Index))
        {
            var text = heading.Text.Trim();
            var at = GatherDocument.FindHeading(units, cursor, text);
            if (at < 0) continue;
            var last = GatherDocument.HeadingEnd(units, at, text);
            found.Add((text, at, last, heading.Level ?? (found.Count > 0 ? found[^1].Level : 1)));
            cursor = last + 1;
        }
        var sections = new List<Section>();
        var open = new Stack<int>();
        for (var i = 0; i < found.Count; i++)
        {
            var h = found[i];
            while (open.Count > 0 && sections[open.Peek()].Level >= h.Level) open.Pop();
            var parent = open.Count > 0 ? open.Peek() : -1;
            var chain = parent < 0 ? Key(h.Text) : sections[parent].Chain + ChainSeparator + Key(h.Text);
            sections.Add(new Section(h.Text, Name(h.Text), h.First, h.Last, i + 1 < found.Count ? found[i + 1].First : units.Count)
            { Level = h.Level, Parent = parent, Chain = chain, Location = units[h.First].Location });
            open.Push(i);
        }
        return sections;
    }

    const string ChainSeparator = " > ";

    /// <summary>
    /// Word similarity at or above which a source heading the target does not have is not added as a new heading but held as uncertain next to
    /// the similar target heading ("Tiếp nhận và hỗ trợ từ thiện" and "Công tác tiếp nhận và hỗ trợ từ thiện"): the code never merges by similarity.
    /// </summary>
    public const double NearNameThreshold = 0.5;

    /// <summary>Where the content of a source section goes: an existing target section, a new heading, nowhere (with the reason), or held as uncertain.</summary>
    sealed record Placement(int Target, NewHeading? New, string? Unplaced, bool PathDiffers, UncertainItem? Uncertain = null);

    /// <summary>A source section whose place the code cannot decide, with the target sections it could go to.</summary>
    sealed class UncertainItem(string kind, string heading, string source, string? parent, IReadOnlyList<int> candidates, string reason)
    {
        public string Kind { get; } = kind;
        public string Heading { get; } = heading;
        public string Source { get; } = source;
        public string? Parent { get; } = parent;
        public IReadOnlyList<int> Candidates { get; } = candidates;
        public string Reason { get; } = reason;
        public List<GatheredFact> Facts { get; } = [];
        /// <summary>The target section of its nearest placed parent (-1: none), under which it goes if it becomes a new heading.</summary>
        public int Anchor { get; set; } = -1;
    }

    /// <summary>A heading the target does not have, to be written with its content under its nearest placed parent.</summary>
    sealed class NewHeading(string text, string key, int anchor, NewHeading? parent, string source)
    {
        public string Text { get; } = text;
        public string Key { get; } = key;
        /// <summary>The target section it is written at the end of, or -1 for the end of the document.</summary>
        public int Anchor { get; } = anchor;
        public NewHeading? Parent { get; } = parent;
        public string Source { get; } = source;
        public List<NewHeading> Children { get; } = [];
        public List<GatheredFact> Facts { get; } = [];
        public int Depth => Parent is null ? 1 : Parent.Depth + 1;
    }

    /// <summary>The first target section after the subtree of <paramref name="anchor"/>, or -1 when the subtree runs to the end of the document.</summary>
    static int SubtreeEnd(IReadOnlyList<Section> sections, int anchor) => anchor < 0 ? -1
        : Enumerable.Range(anchor + 1, sections.Count - anchor - 1).FirstOrDefault(i => sections[i].Level <= sections[anchor].Level, -1);

    /// <summary>The target sections inside the subtree of <paramref name="anchor"/> (all of them for -1).</summary>
    static IEnumerable<int> Subtree(IReadOnlyList<Section> sections, int anchor)
    {
        if (anchor < 0) { for (var i = 0; i < sections.Count; i++) yield return i; yield break; }
        for (var i = anchor + 1; i < sections.Count && sections[i].Level > sections[anchor].Level; i++) yield return i;
    }

    /// <summary>The headings of a document read without an outline: its heading units (style headings), each up to its verified end.</summary>
    static IReadOnlyList<HeadingRecord> OutlineFromUnits(IReadOnlyList<GatherUnit> units) =>
        units.Select((u, i) => (u, i)).Where(x => x.u.IsHeading && x.u.HeadingEnd is not null)
            .Select(x => new HeadingRecord { Index = x.i, Level = 1, Text = x.u.Text[..x.u.HeadingEnd!.Value] }).ToList();

    /// <summary>
    /// For every heading of the original, the content of the matching sections of the sources. A source section matches by its chain (the keys of
    /// its parents and its own, see <see cref="Key"/>): the one target section with the same chain. When no target section has that chain, the one
    /// target section with the same name inside the subtree of the nearest matched parent is held as PATH_DIFFERS for review (an outline
    /// may give a heading another level). When there is none: with <paramref name="addMissingHeadings"/> the heading is added as a new section at the
    /// end of that parent's subtree, otherwise its content is listed as not placed. Two target sections with the same chain, or the same name in that
    /// subtree, receive nothing: the content is listed as not placed. Nothing a source holds under a heading is dropped without being listed.
    /// A document title that only one side gives as a heading is left out of that side's chains first (<see cref="WithoutTitle"/>); what the
    /// source holds inside its title is listed as DOCUMENT_TITLE.
    /// <para>
    /// A source heading the target does not have but whose name is close to a target heading under the same parent, a chain or name that
    /// matches several target sections, and the children of such a heading are held as uncertain (<see cref="GatherHeadingPlan.Uncertain"/>):
    /// they are never written, and the reader (or a model's advice) decides.
    /// </para>
    /// <para>
    /// Returns the target sections in order, then the new sections (parent before child), the facts that are not written (already in the
    /// original, already taken from another source, or not placed), and the uncertain sections.
    /// </para>
    /// </summary>
    public static GatherHeadingPlan Build(string targetPath,
        IReadOnlyList<HeadingRecord> targetOutline, IReadOnlyList<GatherSourceFile> sources, bool addMissingHeadings = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentNullException.ThrowIfNull(targetOutline);
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Any(s => string.IsNullOrWhiteSpace(s.Name)) ||
            sources.Select(s => s.Name).Distinct(StringComparer.Ordinal).Count() != sources.Count)
            throw new ArgumentException("Each source must have a distinct, non-empty ID.", nameof(sources));
        var target = GatherUnits.Read(targetPath, "target", targetOutline);
        var targetSections = Sections(target.Units, targetOutline);
        var read = sources.Select(s =>
        {
            var source = GatherUnits.Read(s.Path, s.Name, s.Outline);
            return (Source: source, Sections: Sections(source.Units, s.Outline ?? OutlineFromUnits(source.Units)));
        }).ToList();
        // A title only one side has is left out of the chains on that side.
        var targetKeys = targetSections.Select(t => Key(t.Heading)).ToHashSet(StringComparer.Ordinal);
        var sourceKeys = read.SelectMany(r => r.Sections).Select(t => Key(t.Heading)).ToHashSet(StringComparer.Ordinal);
        targetSections = WithoutTitle(targetSections, sourceKeys);
        read = read.Select(r => (r.Source, WithoutTitle(r.Sections, targetKeys))).ToList();
        var gaps = target.Gaps.Concat(read.SelectMany(r => r.Source.Gaps)).ToList();

        var chainCount = targetSections.Where(t => !t.Title).GroupBy(t => t.Chain).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var received = targetSections.Select(_ => new List<GatheredFact>()).ToList();
        var roots = new List<NewHeading>();
        var excluded = new List<GatherExcluded>();
        var matched = new HashSet<int>();
        var uncertain = new List<UncertainItem>();
        foreach (var (source, sourceSections, path) in read.Select((r, i) => (r.Source, r.Sections, sources[i].Path)))
        {
            if (sourceSections.Count > 0)
                foreach (var fact in Content(source.Units, new Section("", "", -1, -1, sourceSections[0].First)))
                    excluded.Add(new GatherExcluded("", fact, "BEFORE_FIRST_HEADING: nội dung trước heading đầu tiên của nguồn"));
            var placed = new Placement[sourceSections.Count];
            var perTarget = new Dictionary<int, int>();
            for (var i = 0; i < sourceSections.Count; i++)
            {
                var s = sourceSections[i];
                placed[i] = Place(s, i, placed, sourceSections, targetSections, chainCount, roots, addMissingHeadings,
                    source.Units[s.First].SourceId + " " + s.Location);
                if (placed[i].Uncertain is { } item && !uncertain.Contains(item)) uncertain.Add(item);
                if (placed[i].Target >= 0)
                {
                    perTarget[placed[i].Target] = perTarget.GetValueOrDefault(placed[i].Target) + 1;
                    matched.Add(placed[i].Target);
                }
            }
            var endTable = GatherDocument.EndTablePrefix(path);
            for (var i = 0; i < sourceSections.Count; i++)
            {
                var s = sourceSections[i];
                var p = placed[i];
                foreach (var content in Content(source.Units, s))
                {
                    // The table that ends the source (signature, recipients) closes that document; it is not content of its last heading.
                    if (endTable is not null && content.Parts.All(part => part.Location.StartsWith(endTable, StringComparison.Ordinal)))
                    {
                        excluded.Add(new GatherExcluded(s.Heading, content, "DOCUMENT_END_TABLE: bảng kết thúc văn bản nguồn (ký tên, nơi nhận)"));
                        continue;
                    }
                    var fact = p.PathDiffers ? content with { Flags = [.. content.Flags, "HEADING_PATH_DIFFERS: " + s.Chain] } : content;
                    if (p.Target >= 0)
                        received[p.Target].Add(perTarget[p.Target] > 1 ? fact with { Flags = [.. fact.Flags, "MULTIPLE_MATCHING_SOURCE_SECTIONS"] } : fact);
                    else if (p.New is not null) p.New.Facts.Add(fact);
                    else if (p.Uncertain is not null) p.Uncertain.Facts.Add(fact);
                    else excluded.Add(new GatherExcluded(s.Heading, fact, s.Title
                        ? "DOCUMENT_TITLE: nội dung trong phần tiêu đề của văn bản nguồn, không thuộc mục nào"
                        : "UNPLACED: " + p.Unplaced));
                }
            }
        }

        var sections = new List<GatherSection>();
        for (var t = 0; t < targetSections.Count; t++)
        {
            var section = targetSections[t];
            var ambiguous = !section.Title && chainCount[section.Chain] > 1;
            var facts = received[t].Select(f => f with { Flags = [.. f.Flags, "SCOPE_UNREVIEWED"] }).ToList();
            var targetContent = Content(target.Units, section).ToList();
            var goal = string.Join("\n", new[] { section.Heading }.Concat(targetContent.Select(f => f.Render())));
            var run = new GatherRun(facts, [], [], ambiguous ? ["AMBIGUOUS_TARGET_HEADING"] : [],
                ambiguous ? read.SelectMany(r => r.Source.Units).Select(u => u.Id).ToArray() : [], gaps, 1);
            var built = new GatherSection(section.Location, section.Heading, goal, run, GatherDocument.Conflicts(facts),
                section.End < target.Units.Count ? target.Units[section.End].Location : null)
            {
                Note = ambiguous ? "có nhiều heading cùng tên trong văn bản đích; chưa xác định được mục tương ứng" :
                    facts.Count == 0 ? "không có heading cùng tên trong văn bản nguồn" : null,
            };
            var (kept, repeated) = RemoveDuplicates(built, targetContent);
            var note = built.Note is null && kept.Run.Facts.Count == 0 ? "mọi đoạn của nguồn đã có sẵn trong văn bản đích" :
                matched.Contains(t) && facts.Count == 0 && !ambiguous ? "nguồn có heading cùng chuỗi nhưng không có nội dung trực tiếp dưới heading đó" : built.Note;
            sections.Add(kept with { Note = note });
            excluded.AddRange(repeated);
        }
        foreach (var root in roots)
            foreach (var node in DepthFirst(root))
            {
                // A new heading is written at the end of its anchor's subtree: before the next target heading of the same or a higher level.
                var anchor = node.Anchor;
                var boundary = SubtreeEnd(targetSections, anchor);
                var facts = node.Facts.Select(f => f with { Flags = [.. f.Flags, "SCOPE_UNREVIEWED"] }).ToList();
                var built = new GatherSection("", node.Text, node.Text, new GatherRun(facts, [], [], [], [], gaps, 1), GatherDocument.Conflicts(facts),
                    boundary < 0 ? null : targetSections[boundary].Location)
                {
                    NewHeadingDepth = node.Depth,
                    NewHeadingParent = node.Parent?.Text ?? (anchor < 0 ? null : targetSections[anchor].Heading),
                    NewHeadingSource = node.Source,
                };
                var (kept, repeated) = RemoveDuplicates(built, []);
                sections.Add(kept);
                excluded.AddRange(repeated);
            }
        var held = uncertain.Select(u => new GatherUncertain(u.Kind, u.Heading, u.Source, u.Parent,
            u.Candidates.Select(t => new GatherCandidate(targetSections[t].Heading, targetSections[t].Location, targetSections[t].Chain,
                Content(target.Units, targetSections[t]).Select(f => f.Render()).ToList())).ToList(), u.Facts, u.Reason)
        {
            NewHeadingParent = u.Anchor < 0 ? null : targetSections[u.Anchor].Heading,
            NewHeadingNextLocation = SubtreeEnd(targetSections, u.Anchor) is var end and >= 0 ? targetSections[end].Location : null,
        }).ToList();
        return new GatherHeadingPlan(sections, excluded, held);
    }

    /// <summary>
    /// The sections with the SCOPE_UNREVIEWED guard removed, for a caller whose contract is the heading chain itself: the place of every fact was
    /// decided by the chain, and what the chain could not decide is in <see cref="GatherHeadingPlan.Uncertain"/>, which is never written.
    /// </summary>
    public static IReadOnlyList<GatherSection> DecidedByChain(IReadOnlyList<GatherSection> sections) => sections.Select(s => s with
    {
        Run = s.Run with { Facts = s.Run.Facts.Select(f => f with { Flags = f.Flags.Where(x => x != "SCOPE_UNREVIEWED").ToArray() }).ToList() },
    }).ToList();

    static IEnumerable<NewHeading> DepthFirst(NewHeading node)
    {
        yield return node;
        foreach (var child in node.Children)
            foreach (var x in DepthFirst(child)) yield return x;
    }

    /// <summary>Where one source section goes (see <see cref="Build"/>). Its parent sections are placed before it.</summary>
    static Placement Place(Section s, int index, Placement[] placed, IReadOnlyList<Section> sourceSections, IReadOnlyList<Section> targets,
        Dictionary<string, int> chainCount, List<NewHeading> roots, bool addMissing, string location)
    {
        if (s.Name.Length == 0) return new(-1, null, "heading không có tên", false);
        if (s.Title) return new(-1, null, "tiêu đề văn bản", false);
        string? ParentText(int p) => p < 0 ? null : sourceSections[p].Heading;
        if (chainCount.TryGetValue(s.Chain, out var same))
            return same == 1 ? new(targets.ToList().FindIndex(t => t.Chain == s.Chain), null, null, false)
                : new(-1, null, null, false, new UncertainItem("AMBIGUOUS", s.Heading, location, ParentText(s.Parent),
                    Enumerable.Range(0, targets.Count).Where(t => targets[t].Chain == s.Chain).ToList(),
                    "văn bản đích có " + same + " heading cùng chuỗi \"" + s.Chain + "\"")
                    { Anchor = s.Parent >= 0 ? placed[s.Parent].Target : -1 });
        // The nearest parent that was placed: a target section to search under, or a new heading to add under.
        var parent = s.Parent;
        NewHeading? newParent = null;
        UncertainItem? uncertainParent = null;
        while (parent >= 0 && placed[parent].Target < 0)
        {
            if (placed[parent].Unplaced is not null) return new(-1, null, "heading cha \"" + sourceSections[parent].Heading + "\" chưa xếp được", false);
            newParent ??= placed[parent].New;
            uncertainParent ??= placed[parent].Uncertain;
            parent = sourceSections[parent].Parent;
        }
        var anchor = parent < 0 ? -1 : placed[parent].Target;
        var key = Key(s.Heading);
        var named = Subtree(targets, anchor).Where(t => !targets[t].Title && Key(targets[t].Heading) == key).ToList();
        if (named.Count == 1)
            return new(-1, null, null, false, new UncertainItem("PATH_DIFFERS", s.Heading, location, ParentText(s.Parent), named,
                "heading cùng tên nhưng chuỗi mục cha khác; cần xác nhận trước khi chèn") { Anchor = anchor });
        if (named.Count > 1)
            return new(-1, null, null, false, new UncertainItem("AMBIGUOUS", s.Heading, location, ParentText(s.Parent), named,
                "văn bản đích có " + named.Count + " heading tên \"" + s.Name + "\" dưới cùng mục cha") { Anchor = anchor });
        var near = Subtree(targets, anchor).Where(t => !targets[t].Title).Select(t => (t, Score: Changes.DocxComparer.Similarity(s.Name, targets[t].Name)))
            .Where(x => x.Score >= NearNameThreshold).OrderByDescending(x => x.Score).Select(x => x.t).Take(1).ToList();
        if (near.Count == 1)
            return new(-1, null, null, false, new UncertainItem("NEAR_NAME", s.Heading, location, ParentText(s.Parent), near,
                "văn bản đích không có heading \"" + s.Name + "\" nhưng có heading tên gần giống \"" + targets[near[0]].Heading + "\"") { Anchor = anchor });
        if (uncertainParent is not null)
            return new(-1, null, null, false, new UncertainItem("PARENT_UNCERTAIN", s.Heading, location, ParentText(s.Parent), uncertainParent.Candidates,
                "heading cha \"" + uncertainParent.Heading + "\" đang ở nhóm không chắc") { Anchor = anchor });
        if (!addMissing) return new(-1, null, "văn bản đích không có heading \"" + s.Name + "\" (chuỗi \"" + s.Chain + "\")", false);
        // Two sources adding the same missing heading under the same parent share one new heading.
        var siblings = newParent?.Children ?? roots;
        var node = siblings.FirstOrDefault(n => n.Key == key && n.Anchor == anchor);
        if (node is null)
        {
            node = new NewHeading(s.Heading, key, anchor, newParent, location);
            siblings.Add(node);
        }
        return new(-1, node, null, false);
    }

    /// <summary>Review name-matched candidates against their heading and source. Failed or unsure reviews remain blocked from writeback.</summary>
    public static async Task<(IReadOnlyList<GatherSection> Sections, IReadOnlyList<GatherExcluded> Excluded, IReadOnlyList<GatherHeadingReview> Reviews)>
        ReviewAsync(IReadOnlyList<GatherSection> sections, IReadOnlyList<GatherSource> sources, IGatherModel model, CancellationToken ct = default)
    {
        var reviewed = new List<GatherSection>();
        var excluded = new List<GatherExcluded>();
        var reviews = new List<GatherHeadingReview>();
        foreach (var section in sections)
        {
            if (section.Run.Facts.Count == 0) { reviewed.Add(section); continue; }
            // The candidate outline may omit intervening headings, so its inferred target content is not a reliable review goal.
            var result = await GatherSemanticReview.ReviewAsync(section.Heading, section.Run, sources, model, ct).ConfigureAwait(false);
            var (accepted, rejected) = GatherReviewApply.Apply(section, result);
            reviewed.Add(accepted);
            excluded.AddRange(rejected);
            reviews.Add(new(section.Heading, result));
        }
        return (reviewed, excluded, reviews);
    }

    /// <summary>
    /// A fact whose text is a whole content item of the original section is excluded (ALREADY_IN_TARGET_EXACT), and so is a fact whose text was
    /// already taken from another source into the same section (ALREADY_FROM_OTHER_SOURCE). A fact close to an item of the original but not the
    /// same (another date or number) is kept and flagged POSSIBLE_CONFLICT. A substring of an item is not a repeat: it may say less.
    /// </summary>
    static (GatherSection Section, IReadOnlyList<GatherExcluded> Excluded) RemoveDuplicates(
        GatherSection section, IReadOnlyList<GatheredFact> targetContent)
    {
        static string TextKey(string text) => Spaces.Replace(text.Normalize(NormalizationForm.FormC).Trim(), " ");
        var targetLines = targetContent.Select(f => f.Render()).ToList();
        var targetKeys = targetLines.Select(TextKey).ToHashSet(StringComparer.Ordinal);
        var taken = new Dictionary<string, string>(StringComparer.Ordinal);
        var kept = new List<GatheredFact>();
        var excluded = new List<GatherExcluded>();
        foreach (var fact in section.Run.Facts)
        {
            var text = fact.Render();
            var key = TextKey(text);
            var source = fact.Parts[0].SourceId;
            if (targetKeys.Contains(key)) { excluded.Add(new(section.Heading, fact, "ALREADY_IN_TARGET_EXACT")); continue; }
            if (taken.TryGetValue(key, out var first) && first != source)
            {
                excluded.Add(new(section.Heading, fact, "ALREADY_FROM_OTHER_SOURCE: " + first));
                continue;
            }
            taken.TryAdd(key, source);
            var tokens = Changes.TextDiff.WordTokens(text.ToLowerInvariant());
            var near = targetLines.FirstOrDefault(line =>
                Changes.DocxComparer.Dice(tokens, Changes.TextDiff.WordTokens(line.ToLowerInvariant())) >= GatherReviewApply.NearTargetThreshold);
            kept.Add(near is null ? fact : fact with { Flags = [.. fact.Flags, "POSSIBLE_CONFLICT: " + (near.Length > 80 ? near[..80] + "…" : near)] });
        }
        return (section with { Run = section.Run with { Facts = kept }, Conflicts = GatherDocument.Conflicts(kept) }, excluded);
    }

    /// <summary>
    /// The content of a section as verbatim facts: the text after the heading in its last paragraph, then each paragraph up to the next heading.
    /// The cells of one table row are one fact, so a sentence laid out across cells stays whole.
    /// </summary>
    static IEnumerable<GatheredFact> Content(IReadOnlyList<GatherUnit> units, Section section)
    {
        // Last is -1 for the text before the first heading.
        if (section.Last >= 0 && units[section.Last] is { HeadingEnd: { } end } heading &&
            Trimmed(heading.Text, ContentStart(heading.Text, end), heading.Text.Length) is { } inline)
            yield return new GatheredFact([Part(heading, inline.Start, inline.End)], []);
        var row = new List<SourcedPart>();
        string? rowPath = null;
        for (var i = section.Last + 1; i < section.End; i++)
        {
            var unit = units[i];
            if (Trimmed(unit.Text, 0, unit.Text.Length) is not { } whole) continue;
            var part = Part(unit, whole.Start, whole.End);
            if (unit.TablePath is not null && unit.RowPath == rowPath) { row.Add(part); continue; }
            if (row.Count > 0) yield return new GatheredFact(row, []);
            row = [part];
            rowPath = unit.TablePath is null ? null : unit.RowPath;
            if (rowPath is null) { yield return new GatheredFact(row, []); row = []; }
        }
        if (row.Count > 0) yield return new GatheredFact(row, []);
    }

    // A lone colon followed by whitespace can be the heading delimiter. Never remove a leading minus, dash or other source punctuation.
    static int ContentStart(string text, int from)
    {
        var at = from;
        while (at < text.Length && char.IsWhiteSpace(text[at])) at++;
        return at < text.Length && text[at] == ':' && at + 1 < text.Length && char.IsWhiteSpace(text[at + 1]) ? at + 1 : from;
    }

    static SourcedPart Part(GatherUnit unit, int start, int end) =>
        new(unit.Id, unit.SourceId, unit.Location, "text", unit.Text[start..end], start, end);

    /// <summary>The range inside [from, to) without leading or trailing whitespace, or null when it holds only whitespace.</summary>
    static (int Start, int End)? Trimmed(string text, int from, int to)
    {
        while (from < to && char.IsWhiteSpace(text[from])) from++;
        while (to > from && char.IsWhiteSpace(text[to - 1])) to--;
        return to > from ? (from, to) : null;
    }
}
