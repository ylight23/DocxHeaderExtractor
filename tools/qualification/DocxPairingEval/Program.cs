// Offline evaluation of paragraph pairing (P1, experimental). No model call, no Gold. Every case is a controlled edit of a real DOCX with a
// known answer; each case is compared under both grouping rules so the rules are measured on exactly the same inputs.
//
// Judging checks both sides: a split must name the template paragraph and both document parts, a merge both template parts and the merged
// paragraph, an inserted copy the copy itself. Every pair in the result is also checked (mispaired). Locations of edited paragraphs keep
// the original location plus '#a', '#b' (split parts) or '#m' (merge), so any pair whose document base location differs is a wrong pair.
//
// Sets: baseline210 re-creates the earlier 210 cases exactly (same files, order, seed, generator); tune and eval split the same files by
// position; holdout uses files never looked at while choosing a rule.
//
// usage: dotnet run --project tools/qualification/DocxPairingEval -- <repo-root> <new-output-dir>
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.DocumentProcessing.Changes;
using DocxHeaderExtractor.Sources.Docx.Changes;

if (args.Length != 2) throw new ArgumentException("usage: <repo-root> <new-output-dir>");
var root = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
if (Directory.Exists(output)) throw new InvalidOperationException("REFUSE_OVERWRITE_OUTPUT");
Directory.CreateDirectory(output);

string[] Docx(string folder) => Directory.GetDirectories(Path.Combine(root, folder)).OrderBy(d => d, StringComparer.Ordinal)
    .SelectMany(d => Directory.GetFiles(d, "*.docx").OrderBy(f => f, StringComparer.Ordinal)).ToArray();
var files = Docx("todo10_8/generated-docx-v2").Concat(Docx("todo10_8/heading_corpus_100")).ToArray();
var versions = new Dictionary<string, DocxVersion>();
foreach (var f in files) versions[f] = DocxVersionReader.Read(f);

// Holdout: Word files whose document number was never used above (nor 075, used to form the hypothesis).
static string Number(string path) => Path.GetFileName(path)[..3];
var used = files.Select(Number).Append("075").ToHashSet();
var unreadable = new List<string>();
var holdoutFiles = new List<string>();
foreach (var f in Docx("todo10_8/heading_corpus_95_word").Where(f => !used.Contains(Number(f))))
{
    try { versions[f] = DocxVersionReader.Read(f); holdoutFiles.Add(f); }
    catch (Exception e) { unreadable.Add($"{Path.GetFileName(f)}: {e.GetType().Name}"); }
}
var rules = new[] { GroupRule.Advantage, GroupRule.ExplainsLeftover };

// 1. Document 075: the comparer's own decision record for the split of "MEETING MINUTES".
var v1 = Path.Combine(root, "todo10_8/generated-docx/05_bien_ban_hop/075_FORTIS_GC_Minutes_Nov21_2024.docx");
var v2 = Path.Combine(root, "todo10_8/generated-docx-v2/05_bien_ban_hop/075_FORTIS_GC_Minutes_Nov21_2024.docx");
var meeting = new List<object>();
foreach (var rule in rules)
{
    var decisions = new List<GroupDecision>();
    var result = DocxComparer.Compare(DocxVersionReader.Read(v1), DocxVersionReader.Read(v2), new ComparerOptions(rule), decisions);
    var candidate = decisions.FirstOrDefault(d => d.Kind == "split" && d.WholeLocation == "body[1]/p[2]"
        && d.PartLocations.SequenceEqual(new[] { "body[1]/p[1]", "body[1]/p[2]" }));
    var reported = result.Changes.Any(c => c.Kind == "paragraph_split" && c.Template!.Location == "body[1]/p[2]");
    meeting.Add(new { rule = rule.ToString(), candidate, reportedAsSplit = reported });
    Console.WriteLine($"075 MEETING MINUTES [{rule}]: reason={candidate?.Reason ?? "not_recorded"} gain={candidate?.Gain} reported_as_split={reported}");
}

// 2. Cases.
var cases = new List<Case>();
cases.AddRange(Generate("baseline210", files, 20261102, 30, BaseTypes, legacy: true));
var tuneFiles = files.Where((_, i) => i % 2 == 0).ToArray();
var evalFiles = files.Where((_, i) => i % 2 == 1).ToArray();
var newTypes = BaseTypes.Concat(HardTypes).Concat(TableTypes).ToArray();
cases.AddRange(Generate("tune", tuneFiles, 20261103, 20, newTypes, legacy: false));
cases.AddRange(Generate("eval", evalFiles, 20261104, 20, newTypes, legacy: false));
cases.AddRange(Generate("holdout", holdoutFiles.ToArray(), 20261105, 20, newTypes, legacy: false));

// 3. Run every case under both rules.
// Both rules run with rejected-group flags on (the comparer default). For each rule the same case is also run with flags off, to check that
// the flags change only uncertainty and notes, never which differences are reported or where.
var outcomes = new List<Outcome>();
foreach (var c in cases)
    foreach (var rule in rules)
    {
        var decisions = new List<GroupDecision>();
        var options = new ComparerOptions(rule, FlagRejectedGroups: true);
        var result = DocxComparer.Compare(c.Template, c.Document, options, decisions);
        var unflagged = DocxComparer.Compare(c.Template, c.Document, options with { FlagRejectedGroups = false });
        var verdict = Judge(c, result);
        var flagged = result.Changes.Any(x => x.Note?.Contains("_rejected", StringComparison.Ordinal) == true);
        var differencesChanged = !Shape(result).SequenceEqual(Shape(unflagged));
        outcomes.Add(new Outcome(c.Set, c.Type, Path.GetFileName(c.File), c.Location, c.Expected, rule.ToString(), verdict, flagged, differencesChanged,
            verdict.Found && !flagged ? null : decisions.Where(d => d.WholeLocation == c.Location || d.PartLocations.Contains(c.Location)).ToArray(),
            verdict.Found && !flagged ? null : result.Changes.Select(x => $"{x.Kind}|{x.Template?.Location}|{x.Document?.Location}|uncertain={x.Uncertain}|note={x.Note}").ToArray()));
    }

Console.WriteLine();
Console.WriteLine("set          type                    rule              cases  found  alt_flagged  alt_unflagged  wrong_group  mispaired  flag_on_found  flag_on_miss  diff_changed");
foreach (var g in outcomes.GroupBy(o => (o.Set, o.Type, o.Rule)))
{
    var list = g.ToList();
    Console.WriteLine($"{g.Key.Set,-12} {g.Key.Type,-23} {g.Key.Rule,-17} {list.Count,5}  {list.Count(o => o.Verdict.Found),5}  " +
        $"{list.Count(o => o.Verdict.AlternativeFlagged),11}  {list.Count(o => o.Verdict.AlternativeUnflagged),13}  " +
        $"{list.Count(o => o.Verdict.WrongGroup),11}  {list.Count(o => o.Verdict.Mispaired),9}  " +
        $"{list.Count(o => o.Verdict.Found && o.Flagged),13}  {list.Count(o => !o.Verdict.Found && o.Flagged),12}  {list.Count(o => o.DifferencesChanged),12}");
}

// Every reported difference, with uncertainty and notes left out: what must not change when flags are turned on or off.
static IEnumerable<string> Shape(ComparisonResult r) => r.Changes.Select(x =>
    $"{x.Kind}|{x.MatchStatus}|{x.Template?.Location}|{x.Template?.Span}|{x.Document?.Location}|{x.Document?.Span}|" +
    $"{string.Join(',', x.TemplateGroup?.Select(s => s.Location) ?? [])}|{string.Join(',', x.DocumentGroup?.Select(s => s.Location) ?? [])}");

File.WriteAllText(Path.Combine(output, "pairing-eval.json"), JsonSerializer.Serialize(new
{
    tool = "DocxPairingEval", providerCalls = 0, goldRead = false,
    files = files.Select(f => Path.GetRelativePath(root, f)),
    tuneFiles = tuneFiles.Select(Path.GetFileName), evalFiles = evalFiles.Select(Path.GetFileName),
    holdoutFiles = holdoutFiles.Select(Path.GetFileName), holdoutUnreadable = unreadable,
    seeds = new { baseline210 = 20261102, tune = 20261103, eval = 20261104, holdout = 20261105 },
    meetingMinutes075 = meeting,
    cases = cases.Select(c => new { c.Set, c.Type, file = Path.GetRelativePath(root, c.File), c.Location, c.Expected, c.Edit }),
    outcomes,
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"\nholdout files={holdoutFiles.Count} unreadable={unreadable.Count}\nwritten: {Path.Combine(output, "pairing-eval.json")}");

// ---- generation -------------------------------------------------------------------------------------------------------------

List<Case> Generate(string set, string[] sources, int seed, int perType, string[] types, bool legacy)
{
    var rng = new Random(seed);
    var pool = sources.Select(f => versions[f]).ToList();
    var list = new List<Case>();
    foreach (var type in types)
    {
        var attempts = 0;
        for (var n = 0; n < perType; n++)
        {
            if (++attempts > perType * 200) break;
            var made = Make(set, type, pool, sources, rng, legacy);
            if (made is null) { n--; continue; }
            list.Add(made);
        }
    }
    return list;
}

Case? Make(string set, string type, List<DocxVersion> pool, string[] sources, Random rng, bool legacy)
{
    if (StructuralTableTypes.Contains(type)) return MakeStructural(set, type, pool, sources, rng);
    var inTable = type.StartsWith("table_", StringComparison.Ordinal);
    var shortMin = type is "merge" or "merge_multi_edit" or "table_cell_merge" ? 4 : type == "table_cell_edit" ? 3 : 12;
    var picked = Pick(pool, rng, shortMin, inTable ? p => p.Container == "table" : null, inTable ? 2000 : int.MaxValue);
    if (picked is null) return null;
    var (v, i) = picked.Value;
    var file = sources[pool.IndexOf(v)];
    var ps = v.Paragraphs.ToList();
    var p = ps[i];
    var w = Words(p.NormalizedKey);
    switch (type)
    {
        case "split_short_head":
        case "split_balanced":
        case "split_with_edit":
        case "split_multi_edit":
        case "table_cell_split":
        {
            if (w.Length < 4) return null;
            var k = type == "split_short_head" ? rng.Next(2, 4) : w.Length / 2;
            var headWords = w[..k].ToArray();
            var tailWords = w[k..].ToArray();
            if (type == "split_with_edit") tailWords[rng.Next(tailWords.Length)] = "QWXZ";
            if (type == "split_multi_edit")
                for (var e = 0; e < 3; e++)
                {
                    var target = e % 2 == 0 ? tailWords : headWords;
                    target[rng.Next(target.Length)] = "QWXZ" + e;
                }
            ps.RemoveAt(i);
            ps.Insert(i, Re(p, string.Join(' ', tailWords), p.Location + "#b"));
            ps.Insert(i, Re(p, string.Join(' ', headWords), p.Location + "#a"));
            return new Case(set, type, file, p.Location, "paragraph_split", $"split after word {k}", v, With(v, ps));
        }
        case "merge":
        case "merge_multi_edit":
        case "table_cell_merge":
        {
            if (i + 1 >= ps.Count || Words(ps[i + 1].NormalizedKey).Length < 4) return null;
            var q = ps[i + 1];
            // New sets merge only within the same parent (body, or the same table cell); the legacy generator did not check.
            var crossParent = Parent(p.Location) != Parent(q.Location);
            if (!legacy && crossParent) return null;
            var merged = Words(p.RawText).Concat(Words(q.RawText)).ToArray();
            var text = p.RawText + " " + q.RawText;
            if (type == "merge_multi_edit")
            {
                for (var e = 0; e < 3; e++) merged[rng.Next(merged.Length)] = "QWXZ" + e;
                text = string.Join(' ', merged);
            }
            ps.RemoveAt(i + 1);
            ps[i] = Re(p, text, p.Location + "#m");
            var label = legacy && crossParent ? "merge_cross_parent" : type;
            return new Case(set, label, file, p.Location, "paragraph_merged:" + q.Location, "merged with next paragraph", v, With(v, ps));
        }
        case "edit_only":
        case "table_cell_edit":
        {
            w[rng.Next(w.Length)] = "QWXZ";
            ps[i] = Re(p, string.Join(' ', w), p.Location);
            return new Case(set, type, file, p.Location, "paired_by_similarity", "one word replaced", v, With(v, ps));
        }
        case "insert_unrelated":
        {
            var other = pool[(pool.IndexOf(v) + 1 + rng.Next(pool.Count - 1)) % pool.Count];
            var candidates = other.Paragraphs.Where(x => Words(x.NormalizedKey).Length >= 4).ToArray();
            if (candidates.Length == 0) return null;
            var foreign = candidates[rng.Next(Math.Min(10, candidates.Length))];
            ps.Insert(i + 1, Re(foreign, foreign.RawText, p.Location + "#ins"));
            return new Case(set, type, file, p.Location, $"paragraph_only_in_document:{p.Location}#ins", "foreign paragraph inserted after", v, With(v, ps), New(p.Location + "#ins"));
        }
        case "insert_duplicate":
        {
            ps.Insert(i + 1, Re(p, p.RawText, p.Location + "#dup"));
            return new Case(set, type, file, p.Location, $"paragraph_only_in_document:{p.Location}#dup", "same paragraph inserted after", v, With(v, ps), New(p.Location + "#dup"));
        }
        case "short_repeated_phrase":
        {
            var counts = v.Paragraphs.GroupBy(x => x.KeyHash).ToDictionary(g => g.Key, g => g.Count());
            var shorts = v.Paragraphs.Where(x => Words(x.NormalizedKey).Length is >= 1 and <= 3 && counts[x.KeyHash] >= 2).ToArray();
            if (shorts.Length == 0) return null;
            var phrase = shorts[rng.Next(shorts.Length)];
            ps.Insert(i + 1, Re(phrase, phrase.RawText, p.Location + "#ins"));
            return new Case(set, type, file, p.Location, $"paragraph_only_in_document:{p.Location}#ins", $"repeated short paragraph '{phrase.NormalizedKey}' inserted after", v, With(v, ps), New(p.Location + "#ins"));
        }
        case "new_contains_old_words":
        {
            var other = pool[(pool.IndexOf(v) + 1 + rng.Next(pool.Count - 1)) % pool.Count];
            var candidates = other.Paragraphs.Where(x => Words(x.NormalizedKey).Length >= 6).ToArray();
            if (candidates.Length == 0) return null;
            var foreign = Words(candidates[rng.Next(Math.Min(10, candidates.Length))].NormalizedKey);
            var start = rng.Next(w.Length - 3);
            var text = string.Join(' ', w[start..(start + 3)].Concat(foreign.Take(5)));
            ps.Insert(i + 1, Re(p, text, p.Location + "#ins"));
            return new Case(set, type, file, p.Location, $"paragraph_only_in_document:{p.Location}#ins", "new paragraph: 3 words of the previous one + 5 foreign words", v, With(v, ps), New(p.Location + "#ins"));
        }
    }
    throw new InvalidOperationException(type);
}

// Structural table edits on a simple table (every paragraph at tbl/tr/tc/p, no merged cells, no nested table, 3+ rows of 2+ cells):
// insert a row of foreign text, delete a row, swap two rows, or add a column of foreign text. Rows and cells after the edit are renumbered,
// so the case carries an origin map from each document location back to its template location ("inserted" for new text).
Case? MakeStructural(string set, string type, List<DocxVersion> pool, string[] sources, Random rng)
{
    for (var tries = 0; tries < 400; tries++)
    {
        var v = pool[rng.Next(pool.Count)];
        var tables = v.Paragraphs.Select((p, i) => (p, i, m: CellPath.Match(p.Location))).Where(x => x.m.Success)
            .GroupBy(x => x.m.Groups["table"].Value).ToArray();
        if (tables.Length == 0) continue;
        var table = tables[rng.Next(tables.Length)];
        var items = table.ToArray();
        var indices = items.Select(x => x.i).ToArray();
        var allInTable = v.Paragraphs.Where(p => p.Location.StartsWith(table.Key + "/", StringComparison.Ordinal)).Count();
        if (allInTable != items.Length || indices[^1] - indices[0] + 1 != items.Length) continue;   // something not at tbl/tr/tc/p (sdt, nested)
        if (items.Any(x => x.p.Cell?.Merged == true || x.p.Cell?.TableDepth > 1)) continue;
        var rows = items.GroupBy(x => int.Parse(x.m.Groups["row"].Value)).OrderBy(g => g.Key)
            .Select(g => g.GroupBy(x => int.Parse(x.m.Groups["cell"].Value)).OrderBy(c => c.Key).Select(c => c.Select(x => x.p).ToList()).ToList()).ToList();
        if (rows.Count < 3 || rows.Any(r => r.Count < 2)) continue;

        var other = pool[(pool.IndexOf(v) + 1 + rng.Next(pool.Count - 1)) % pool.Count];
        var foreignTexts = other.Paragraphs.Where(x => Words(x.NormalizedKey).Length >= 2).Select(x => x.RawText).ToArray();
        if (foreignTexts.Length < 5) continue;
        string Foreign() => foreignTexts[rng.Next(foreignTexts.Length)];

        var r = rng.Next(rows.Count - 1);
        var template = v.Paragraphs[indices[0]];
        // Each new row is a list of cells, each cell a list of (paragraph, origin) where origin null means new text.
        var newRows = rows.Select(row => row.Select(cell => cell.Select(p => (P: p, Origin: (string?)p.Location)).ToList()).ToList()).ToList();
        string expected, edit;
        switch (type)
        {
            case "table_row_insert":
                newRows.Insert(r + 1, rows[r].Select(_ => new List<(ParagraphFingerprint P, string? Origin)> { (template, null) }).ToList());
                expected = $"row_only_in_document:{table.Key}/tr[{r + 2}]";
                edit = $"row of foreign text inserted after row {r + 1}";
                break;
            case "table_row_delete":
                newRows.RemoveAt(r);
                expected = $"row_only_in_template:{table.Key}/tr[{r + 1}]";
                edit = $"row {r + 1} deleted";
                break;
            case "table_row_swap":
                (newRows[r], newRows[r + 1]) = (newRows[r + 1], newRows[r]);
                expected = $"row_moved:{table.Key}/tr[{r + 1}]|{table.Key}/tr[{r + 2}]";
                edit = $"rows {r + 1} and {r + 2} swapped";
                break;
            default:
                foreach (var row in newRows) row.Add([(template, null)]);
                expected = $"column_only_in_document:{table.Key}#col[";
                edit = "column of foreign text added to every row";
                break;
        }
        if (type == "table_row_swap" && rows[r].SelectMany(c => c).Select(p => p.KeyHash).SequenceEqual(rows[r + 1].SelectMany(c => c).Select(p => p.KeyHash))) continue;

        var origin = new Dictionary<string, string>(StringComparer.Ordinal);
        var rebuilt = new List<ParagraphFingerprint>();
        for (var ri = 0; ri < newRows.Count; ri++)
            for (var ci = 0; ci < newRows[ri].Count; ci++)
                for (var pi = 0; pi < newRows[ri][ci].Count; pi++)
                {
                    var (p, from) = newRows[ri][ci][pi];
                    var location = $"{table.Key}/tr[{ri + 1}]/tc[{ci + 1}]/p[{pi + 1}]";
                    rebuilt.Add(from is null ? Re(p, Foreign(), location) : p with { Location = location });
                    origin[location] = from ?? "inserted";
                }
        var ps = v.Paragraphs.ToList();
        ps.RemoveRange(indices[0], indices.Length);
        ps.InsertRange(indices[0], rebuilt);
        foreach (var p in v.Paragraphs.Where(p => !p.Location.StartsWith(table.Key + "/", StringComparison.Ordinal))) origin.TryAdd(p.Location, p.Location);
        return new Case(set, type, sources[pool.IndexOf(v)], table.Key, expected, edit, v, With(v, ps), origin);
    }
    return null;
}

static string Base(string location) => location.Split('#')[0];
// An inserted paragraph sits at the location of the one before it (so inside the same table cell); its origin is new text, not that paragraph.
static Dictionary<string, string> New(string location) => new(StringComparer.Ordinal) { [location] = "inserted" };
static string Parent(string location) => location[..location.LastIndexOf('/')];

Verdict Judge(Case c, ComparisonResult r)
{
    var groups = r.Changes.Where(x => x.Kind is "paragraph_split" or "paragraph_merged").ToArray();
    // Every one-to-one pair must join a template paragraph with its own (possibly edited or renumbered) counterpart.
    string OriginOf(string location) => c.Origin?.GetValueOrDefault(location) ?? Base(location);
    var mispaired = r.Changes.Any(x => x.Template is not null && x.Document is not null && x.TemplateGroup is null && x.DocumentGroup is null
        && OriginOf(x.Document.Location) != x.Template.Location);
    var structural = r.Changes.Where(x => x.Kind.StartsWith("row_", StringComparison.Ordinal) || x.Kind.StartsWith("column_", StringComparison.Ordinal)
        || x.Kind.StartsWith("table_only", StringComparison.Ordinal)).ToArray();
    if (StructuralTableTypes.Contains(c.Type))
    {
        var parts = c.Expected.Split(':', 2);
        bool Right(Change x) => x.Kind == parts[0] && parts[0] switch
        {
            "row_only_in_document" => x.Document!.Location == parts[1],
            "row_only_in_template" => x.Template!.Location == parts[1],
            "row_moved" => parts[1].Split('|').Contains(x.Template!.Location),
            _ => x.Document!.Location.StartsWith(parts[1], StringComparison.Ordinal),
        };
        return new Verdict(structural.Any(Right), false, false, groups.Length > 0 || structural.Any(x => !Right(x)), mispaired);
    }
    // Paragraph-level cases: any table, row or column change is unexpected.
    var verdict = ParagraphVerdict();
    return structural.Length > 0 ? verdict with { WrongGroup = true } : verdict;

    Verdict ParagraphVerdict()
    {
    switch (c.Type)
    {
        case "split_short_head": case "split_balanced": case "split_with_edit": case "split_multi_edit": case "table_cell_split":
        {
            bool Right(Change x) => x.Kind == "paragraph_split" && x.Template!.Location == c.Location
                && x.DocumentGroup!.Select(s => s.Location).SequenceEqual(new[] { c.Location + "#a", c.Location + "#b" });
            return new Verdict(groups.Any(Right), false, false, groups.Any(x => !Right(x)), mispaired);
        }
        case "merge": case "merge_multi_edit": case "table_cell_merge": case "merge_cross_parent":
        {
            var next = c.Expected.Split(':')[1];
            bool Right(Change x) => x.Kind == "paragraph_merged" && x.Document!.Location == c.Location + "#m"
                && x.TemplateGroup!.Select(s => s.Location).SequenceEqual(new[] { c.Location, next });
            return new Verdict(groups.Any(Right), false, false, groups.Any(x => !Right(x)), mispaired);
        }
        case "edit_only": case "table_cell_edit":
            return new Verdict(r.Changes.Any(x => x.Template?.Location == c.Location && x.Document?.Location == c.Location
                && x.MatchStatus == "paired_by_similarity"), false, false, groups.Length > 0, mispaired);
        default:
        {
            // An inserted copy: the right answer names the inserted occurrence. Naming another occurrence is acceptable only when flagged uncertain.
            var expected = c.Expected.Split(':', 2)[1];
            var oneSided = r.Changes.Where(x => x.Kind == "paragraph_only_in_document").ToArray();
            var found = oneSided.Any(x => x.Document!.Location == expected);
            var others = oneSided.Where(x => x.Document!.Location != expected).ToArray();
            return new Verdict(found, !found && others.Length > 0 && others.All(x => x.Uncertain),
                !found && others.Any(x => !x.Uncertain), groups.Length > 0, mispaired);
        }
    }
    }
}

(DocxVersion V, int I)? Pick(List<DocxVersion> pool, Random rng, int minWords, Func<ParagraphFingerprint, bool>? filter, int maxTries)
{
    for (var tries = 0; tries < maxTries; tries++)
    {
        var v = pool[rng.Next(pool.Count)];
        var keys = v.Paragraphs.GroupBy(p => p.KeyHash).ToDictionary(g => g.Key, g => g.Count());
        var i = rng.Next(v.Paragraphs.Count);
        var p = v.Paragraphs[i];
        if (Words(p.NormalizedKey).Length >= minWords && keys[p.KeyHash] == 1 && (filter is null || filter(p))) return (v, i);
    }
    return null;
}

// Same normalization and hash as DocxVersionReader: NFC, whitespace runs collapsed, trimmed; SHA-256 hex of UTF-8.
static string Norm(string raw) => Regex.Replace(raw.Normalize(NormalizationForm.FormC), @"\s+", " ").Trim();
static string Hash(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
static ParagraphFingerprint Re(ParagraphFingerprint p, string raw, string location) =>
    p with { RawText = raw, NormalizedKey = Norm(raw), KeyHash = Hash(Norm(raw)), Runs = [], LineBreakOffsets = [], Location = location };
static DocxVersion With(DocxVersion v, List<ParagraphFingerprint> ps) => v with { Paragraphs = ps.Select((p, i) => p with { Ordinal = i }).ToList() };
static string[] Words(string s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries);

partial class Program
{
    static readonly string[] BaseTypes = ["split_short_head", "split_balanced", "split_with_edit", "merge", "edit_only", "insert_unrelated", "insert_duplicate"];
    static readonly string[] HardTypes = ["short_repeated_phrase", "new_contains_old_words", "split_multi_edit", "merge_multi_edit"];
    static readonly string[] TableTypes = ["table_cell_edit", "table_cell_split", "table_cell_merge",
        "table_row_insert", "table_row_delete", "table_row_swap", "table_column_add"];
    static readonly HashSet<string> StructuralTableTypes = ["table_row_insert", "table_row_delete", "table_row_swap", "table_column_add"];
    static readonly Regex CellPath = new(@"^(?<table>body\[1\]/tbl\[\d+\])/tr\[(?<row>\d+)\]/tc\[(?<cell>\d+)\]/p\[(?<p>\d+)\]$", RegexOptions.Compiled);
}

record Case(string Set, string Type, string File, string Location, string Expected, string Edit, DocxVersion Template, DocxVersion Document,
    IReadOnlyDictionary<string, string>? Origin = null);
record Verdict(bool Found, bool AlternativeFlagged, bool AlternativeUnflagged, bool WrongGroup, bool Mispaired);
record Outcome(string Set, string Type, string File, string Location, string Expected, string Rule, Verdict Verdict, bool Flagged,
    bool DifferencesChanged, GroupDecision[]? RelevantDecisions, string[]? Changes);
