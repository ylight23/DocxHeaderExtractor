using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DocxHeaderExtractor.HeadingAgent.Scan;

/// <summary>
/// Scores the headings of a scan result against a gold of which headings the document has (<c>SCAN_HEADING_GOLD_V1</c>): how many required
/// headings were found (recall), how many selected headings are headings at all (precision), how many came out split into a label and a title,
/// and whether the text of each found heading is the gold's. A heading with a label (Chương II, Mục 1, Điều 15) is matched by the label's
/// kind and number on its page; one without (a title alone, the title block) by its letters against the gold's on the page, or against the
/// start of the gold's when only the first line of a longer title was selected (matched, its text not exact). It reads a result
/// file as the CLI writes it and changes nothing.
/// </summary>
public static partial class ScanHeadingEvaluation
{
    public const string GoldVersion = "SCAN_HEADING_GOLD_V1", ReportVersion = "SCAN_HEADING_EVAL_V1";
    public const double TitleSimilarity = 0.8;

    public sealed record GoldHeading(int N, int Page, string? Label, string Text, bool Required);
    public sealed record ResultHeading(int Index, int Page, string Text);

    /// <summary>A result heading and what it was matched to: a gold heading (its number), a part of one (a split title), or nothing.</summary>
    public sealed record Match(int Index, int Page, string Text, int? Gold, string Kind, bool TextExact);

    public sealed record Report(string Version, int Required, int RequiredFound, int AcceptableFound, int Selected, int SelectedMatched,
        int SplitParts, int FalsePositives, int TextExact, double Recall, double Precision,
        IReadOnlyList<GoldHeading> Missed, IReadOnlyList<Match> Extra, IReadOnlyList<Match> NotExact);

    public static IReadOnlyList<GoldHeading> ReadGold(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
        if (doc.RootElement.GetProperty("version").GetString() != GoldVersion) throw new InvalidOperationException("GOLD_VERSION_INVALID");
        return doc.RootElement.GetProperty("headings").EnumerateArray().Select(h => new GoldHeading(h.GetProperty("n").GetInt32(), h.GetProperty("page").GetInt32(),
            h.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null, h.GetProperty("text").GetString()!,
            h.GetProperty("kind").GetString() == "required")).ToArray();
    }

    /// <summary>The headings of a result file written by <c>dhx scan-extract</c>: index, the page of its first part, its text.</summary>
    public static IReadOnlyList<ResultHeading> ReadResult(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
        JsonElement Field(JsonElement item, string legacy, string production) =>
            item.TryGetProperty(legacy, out var value) ? value : item.GetProperty(production);
        return Field(doc.RootElement, "Headings", "headings").EnumerateArray().Select(h => new ResultHeading(
            Field(h, "Index", "index").GetInt32(), Field(Field(h, "Parts", "scanSources")[0], "Page", "page").GetInt32(),
            Field(h, "Text", "text").GetString()!)).ToArray();
    }

    [GeneratedRegex(@"^\W*(Chương|Mục|Điều|Phần)\s*([IVXLC]+|\d+)(?![IVXLC\d])", RegexOptions.CultureInvariant)]
    private static partial Regex LabelPattern();

    /// <summary>The label a text opens with, as kind and number ("điều 15", "chương ii"); null when it opens with none.</summary>
    public static string? LabelOf(string text)
    {
        var m = LabelPattern().Match(ScanTranscription.Norm(text));
        return m.Success ? (m.Groups[1].Value + " " + m.Groups[2].Value).ToLowerInvariant() : null;
    }

    public static Report Score(IReadOnlyList<GoldHeading> gold, IReadOnlyList<ResultHeading> result)
    {
        var used = new HashSet<int>();
        var matches = new List<Match>();
        foreach (var r in result)
        {
            var label = LabelOf(r.Text);
            var skeleton = ScanTranscription.Skeleton(r.Text);
            GoldHeading? hit = null;
            var kind = "extra";
            if (label is not null)
            {
                hit = gold.FirstOrDefault(g => g.Label is not null && LabelOf(g.Label) == label && Math.Abs(g.Page - r.Page) <= 1 && !used.Contains(g.N));
                if (hit is not null) kind = "heading";
                else if (gold.Any(g => g.Label is not null && LabelOf(g.Label) == label && Math.Abs(g.Page - r.Page) <= 1)) kind = "duplicate";
            }
            else if (skeleton.Length > 0)
            {
                // A heading without a label: the title block, or a title the agent kept apart from its label.
                hit = gold.FirstOrDefault(g => g.Label is null && Math.Abs(g.Page - r.Page) <= 1 && !used.Contains(g.N)
                    && ScanTranscription.Similarity(ScanTranscription.Skeleton(g.Text), skeleton) >= TitleSimilarity)
                    // A title of two lines selected by its first line only: the right heading, its text not exact.
                    ?? gold.FirstOrDefault(g => g.Label is null && Math.Abs(g.Page - r.Page) <= 1 && !used.Contains(g.N)
                        && ScanTranscription.Skeleton(g.Text) is var whole && skeleton.Length >= Math.Max(12, whole.Length / 2) && skeleton.Length < whole.Length
                        && ScanTranscription.Similarity(whole[..skeleton.Length], skeleton) >= TitleSimilarity);
                if (hit is not null) kind = "heading";
                else
                {
                    var whole = gold.FirstOrDefault(g => g.Label is not null && Math.Abs(g.Page - r.Page) <= 1 && skeleton.Length >= 6
                        && ScanTranscription.Skeleton(g.Text).Contains(skeleton, StringComparison.Ordinal));
                    if (whole is not null) { matches.Add(new Match(r.Index, r.Page, r.Text, whole.N, "split-part", false)); continue; }
                }
            }
            if (hit is not null && kind == "heading") used.Add(hit.N);
            matches.Add(new Match(r.Index, r.Page, r.Text, kind == "heading" ? hit!.N : null, kind,
                kind == "heading" && ScanTranscription.Norm(r.Text) == ScanTranscription.Norm(hit!.Text)));
        }
        var required = gold.Count(g => g.Required);
        var requiredFound = gold.Count(g => g.Required && used.Contains(g.N));
        var matched = matches.Count(m => m.Kind is "heading" or "split-part");
        return new Report(ReportVersion, required, requiredFound, gold.Count(g => !g.Required && used.Contains(g.N)), result.Count, matched,
            matches.Count(m => m.Kind == "split-part"), matches.Count(m => m.Kind is "extra" or "duplicate"), matches.Count(m => m.TextExact),
            required == 0 ? 0 : Math.Round((double)requiredFound / required, 4), result.Count == 0 ? 0 : Math.Round((double)matched / result.Count, 4),
            gold.Where(g => g.Required && !used.Contains(g.N)).ToArray(), matches.Where(m => m.Kind is "extra" or "duplicate").ToArray(),
            matches.Where(m => m.Kind == "heading" && !m.TextExact).ToArray());
    }
}
