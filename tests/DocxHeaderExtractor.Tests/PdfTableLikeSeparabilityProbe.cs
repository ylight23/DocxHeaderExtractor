using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// M10.3-B3 diagnostic probe. Asks one question of a fixed population: do any facts the pipeline
/// already produces separate the 37 reviewed outline headings from the 88 other lines the
/// `short_numbered` branch marks?
/// <para>
/// It evaluates only facts that already exist. It defines no predicate of its own, tries no regex,
/// and combines no features - searching a space of invented combinations until one fits 092 would be
/// heuristic mining with an extra step, and would produce a rule that is fitted rather than found.
/// </para>
/// <para>
/// Both sides are always reported. A fact that catches 30 of 37 headings while also catching 50 of
/// 88 non-headings is not a discriminator, and a single-column table would hide that.
/// </para>
/// </summary>
public sealed class PdfTableLikeSeparabilityProbe
{
    private const string WithheldScopeTransition = "b43";

    [Fact]
    public void Report()
    {
        var output = Environment.GetEnvironmentVariable("M10_3_B3_REPORT");
        if (output is null) return;

        var docx = Path.Combine(RepositoryRoot(),
            @"todo10_8\heading_corpus_95_word\07_system_generated\092_RFC9111_HTTP_Caching.docx");
        var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(docx);
        var contexts = PdfCandidateContextBuilder.Build(snapshot.CandidateBlocks, snapshot.Annotations,
            withheldAppendixEntries: new HashSet<string>(StringComparer.Ordinal) { WithheldScopeTransition });
        var rankedList = PdfCandidateRanker.Rank(snapshot.CandidateBlocks, contexts);
        var ranked = rankedList.ToDictionary(item => item.SourceId, StringComparer.Ordinal);
        var rankOf = rankedList.Select((item, index) => (item.SourceId, Rank: index))
            .ToDictionary(x => x.SourceId, x => x.Rank, StringComparer.Ordinal);

        var labelPath = Path.Combine(RepositoryRoot(), "eval", "manual-labels",
            "092-short-numbered-line-labels.v1.json");
        using var document = JsonDocument.Parse(File.ReadAllText(labelPath));
        var labels = document.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => (
                Page: item.GetProperty("page").GetInt32(),
                Readable: item.GetProperty("readable").GetString() ?? "",
                Role: item.GetProperty("role").GetString() ?? ""))
            .ToArray();

        var annotationByKey = new Dictionary<string, PdfLineBlockAnnotation>(StringComparer.Ordinal);
        foreach (var annotation in snapshot.Annotations)
        {
            var key = Key(annotation.Line.Page, PdfTextUtilities.Readable(annotation.Line.Text));
            if (!annotationByKey.ContainsKey(key)) annotationByKey[key] = annotation;
        }
        var blocksByKey = new Dictionary<string, List<PdfSemanticBlock>>(StringComparer.Ordinal);
        foreach (var block in snapshot.CandidateBlocks)
            foreach (var line in block.Lines)
            {
                var key = Key(line.Page, PdfTextUtilities.Readable(line.Text));
                if (!blocksByKey.TryGetValue(key, out var list)) blocksByKey[key] = list = [];
                list.Add(block);
            }

        var rows = labels.Select(label =>
        {
            var key = Key(label.Page, label.Readable);
            var annotation = annotationByKey.GetValueOrDefault(key);
            var block = blocksByKey.TryGetValue(key, out var list)
                ? list.OrderBy(b => rankOf.GetValueOrDefault(b.Id, int.MaxValue)).First()
                : null;
            var candidate = block is null ? null : ranked.GetValueOrDefault(block.Id);
            var text = annotation?.Line.Text ?? label.Readable;
            return new Row(
                label.Role,
                Outline: label.Role == "outline_heading",
                HasStructuralMarker: PdfLineBlockAnnotation.HasStructuralMarker(text),
                StrictMarker: PdfMarkerFactsParser.Parse(text) is not null,
                GenericNumbering: NumberingAudit.Parse(text) is not null,
                LooseLabelledMarker: PdfLayoutEvidenceOutline.ParseLooseLabelledMarkerForAudit(text) is not null,
                Repeated: annotation?.Repeated ?? false,
                HeaderFooterZone: annotation?.HeaderFooterZone ?? false,
                Standalone: block?.LineCount == 1,
                Positive: candidate?.PositiveSignals ?? [],
                Ambiguity: candidate?.AmbiguitySignals ?? [],
                Joined: block is not null);
        }).ToArray();

        var report = new StringBuilder();
        void Line(string value) => report.AppendLine(value);

        Line("population=125 reviewed short_numbered lines, fixed; corrected-scope world");
        Line("facts audited: existing pipeline facts only; no new predicate, no regex, no combination");
        Line($"joined to a candidate block: {rows.Count(r => r.Joined)}/{rows.Length}");

        var roles = new[] { "toc_entry", "table_cell_or_tabular_value", "body_prose", "metadata", "caption_or_other_structural" };

        Line("");
        Line("-- separability of each existing fact");
        Line($"{"fact",-34} {"outline",8} {"of 37",7} {"non-outline",12} {"of 88",7}");
        foreach (var (name, selector) in Facts())
        {
            var outline = rows.Count(r => r.Outline && selector(r));
            var other = rows.Count(r => !r.Outline && selector(r));
            Line($"{name,-34} {outline,8} {"",7} {other,12} {"",7}");
        }

        Line("");
        Line("-- the same facts broken out by what the non-outline lines actually are");
        Line($"{"fact",-34} {"outline",8} " + string.Join(" ", roles.Select(r => $"{Short(r),12}")));
        foreach (var (name, selector) in Facts())
        {
            var cells = roles.Select(role => $"{rows.Count(r => r.Role == role && selector(r)),12}");
            Line($"{name,-34} {rows.Count(r => r.Outline && selector(r)),8} " + string.Join(" ", cells));
        }

        Line("");
        Line("-- role totals, for reading the tables above");
        Line($"{"outline_heading",-34} {rows.Count(r => r.Outline),8}");
        foreach (var role in roles)
            Line($"{role,-34} {rows.Count(r => r.Role == role),8}");

        Line("");
        Line("-- existing ranking signals, by role");
        var signalNames = rows.SelectMany(r => r.Positive.Concat(r.Ambiguity)).Distinct().OrderBy(x => x).ToArray();
        Line($"{"signal",-34} {"outline",8} " + string.Join(" ", roles.Select(r => $"{Short(r),12}")));
        foreach (var signal in signalNames)
        {
            bool Has(Row r) => r.Positive.Contains(signal) || r.Ambiguity.Contains(signal);
            var cells = roles.Select(role => $"{rows.Count(r => r.Role == role && Has(r)),12}");
            Line($"{signal,-34} {rows.Count(r => r.Outline && Has(r)),8} " + string.Join(" ", cells));
        }

        File.WriteAllText(output, report.ToString());
    }

    private static (string Name, Func<Row, bool> Selector)[] Facts() =>
    [
        ("HasStructuralMarker", r => r.HasStructuralMarker),
        ("strict marker parse", r => r.StrictMarker),
        ("generic numbering parse", r => r.GenericNumbering),
        ("loose labelled marker", r => r.LooseLabelledMarker),
        ("annotation repeated", r => r.Repeated),
        ("annotation header/footer zone", r => r.HeaderFooterZone),
        ("block is a single line", r => r.Standalone),
    ];

    private sealed record Row(
        string Role,
        bool Outline,
        bool HasStructuralMarker,
        bool StrictMarker,
        bool GenericNumbering,
        bool LooseLabelledMarker,
        bool Repeated,
        bool HeaderFooterZone,
        bool Standalone,
        IReadOnlyList<string> Positive,
        IReadOnlyList<string> Ambiguity,
        bool Joined);

    private static string Short(string role) => role switch
    {
        "toc_entry" => "toc",
        "table_cell_or_tabular_value" => "table",
        "body_prose" => "prose",
        "metadata" => "meta",
        "caption_or_other_structural" => "caption",
        _ => role,
    };

    private static string Key(int page, string readable) =>
        $"{page}|{Regex.Replace(readable, @"\s+", " ").Trim()}";

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DocxHeaderExtractor.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? AppContext.BaseDirectory;
    }
}
