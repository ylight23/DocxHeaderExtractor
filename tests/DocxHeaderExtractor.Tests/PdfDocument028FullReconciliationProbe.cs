using System.Text;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// B2 for 028, scaled from the Section I pilot (exact 49/49 reconciliation, validated by a manual
/// spot-check of four pages before this was trusted to run at scale - see
/// <see cref="PdfDocument028Section1PilotProbe"/>). Same method, generalized: a dot-leader TOC
/// inventory and an independent body-marker inventory, each scanned over a section's whole span (no
/// need to pinpoint where a local TOC ends and body begins - the two patterns cannot collide), then
/// reconciled by clause number. TOC is discovery only, never assumed exhaustive; gold points at the
/// body occurrence.
/// <para>
/// Section IV (Bidding Forms) and Section X (Contract Forms) are scanned for Section-level identity
/// only, not clause-level: 032's own reviewed labels (same WB procurement family) confirm numbered
/// entries there are <c>form_field</c>, not <c>outline_heading</c> - "1. Proposer's Legal Name" is a
/// fill-in prompt, not a document-structure heading.
/// </para>
/// </summary>
public sealed class PdfDocument028FullReconciliationProbe
{
    private const string DocxRelativePath = @"todo10_8\heading_corpus_95_word\02_hop_dong_mua_sam\028_WB_RFB_Works_Without_Prequal_2017.docx";

    // (name, span start index, span end index exclusive, scan clause-level markers)
    private static readonly (string Name, int Start, int End, bool ScanClauses)[] Sections =
    [
        ("Section I", 370, 1472, true),
        ("Section II", 1472, 1887, true),
        ("Section III", 1887, 2446, true),
        ("Section IV", 2446, 3813, false),
        ("Section V", 3813, 3823, true),
        ("Section VI", 3823, 3908, true),
        ("Section VII", 3910, 4088, true),
        ("Section VIII", 4297, 9282, true),
        ("Section IX", 9282, 9700, true),
    ];

    [Fact]
    public void Report()
    {
        var output = Environment.GetEnvironmentVariable("BENCH_028_FULL_REPORT");
        if (string.IsNullOrWhiteSpace(output)) return;

        var docxPath = Path.Combine(PdfExtractorQualityBenchmarkProbe.RepositoryRoot(), DocxRelativePath);
        var lines = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(docxPath).Lines;

        var report = new StringBuilder();
        void Line(string value) => report.AppendLine(value);

        var totalMatched = 0;
        var totalTocOnly = 0;
        var totalBodyOnly = 0;
        var totalDuplicates = 0;

        foreach (var (name, start, end, scanClauses) in Sections)
        {
            // Section X runs to the end of the document; every other section's end is the next
            // section's own start index, already exclusive.
            var effectiveEnd = end;

            var toc = scanClauses ? BuildTocInventory(lines, start, effectiveEnd) : [];
            var body = scanClauses ? BuildBodyInventory(lines, start, effectiveEnd) : [];

            var tocMarkers = toc.Select(t => t.Marker).ToHashSet(StringComparer.Ordinal);
            var bodyMarkers = body.Select(b => b.Marker).ToHashSet(StringComparer.Ordinal);
            var byMarker = body.ToLookup(b => b.Marker, StringComparer.Ordinal);

            var matched = toc.Where(t => bodyMarkers.Contains(t.Marker)).ToList();
            var tocOnly = toc.Where(t => !bodyMarkers.Contains(t.Marker)).ToList();
            var bodyOnly = body.Where(b => !tocMarkers.Contains(b.Marker)).ToList();
            var duplicates = byMarker.Where(g => g.Count() > 1).ToList();

            totalMatched += matched.Count;
            totalTocOnly += tocOnly.Count;
            totalBodyOnly += bodyOnly.Count;
            totalDuplicates += duplicates.Count;

            Line($"{name} [{start},{effectiveEnd}) scanClauses={scanClauses}: toc={toc.Count} body={body.Count} " +
                 $"matched={matched.Count} tocOnly={tocOnly.Count} bodyOnly={bodyOnly.Count} duplicates={duplicates.Count}");

            foreach (var t in tocOnly) Line($"    TOC-ONLY [{t.Marker}] {t.Title} (tocIndex={t.Index}, page={lines[t.Index].Page})");
            foreach (var b in bodyOnly) Line($"    BODY-ONLY [{b.Marker}] index={b.Index} page={lines[b.Index].Page}: {Trim(b.Text)}");
            foreach (var group in duplicates)
            {
                Line($"    DUPLICATE marker={group.Key}");
                foreach (var b in group) Line($"      index={b.Index} page={lines[b.Index].Page}: {Trim(b.Text)}");
            }
        }

        Line("");
        Line($"TOTAL matched={totalMatched} tocOnly={totalTocOnly} bodyOnly={totalBodyOnly} duplicates={totalDuplicates}");

        File.WriteAllText(output, report.ToString());
    }

    private sealed record TocEntry(string Marker, string Title, int Index);
    private sealed record BodyEntry(string Marker, string Text, int Index);

    private static List<TocEntry> BuildTocInventory(IReadOnlyList<PdfLine> lines, int start, int end)
    {
        var entries = new List<TocEntry>();
        var dotLeader = new Regex(@"^(\d+(?:\.\d+)*)\s*\.?\s+(.+?)\s*\.{4,}\s*\d+\s*$");
        for (var i = start; i < end; i++)
        {
            var match = dotLeader.Match(NormalizeDigitSpacing(lines[i].Text.Trim()));
            if (match.Success) entries.Add(new TocEntry(match.Groups[1].Value, match.Groups[2].Value, i));
        }
        return entries;
    }

    private static List<BodyEntry> BuildBodyInventory(IReadOnlyList<PdfLine> lines, int start, int end)
    {
        var entries = new List<BodyEntry>();
        var clauseHeading = new Regex(@"^(\d+(?:\.\d+)*)\s*\.\s+(?=\p{L})");
        var letterGroup = new Regex(@"^([A-Z])\.\s");

        for (var i = start; i < end; i++)
        {
            var text = lines[i].Text.Trim();
            var normalized = NormalizeDigitSpacing(text);
            var clauseMatch = clauseHeading.Match(normalized);
            if (clauseMatch.Success) { entries.Add(new BodyEntry(clauseMatch.Groups[1].Value, text, i)); continue; }
            var letterMatch = letterGroup.Match(text);
            if (letterMatch.Success) entries.Add(new BodyEntry($"group-{letterMatch.Groups[1].Value}", text, i));
        }
        return entries;
    }

    private static string NormalizeDigitSpacing(string text) => Regex.Replace(text, @"(?<=\d)\s+(?=\d)", "");

    private static string Trim(string value) => value.Length <= 90 ? value : value[..90] + "...";
}
