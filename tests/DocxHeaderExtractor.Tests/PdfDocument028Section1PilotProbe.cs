using System.Text;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// B2 pilot for 028 (procurement, WB Standard Procurement Document), Section I only, before scaling to
/// the rest of the document. Two independent source-only inventories, reconciled by clause number - the
/// TOC is a discovery aid, never assumed exhaustive, and gold points at the body occurrence, never the
/// TOC line.
/// <para>
/// A. TOC inventory: Section I's own local "Contents" listing (dot-leader entries, page 16-17) - marker
/// number and title, TOC page kept only as evidence, never treated as a body heading itself.
/// </para>
/// <para>
/// B. Body inventory: independent scan of Section I's actual body (page 18 onward, before Section II
/// starts) for structural markers - lettered groups ("A. General") and numbered clauses ("1.", "20.8")
/// - using the same period-after-number discriminator validated on 001 to reject a cross-reference that
/// coincidentally starts its own wrapped line.
/// </para>
/// <para>C. Reconciliation: matched (both agree) / body-only (TOC may have missed it) / toc-only
/// (extraction may have missed it, or it is a TOC-only entry) / never invented as HEADING from TOC
/// membership alone.</para>
/// </summary>
public sealed class PdfDocument028Section1PilotProbe
{
    private const string DocxRelativePath = @"todo10_8\heading_corpus_95_word\02_hop_dong_mua_sam\028_WB_RFB_Works_Without_Prequal_2017.docx";
    private const int TocStart = 319;
    private const int TocEndExclusive = 370; // [370] is the body-start heading itself
    private const int BodyStart = 370;
    private const int BodyEndExclusive = 1472; // [1472] is Section II's body-start heading

    [Fact]
    public void Report()
    {
        var output = Environment.GetEnvironmentVariable("BENCH_028_S1_PILOT_REPORT");
        if (string.IsNullOrWhiteSpace(output)) return;

        var docxPath = Path.Combine(PdfExtractorQualityBenchmarkProbe.RepositoryRoot(), DocxRelativePath);
        var lines = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(docxPath).Lines;

        var toc = BuildTocInventory(lines);
        var body = BuildBodyInventory(lines);

        var report = new StringBuilder();
        void Line(string value) => report.AppendLine(value);

        Line($"Section I pilot - toc={toc.Count} body={body.Count}");
        Line("");

        var byMarker = body.ToLookup(b => b.Marker, StringComparer.Ordinal);
        var tocMarkers = toc.Select(t => t.Marker).ToHashSet(StringComparer.Ordinal);
        var bodyMarkers = body.Select(b => b.Marker).ToHashSet(StringComparer.Ordinal);

        var matched = toc.Where(t => bodyMarkers.Contains(t.Marker)).ToList();
        var tocOnly = toc.Where(t => !bodyMarkers.Contains(t.Marker)).ToList();
        var bodyOnly = body.Where(b => !tocMarkers.Contains(b.Marker)).ToList();
        var duplicateMarkersInBody = byMarker.Where(g => g.Count() > 1).ToList();

        Line($"matched={matched.Count} tocOnly={tocOnly.Count} bodyOnly={bodyOnly.Count} duplicateBodyMarkers={duplicateMarkersInBody.Count}");
        Line("");

        Line("TOC-only (in Section I's Contents but no body occurrence found - review):");
        foreach (var t in tocOnly) Line($"  [{t.Marker}] {t.Title} (toc index={t.Index}, tocPage={lines[t.Index].Page})");
        Line("");

        Line("Body-only (structural marker in body with no matching TOC entry - review, may be a real heading the Contents omitted):");
        foreach (var b in bodyOnly) Line($"  [{b.Marker}] index={b.Index} page={lines[b.Index].Page}: {Trim(b.Text)}");
        Line("");

        Line("Duplicate body markers (more than one body occurrence for the same clause number - review):");
        foreach (var group in duplicateMarkersInBody)
        {
            Line($"  marker={group.Key}");
            foreach (var b in group) Line($"    index={b.Index} page={lines[b.Index].Page}: {Trim(b.Text)}");
        }
        Line("");

        Line("Matched (TOC + body agree, sample of first 10):");
        foreach (var t in matched.Take(10))
        {
            var b = byMarker[t.Marker].First();
            Line($"  [{t.Marker}] toc=\"{t.Title}\" body(index={b.Index}, page={lines[b.Index].Page})=\"{Trim(b.Text)}\"");
        }

        File.WriteAllText(output, report.ToString());
    }

    private sealed record TocEntry(string Marker, string Title, int Index);
    private sealed record BodyEntry(string Marker, string Text, int Index);

    private static List<TocEntry> BuildTocInventory(IReadOnlyList<PdfLine> lines)
    {
        var entries = new List<TocEntry>();
        var dotLeader = new Regex(@"^(\d+(?:\.\d+)*)\s*\.?\s+(.+?)\s*\.{4,}\s*\d+\s*$");
        for (var i = TocStart; i < TocEndExclusive; i++)
        {
            var match = dotLeader.Match(NormalizeDigitSpacing(lines[i].Text.Trim()));
            if (match.Success) entries.Add(new TocEntry(match.Groups[1].Value, match.Groups[2].Value, i));
        }
        return entries;
    }

    /// <summary>
    /// Collapses a space the renderer inserted inside a multi-digit number ("1 1" -> "11", "3 1" -> "31")
    /// - the same space-insertion damage documented elsewhere in this corpus, here breaking a marker
    /// regex that otherwise matches every other entry cleanly. Only touches digit-to-digit gaps, so
    /// "Scope of Bid" is untouched.
    /// </summary>
    private static string NormalizeDigitSpacing(string text) => Regex.Replace(text, @"(?<=\d)\s+(?=\d)", "");

    private static List<BodyEntry> BuildBodyInventory(IReadOnlyList<PdfLine> lines)
    {
        var entries = new List<BodyEntry>();
        // Genuine clause heading: number(.number)* immediately followed by a period, AND the title word
        // right after starts with a letter, not a digit - the same discriminator validated on 001's
        // "Dieu N." vs a cross-reference. Without the letter check, a sub-clause reference rendered with
        // a stray space ("4. 10 A firm...", really "4.10") is misread as a fresh top-level marker "4"
        // duplicating the real "4. Eligible Bidders" heading - caught by this pilot, not assumed away.
        var clauseHeading = new Regex(@"^(\d+(?:\.\d+)*)\s*\.\s+(?=\p{L})");
        var letterGroup = new Regex(@"^([A-Z])\.\s");

        for (var i = BodyStart; i < BodyEndExclusive; i++)
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

    private static string Trim(string value) => value.Length <= 90 ? value : value[..90] + "...";
}
