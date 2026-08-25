using System.Text;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// M10.4-A1 diagnostic probe. Asks one question of the existing quote state machine on 092: which
/// source fact sets the latch, how far it persists, and what exit the code actually offers.
/// <para>
/// It reports the open and close conditions the tracker evaluated and the raw quote-character counts
/// they were computed from, because the two conditions do not read the same characters - a block can
/// fail to close either by carrying no closing evidence or by being unable to produce any. It defines
/// no quote boundary of its own.
/// </para>
/// <para>
/// A transition is not called false here because its text looks wrong. Whether the page 28
/// transition is a defect is a reviewed judgement, and A1 only lays out the evidence for it.
/// </para>
/// </summary>
public sealed class PdfQuoteLifecycleAuditProbe
{
    [Fact]
    public void Report()
    {
        var output = Environment.GetEnvironmentVariable("M10_4_REPORT");
        if (output is null) return;

        var docx = Path.Combine(RepositoryRoot(),
            @"todo10_8\heading_corpus_95_word\07_system_generated\092_RFC9111_HTTP_Caching.docx");
        var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(docx);
        var trace = new List<StructuralScopeTransition>();
        PdfCandidateContextBuilder.Build(snapshot.CandidateBlocks, snapshot.Annotations, scopeTrace: trace);

        var report = new StringBuilder();
        void Line(string value) => report.AppendLine(value);

        Line($"blocks={trace.Count} pages={trace.Min(t => t.Page)}..{trace.Max(t => t.Page)}");

        Line("");
        Line("-- 1. trigger: every block whose open condition held");
        var opens = trace.Where(t => t.QuoteOpened).ToArray();
        Line($"count: {opens.Length}");
        foreach (var entry in opens.Take(25))
            Line($"  p{entry.Page,-3} {entry.SourceId,-12} before={entry.QuoteStateBefore,-5} " +
                 $"closed={entry.QuoteClosed,-5} left={entry.LeftCurlyQuotes} right={entry.RightCurlyQuotes} " +
                 $"straight={entry.StraightQuotes} {Trim(entry.RawText)}");

        var first = trace.FirstOrDefault(t => t.QuoteOpened && !t.QuoteClosed);
        Line("");
        Line("-- 2. persistence");
        if (first is null)
        {
            Line("no block both opened and failed to close; not measured");
        }
        else
        {
            Line($"first block that opened without closing: p{first.Page} {first.SourceId}");
            Line($"  text: {Trim(first.RawText)}");
            var after = trace.SkipWhile(t => t.SourceId != first.SourceId).ToArray();
            Line($"blocks from there to the end: {after.Length}, pages {after.Min(t => t.Page)}..{after.Max(t => t.Page)}");
            Line($"  resulting scope quoted_replacement: {after.Count(t => t.ResultingScope == "quoted_replacement")}");
            Line($"  resulting scope embedded_amendment: {after.Count(t => t.ResultingScope == "embedded_amendment")}");
            Line($"  blocks after it whose close condition held: {after.Count(t => t.QuoteClosed)}");
        }

        Line("");
        Line("-- 3. exit: what the close condition could have fired on");
        Line($"blocks whose close condition held anywhere in the document: {trace.Count(t => t.QuoteClosed)}");
        Line($"blocks containing a closing curly quote: {trace.Count(t => t.RightCurlyQuotes > 0)}");
        Line($"blocks containing an opening curly quote: {trace.Count(t => t.LeftCurlyQuotes > 0)}");
        Line($"blocks containing a straight quote character: {trace.Count(t => t.StraightQuotes > 0)}");
        Line($"straight quote characters in the document: {trace.Sum(t => t.StraightQuotes)}");
        Line($"curly quote characters in the document: " +
             $"{trace.Sum(t => t.LeftCurlyQuotes)} opening, {trace.Sum(t => t.RightCurlyQuotes)} closing");

        Line("");
        Line("-- 4. the pages that matter");
        foreach (var page in new[] { 27, 28, 29, 31, 32, 33 })
        {
            var onPage = trace.Where(t => t.Page == page).ToArray();
            if (onPage.Length == 0) continue;
            Line($"p{page}: {string.Join(" ", onPage.GroupBy(t => t.ResultingScope).Select(g => $"{g.Key}={g.Count()}"))}");
            foreach (var entry in onPage.Where(t => t.QuoteOpened || t.QuoteClosed))
                Line($"    {entry.SourceId,-12} opened={entry.QuoteOpened,-5} closed={entry.QuoteClosed,-5} " +
                     $"left={entry.LeftCurlyQuotes} right={entry.RightCurlyQuotes} straight={entry.StraightQuotes} " +
                     $"{Trim(entry.RawText)}");
        }

        Line("");
        Line("-- 5. the real appendix triggers on page 32, as the quote state found them");
        foreach (var entry in trace.Where(t => t.AppendixTriggeredHere))
            Line($"  p{entry.Page,-3} {entry.SourceId,-12} incoming={entry.IncomingScope,-18} " +
                 $"resulting={entry.ResultingScope,-20} quoteBefore={entry.QuoteStateBefore} " +
                 $"{Trim(entry.RawText)}");

        File.WriteAllText(output, report.ToString());
    }

    private static string Trim(string value)
    {
        var single = Regex.Replace(value, @"\s+", " ").Trim();
        return single.Length <= 90 ? single : single[..90] + "...";
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DocxHeaderExtractor.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? AppContext.BaseDirectory;
    }
}
