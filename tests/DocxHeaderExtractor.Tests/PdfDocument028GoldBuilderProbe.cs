using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Eval;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// B2 gold for 028 (WB procurement, RFB Works Without Prequalification). Method validated on Section I
/// (exact 49/49 TOC-body reconciliation, 4-page manual audit clean - see
/// <see cref="PdfDocument028Section1PilotProbe"/>), then investigated by hand for the three hard cases
/// scaling immediately surfaced (not by writing more general regex):
/// <para>
/// 1. Numbered prose is not a heading. Section II's "1. the terms of the Bidding Documents; and / 2.
/// the Employer's decision..." and all of Section VII's ESHS/Code-of-Conduct commitments are sentence
/// enumerations after a lead-in ("may challenge any of the following:", "the policy... commitments
/// to:"), confirmed by reading the actual lead-in text, not inferred from shape. Excluded explicitly by
/// index below - a reviewed classification, not a new production heuristic.
/// </para>
/// <para>
/// 2. Numbering restarts within one section are real, not duplicates. Section III has two local scopes
/// (a short "Margin of Preference/Evaluation" pair, then the qualification-criteria table's own
/// category headers starting again at 1). Section VIII has three: the main GC clauses, "APPENDIX A -
/// General Conditions of Dispute Board Agreement" (its own clause 1-9), and "APPENDIX B - Fraud and
/// Corruption" (clause 1-2, identical boilerplate to Section VI). Recorded as an optional scope tag
/// inside <c>goldStableId</c> - review-only annotation, no schema change, no scope-resolver component.
/// </para>
/// <para>
/// 3. GC clauses 13, 14, 15, 19 have no standalone heading anywhere in this document's own source -
/// verified directly: the TOC jumps 12.4 -&gt; 13.1, 13.8 -&gt; 14.1, 14.15 -&gt; 15.1, and 18.x -&gt; 20
/// with no "13./14./15./19. [Title]" line in between, in either the TOC or the body. Not fabricated
/// from outside knowledge of what a standard FIDIC edition "should" contain - simply absent, so gold
/// contains nothing for them.
/// </para>
/// </summary>
public sealed class PdfDocument028GoldBuilderProbe
{
    private const string DocxRelativePath = @"todo10_8\heading_corpus_95_word\02_hop_dong_mua_sam\028_WB_RFB_Works_Without_Prequal_2017.docx";

    // (label, start, end, local scope tag, indexes to explicitly exclude as reviewed non-headings)
    private static readonly (string Label, int Start, int End, string Scope, int[] Exclude)[] ClauseScopes =
    [
        ("Section I", 319, 1472, "ITB", []),
        ("Section II", 1472, 1887, "BDS", [1884, 1885]),
        ("Section III (Margin/Evaluation)", 1887, 2075, "EVAL", []),
        ("Section III (Qualification)", 2075, 2446, "QUAL", []),
        ("Section VI", 3823, 3908, "FraudCorruption", []),
        ("Section VIII (GC)", 4297, 8734, "GCC", []),
        ("Section VIII Appendix A (DBA)", 8734, 9068, "DBA", []),
        ("Section VIII Appendix B", 9068, 9282, "APPXB", []),
    ];

    // Genuine top-level structural headings (Section/Part/Appendix/Part-of-Section-IX), found and
    // verified individually - some multiline (marker + title on a following line).
    private static readonly (int[] Indexes, string Label)[] StructuralHeadings =
    [
        ([315], "PART 1 - Bidding Procedures"),
        ([317], "Section I - Instructions to Bidders (title page)"),
        ([370], "Section I - Instructions to Bidders"),
        ([1472], "Section II - Bid Data Sheet (BDS)"),
        ([1887, 1888], "Section III - Evaluation and Qualification Criteria"),
        ([2446], "Section IV - Bidding Forms"),
        ([3813], "Section V - Eligible Countries"),
        ([3823], "Section VI - Fraud and Corruption"),
        ([3908], "PART 2 - Works' Requirements"),
        ([3910], "Section VII - Works' Requirements"),
        ([4088, 4089], "PART 3 - Conditions of Contract and Contract Forms"),
        ([4297], "Section VIII - General Conditions (GC)"),
        ([8734, 8735], "APPENDIX A - General Conditions of Dispute Board Agreement"),
        ([9068, 9069], "APPENDIX B - Fraud and Corruption"),
        ([9282, 9283], "Section IX - Particular Conditions of Contract"),
        ([9288], "Part A - Contract Data"),
        ([9700], "Section X - Contract Forms"),
    ];

    [Fact]
    public void Report()
    {
        var auditOutput = Environment.GetEnvironmentVariable("BENCH_028_GOLD_AUDIT");
        var goldOutput = Environment.GetEnvironmentVariable("BENCH_028_GOLD_JSON");
        if (string.IsNullOrWhiteSpace(auditOutput) || string.IsNullOrWhiteSpace(goldOutput)) return;

        var docxPath = Path.Combine(PdfExtractorQualityBenchmarkProbe.RepositoryRoot(), DocxRelativePath);
        var lines = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(docxPath).Lines;

        var occurrences = new List<PdfReviewedOccurrence>();
        var audit = new StringBuilder();
        void Line(string value) => audit.AppendLine(value);

        foreach (var (indexes, label) in StructuralHeadings)
        {
            Emit(occurrences, lines, indexes, "structural", label);
        }

        var totalMatched = 0;
        var totalTocOnly = 0;
        var totalBodyOnly = 0;

        foreach (var (label, start, end, scope, exclude) in ClauseScopes)
        {
            var excludeSet = exclude.ToHashSet();
            var toc = BuildTocInventory(lines, start, end);
            var body = BuildBodyInventory(lines, start, end, excludeSet);

            var tocMarkers = toc.Select(t => t.Marker).ToHashSet(StringComparer.Ordinal);
            var bodyMarkers = body.Select(b => b.Marker).ToHashSet(StringComparer.Ordinal);
            var matched = toc.Count(t => bodyMarkers.Contains(t.Marker));
            var tocOnly = toc.Count(t => !bodyMarkers.Contains(t.Marker));
            var bodyOnly = body.Count(b => !tocMarkers.Contains(b.Marker));

            totalMatched += matched;
            totalTocOnly += tocOnly;
            totalBodyOnly += bodyOnly;

            Line($"{label} [{start},{end}) scope={scope}: toc={toc.Count} body={body.Count} matched={matched} tocOnly={tocOnly} bodyOnly={bodyOnly}");
            foreach (var t in toc.Where(t => !bodyMarkers.Contains(t.Marker)))
                Line($"    TOC-ONLY [{t.Marker}] {t.Title} page={lines[t.Index].Page}");

            foreach (var b in body)
            {
                var stableId = $"028/{scope}/{b.Marker}";
                var goldLines = new[] { new PdfReviewedOccurrenceLine(b.Index, PdfCandidateProvenance.LineId(lines[b.Index]), lines[b.Index].Text) };
                occurrences.Add(new PdfReviewedOccurrence(stableId, lines[b.Index].Text, lines[b.Index].Page, goldLines, "reviewed", scope, 1));
            }
        }

        Line("");
        Line($"TOTAL structural={StructuralHeadings.Length} clauseMatched={totalMatched} clauseTocOnly={totalTocOnly} clauseBodyOnly={totalBodyOnly}");
        Line($"TOTAL gold occurrences = {occurrences.Count}");
        Line("");
        Line("Confirmed absent from source (checked TOC and body directly, not fabricated): GC clauses 13, 14, 15, 19 - no standalone heading line anywhere.");

        File.WriteAllText(auditOutput, audit.ToString());

        var bridge = new PdfReviewedOccurrenceBridge(
            "028_WB_RFB_Works_Without_Prequal_2017.docx",
            Sha256(docxPath),
            "not_recorded_bridge_does_not_enforce_pdf_sha_at_load",
            "not_recorded_no_answer_key_source",
            ExtractionFingerprint(lines),
            occurrences.OrderBy(o => o.Lines[0].Index).ToArray());
        File.WriteAllText(goldOutput, JsonSerializer.Serialize(bridge, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
    }

    private static void Emit(List<PdfReviewedOccurrence> occurrences, IReadOnlyList<PdfLine> lines, int[] indexes, string kind, string label)
    {
        var goldLines = indexes.Select(i => new PdfReviewedOccurrenceLine(i, PdfCandidateProvenance.LineId(lines[i]), lines[i].Text)).ToArray();
        occurrences.Add(new PdfReviewedOccurrence(
            $"028/{kind}/{indexes[0]}", string.Join(" ", goldLines.Select(l => l.Text)), lines[indexes[0]].Page, goldLines, "reviewed", kind, 1));
    }

    private sealed record TocEntry(string Marker, string Title, int Index);
    private sealed record BodyEntry(string Marker, int Index);

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

    private static List<BodyEntry> BuildBodyInventory(IReadOnlyList<PdfLine> lines, int start, int end, HashSet<int> exclude)
    {
        var entries = new List<BodyEntry>();
        var clauseHeading = new Regex(@"^(\d+(?:\.\d+)*)\s*\.\s+(?=\p{L})");
        var letterGroup = new Regex(@"^([A-Z])\.\s");
        // A dot-leader TOC line ("1. Scope of Bid........6") also structurally matches clauseHeading -
        // both start "N. Title" - so it must be excluded here explicitly, not just treated as a
        // separate range: this document's own local TOC (Section I, GC) sits inside these same scan
        // ranges once the earlier boundary bug (excluding a section's own TOC) was fixed, and without
        // this exclusion each TOC line would be added to gold a second time as a fake body occurrence.
        var dotLeaderShape = new Regex(@"\.{4,}\s*\d+\s*$");

        for (var i = start; i < end; i++)
        {
            if (exclude.Contains(i)) continue;
            var text = lines[i].Text.Trim();
            if (dotLeaderShape.IsMatch(NormalizeDigitSpacing(text))) continue;
            var normalized = NormalizeDigitSpacing(text);
            var clauseMatch = clauseHeading.Match(normalized);
            if (clauseMatch.Success) { entries.Add(new BodyEntry(clauseMatch.Groups[1].Value, i)); continue; }
            var letterMatch = letterGroup.Match(text);
            if (letterMatch.Success) entries.Add(new BodyEntry($"group-{letterMatch.Groups[1].Value}", i));
        }
        return entries;
    }

    private static string NormalizeDigitSpacing(string text) => Regex.Replace(text, @"(?<=\d)\s+(?=\d)", "");

    private static string ExtractionFingerprint(IReadOnlyList<PdfLine> lines) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(string.Join('\n', lines.Select((l, i) => $"{i}|{PdfCandidateProvenance.LineId(l)}"))))).ToLowerInvariant();

    private static string Sha256(string path) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}
