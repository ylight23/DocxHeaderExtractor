using System.Text;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// M10.2 diagnostic probe. Describes what the production DOCX alignment actually did on 010; it
/// asserts nothing about what it should have done.
/// </summary>
public sealed class PdfDocxGroundingAuditProbe
{
    [Fact]
    public void Report()
    {
        var output = Environment.GetEnvironmentVariable("M10_2_REPORT");
        if (output is null) return;

        var docx = Path.Combine(RepositoryRoot(),
            @"todo10_8\heading_corpus_95_word\01_phap_quy\010_Luat_An_ninh_mang_24-2018-QH14.docx");
        var slim = new DocxSlimExtractor().Extract(docx);
        var snapshot = PdfLayoutEvidenceOutline.BuildDocxAlignmentSnapshot(docx, slim,
            PdfDocxAlignmentPopulation.RetrievalPopulation);
        var text = slim.Paragraphs.ToDictionary(p => p.Index, p => p.Text ?? "");

        var report = new StringBuilder();
        void Line(string value) => report.AppendLine(value);

        Line($"status={snapshot.Status} candidates={snapshot.CandidateCount} " +
             $"traced={snapshot.Trace.Count} aligned={snapshot.Headings.Count}");

        var grounded = snapshot.Trace.Where(t => t.ParagraphIndex is not null).ToArray();
        var accepted = grounded.Where(t => t.Accepted).ToArray();
        Line($"unmatched={snapshot.Trace.Count - grounded.Length} " +
             $"matched_then_dropped_as_duplicate={grounded.Length - accepted.Length}");

        Line("");
        Line("-- branch usage (accepted)");
        foreach (var group in accepted.GroupBy(t => t.Branch).OrderByDescending(g => g.Count()))
            Line($"{group.Key,-22} {group.Count()}");

        Line("");
        Line("-- mapping shape");
        var byParagraph = accepted.GroupBy(t => t.ParagraphIndex!.Value).ToArray();
        Line($"distinct_paragraphs={byParagraph.Length}");
        Line($"paragraphs_with_more_than_one_block={byParagraph.Count(g => g.Count() > 1)}");
        Line($"max_blocks_per_paragraph={(byParagraph.Length == 0 ? 0 : byParagraph.Max(g => g.Count()))}");
        foreach (var group in byParagraph.Where(g => g.Count() > 1).OrderByDescending(g => g.Count()))
        {
            Line($"  p{group.Key} x{group.Count()}: {Trim(text.GetValueOrDefault(group.Key, ""))}");
            foreach (var entry in group.OrderBy(e => e.Start))
                Line($"    {entry.SourceBlockId} @{entry.Start}+{entry.Length} needle={Trim(entry.Needle)}");
        }

        Line("");
        Line("-- match shape (accepted)");
        var shapes = accepted.Select(entry =>
        {
            var haystack = text.GetValueOrDefault(entry.ParagraphIndex!.Value, "");
            var start = entry.Start!.Value;
            var length = entry.Length!.Value;
            if (start + length > haystack.Length) return "span_outside_paragraph_text";
            var whole = haystack.Trim().Length == haystack.Substring(start, length).Trim().Length;
            if (whole) return "whole_paragraph";
            var before = start == 0 || !char.IsLetterOrDigit(haystack[start - 1]);
            var after = start + length >= haystack.Length || !char.IsLetterOrDigit(haystack[start + length]);
            return before && after ? "substring_word_bounded" : "substring_mid_word";
        }).ToArray();
        foreach (var group in shapes.GroupBy(s => s).OrderByDescending(g => g.Count()))
            Line($"{group.Key,-28} {group.Count()}");

        Line("");
        Line("-- needle length (accepted)");
        foreach (var group in accepted.GroupBy(t => Bucket(t.Needle.Length)).OrderBy(g => g.Key))
            Line($"{group.Key,-10} {group.Count()}");
        foreach (var entry in accepted.Where(t => t.Needle.Length <= 8).OrderBy(t => t.Needle.Length))
            Line($"  short: {entry.SourceBlockId} len={entry.Needle.Length} needle=\"{entry.Needle}\" " +
                 $"-> p{entry.ParagraphIndex} @{entry.Start} in \"{Trim(text.GetValueOrDefault(entry.ParagraphIndex!.Value, ""))}\"");

        Line("");
        Line("-- needle ambiguity (how many paragraphs the needle occurs in at all)");
        var ambiguity = accepted
            .Select(entry => (entry, count: snapshot.Haystacks.Count(h => h.CanonicalText.Contains(entry.Needle, StringComparison.Ordinal))))
            .ToArray();
        foreach (var group in ambiguity.GroupBy(a => a.count switch { 1 => "unique", <= 3 => "2-3", <= 10 => "4-10", _ => ">10" })
                     .OrderByDescending(g => g.Count()))
            Line($"{group.Key,-10} {group.Count()}");
        foreach (var item in ambiguity.Where(a => a.count > 1).OrderByDescending(a => a.count).Take(15))
            Line($"  {item.entry.SourceBlockId} in {item.count} paragraphs, len={item.entry.Needle.Length}, " +
                 $"chose p{item.entry.ParagraphIndex} @{item.entry.Start} needle=\"{Trim(item.entry.Needle)}\"");

        Line("");
        Line("-- matches starting or ending inside a word");
        foreach (var entry in accepted)
        {
            var haystack = text.GetValueOrDefault(entry.ParagraphIndex!.Value, "");
            var start = entry.Start!.Value;
            var length = entry.Length!.Value;
            if (start + length > haystack.Length) continue;
            var before = start == 0 || !char.IsLetterOrDigit(haystack[start - 1]);
            var after = start + length >= haystack.Length || !char.IsLetterOrDigit(haystack[start + length]);
            if (before && after) continue;
            Line($"  {entry.SourceBlockId} p{entry.ParagraphIndex} @{start}+{length} " +
                 $"\"{Trim(haystack.Substring(start, length))}\" in \"{Trim(haystack)}\"");
        }

        Line("");
        Line("-- paragraph order and reuse distance");
        var ordered = accepted.ToArray();
        var backwards = 0;
        var distances = new List<int>();
        for (var i = 1; i < ordered.Length; i++)
        {
            var delta = ordered[i].ParagraphIndex!.Value - ordered[i - 1].ParagraphIndex!.Value;
            if (delta < 0) backwards++;
            distances.Add(delta);
        }
        Line($"blocks_landing_before_their_predecessor={backwards}/{Math.Max(0, ordered.Length - 1)}");
        if (distances.Count > 0)
            Line($"paragraph_delta min={distances.Min()} max={distances.Max()} " +
                 $"median={distances.OrderBy(d => d).ElementAt(distances.Count / 2)}");

        Line("");
        Line("-- unmatched blocks");
        foreach (var entry in snapshot.Trace.Where(t => t.ParagraphIndex is null))
            Line($"{entry.SourceBlockId} needle=\"{Trim(entry.Needle)}\"");

        File.WriteAllText(output, report.ToString());
    }

    private static string Bucket(int length) => length switch
    {
        <= 4 => "<=4",
        <= 8 => "5-8",
        <= 16 => "9-16",
        <= 32 => "17-32",
        _ => ">32",
    };

    private static string Trim(string value)
    {
        var single = Regex.Replace(value, @"\s+", " ").Trim();
        return single.Length <= 110 ? single : single[..110] + "...";
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DocxHeaderExtractor.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? AppContext.BaseDirectory;
    }
}
