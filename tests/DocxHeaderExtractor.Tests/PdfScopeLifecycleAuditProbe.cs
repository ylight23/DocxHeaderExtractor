using System.Text;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// M10.3-A1 diagnostic probe. Describes what the scope tracker actually did on 092; it asserts
/// nothing about what it should have done, and changes nothing.
/// </summary>
public sealed class PdfScopeLifecycleAuditProbe
{
    [Fact]
    public void Report()
    {
        var output = Environment.GetEnvironmentVariable("M10_3_REPORT");
        if (output is null) return;

        var docx = Path.Combine(RepositoryRoot(),
            @"todo10_8\heading_corpus_95_word\07_system_generated\092_RFC9111_HTTP_Caching.docx");
        var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(docx);
        var trace = new List<StructuralScopeTransition>();
        PdfCandidateContextBuilder.Build(snapshot.CandidateBlocks, snapshot.Annotations, scopeTrace: trace);

        var report = new StringBuilder();
        void Line(string value) => report.AppendLine(value);

        Line($"status={snapshot.Audit.Status} blocks={snapshot.CandidateBlocks.Count} traced={trace.Count}");
        var pages = trace.Select(t => t.Page).DefaultIfEmpty(0);
        Line($"pages={pages.Min()}..{pages.Max()}");

        Line("");
        Line("-- incoming scope -> resulting scope");
        foreach (var group in trace.GroupBy(t => $"{t.IncomingScope} -> {t.ResultingScope}")
                     .OrderByDescending(g => g.Count()))
            Line($"{group.Key,-46} {group.Count()}");

        Line("");
        Line("-- appendix latch lifecycle");
        var triggers = trace.Where(t => t.AppendixTriggeredHere).ToArray();
        Line($"blocks matching the appendix pattern: {triggers.Length}");
        foreach (var entry in triggers.Take(20))
            Line($"  p{entry.Page} {entry.SourceId} incoming={entry.IncomingScope} \"{Trim(entry.RawText)}\"");
        var first = triggers.FirstOrDefault();
        if (first is not null)
        {
            Line($"first latch: p{first.Page} {first.SourceId} incoming={first.IncomingScope}");
            var after = trace.SkipWhile(t => t.SourceId != first.SourceId).ToArray();
            Line($"blocks after the first latch: {after.Length}");
            Line($"  of those, latch still on: {after.Count(t => t.AppendixLatched)}");
            var asAppendix = after.Count(t => t.ResultingScope == "appendix");
            var asAppendixTable = after.Count(t => t.ResultingScope == "appendix_table");
            Line($"  of those, resulting scope appendix: {asAppendix}");
            Line($"  of those, resulting scope appendix_table: {asAppendixTable}");
            Line($"  pages spanned after the first latch: " +
                 $"{after.Select(t => t.Page).DefaultIfEmpty(0).Min()}..{after.Select(t => t.Page).DefaultIfEmpty(0).Max()}");
        }
        Line($"blocks where the latch was ever off after being on: " +
             $"{CountResets(trace)}");

        Line("");
        Line("-- scope by page");
        foreach (var group in trace.GroupBy(t => t.Page).OrderBy(g => g.Key))
        {
            var counts = group.GroupBy(t => t.ResultingScope)
                .OrderByDescending(g => g.Count())
                .Select(g => $"{g.Key}={g.Count()}");
            Line($"p{group.Key,-4} {string.Join(" ", counts)}");
        }

        Line("");
        Line("-- blocks the latch relabelled, by page (incoming document_body or table)");
        foreach (var group in trace
                     .Where(t => t.ResultingScope is "appendix" or "appendix_table")
                     .GroupBy(t => t.Page).OrderBy(g => g.Key))
            Line($"p{group.Key,-4} {group.Count()}");

        Line("");
        Line("-- sample of relabelled blocks");
        foreach (var entry in trace.Where(t => t.ResultingScope is "appendix" or "appendix_table").Take(30))
            Line($"  p{entry.Page} {entry.SourceId} {entry.IncomingScope}->{entry.ResultingScope} \"{Trim(entry.RawText)}\"");

        File.WriteAllText(output, report.ToString());
    }

    private static int CountResets(IReadOnlyList<StructuralScopeTransition> trace)
    {
        var seenOn = false;
        var resets = 0;
        foreach (var entry in trace)
        {
            if (seenOn && !entry.AppendixLatched) resets++;
            if (entry.AppendixLatched) seenOn = true;
        }
        return resets;
    }

    private static string Trim(string value)
    {
        var single = Regex.Replace(value, @"\s+", " ").Trim();
        return single.Length <= 100 ? single : single[..100] + "...";
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DocxHeaderExtractor.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? AppContext.BaseDirectory;
    }
}
