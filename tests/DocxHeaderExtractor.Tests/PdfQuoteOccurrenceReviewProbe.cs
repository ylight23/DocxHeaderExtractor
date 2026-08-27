using System.Text;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// M10.4-A2a review aid. Prints the source lines around the block that first sets the quote latch,
/// with their quote counts, so the claim that the odd parity comes from line segmentation can be
/// checked against the source rather than assumed from one line in isolation.
/// </summary>
public sealed class PdfQuoteOccurrenceReviewProbe
{
    [Fact]
    public void Report()
    {
        var output = Environment.GetEnvironmentVariable("M10_4_REVIEW");
        if (output is null) return;

        var docx = Path.Combine(RepositoryRoot(),
            @"todo10_8\heading_corpus_95_word\07_system_generated\092_RFC9111_HTTP_Caching.docx");
        var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(docx);

        var report = new StringBuilder();
        void Line(string value) => report.AppendLine(value);

        var onPage = snapshot.Lines
            .Select((line, index) => (Index: index, Line: line))
            .Where(x => x.Line.Page == 28)
            .ToArray();

        Line("page 28 source lines, in extraction order");
        Line($"{"idx",6} {"straight",9} {"lcurly",7} {"rcurly",7}  text");
        var running = 0;
        foreach (var (index, line) in onPage)
        {
            var straight = line.Text.Count(c => c == '"');
            var left = line.Text.Count(c => c == '“');
            var right = line.Text.Count(c => c is '”' or '‟');
            running += straight;
            Line($"{index,6} {straight,9} {left,7} {right,7}  {Trim(line.Text)}");
        }
        Line($"straight quotes on page 28: {running} (even means every opening one closes on this page)");

        Line("");
        Line("-- the line the tracker opened on, and its neighbours joined");
        var target = onPage.FirstOrDefault(x => x.Line.Text.Contains("HTTP Semantics", StringComparison.Ordinal));
        if (target.Line is null) { Line("target line not found"); }
        else
        {
            var window = onPage.Where(x => Math.Abs(x.Index - target.Index) <= 2).OrderBy(x => x.Index).ToArray();
            var joined = string.Join(" ", window.Select(x => x.Line.Text));
            Line(Trim(joined, 600));
            Line($"straight quotes across that window: {joined.Count(c => c == '"')}");
        }

        File.WriteAllText(output, report.ToString());
    }

    private static string Trim(string value, int max = 120)
    {
        var single = Regex.Replace(value, @"\s+", " ").Trim();
        return single.Length <= max ? single : single[..max] + "...";
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DocxHeaderExtractor.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? AppContext.BaseDirectory;
    }
}
