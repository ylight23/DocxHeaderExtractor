using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Eval;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// B2 gold for 041 (IBRD Management's Discussion and Analysis and Financial Statements). Deliberately
/// not reusing 028's marker-based rules: this document's failure class is different - a "Contents" page
/// (page 3, two-column layout) lists both 14 top-level "Section N: Title" entries and, per section, a
/// handful of sub-topic titles that carry NO marker at all in the body (no number, no letter - just a
/// short standalone line immediately before a paragraph, e.g. "Financial Business Model"). A regex on
/// the marker shape does not exist for these; the project's own established tool for exactly this
/// situation - a checklist title resolved against source lines by canonical text, reporting ambiguity
/// rather than guessing - is <see cref="PdfOccurrenceBridgeProposal"/>, reused here rather than
/// reinvented.
/// <para>
/// Section-level "Section N: Title" headings were found directly in the body (clean, unambiguous, no
/// TOC needed) before touching the TOC at all. The TOC's sub-topic column (right column, Left ~264-265
/// vs Section entries' ~74-75) supplies the checklist for sub-topic titles only; the fused first-row
/// entries (e.g. "Introduction" merged onto Section I's own TOC line) were checked directly and do not
/// correspond to any separate body heading, so they are not chased further.
/// </para>
/// <para>
/// Deliberately out of scope, not silently included: the "List of Tables, Figures, and Boxes" index
/// (page 4) - "Table N:"/"Figure N:"/"Box N:" are captions for embedded data tables, not document
/// outline headings, consistent with this project's existing caption/TableLike distinction. Also out of
/// scope: sub-headings inside individual Notes (A-N) - unlike the MD&A section, no accessible checklist
/// exists for note-internal structure, and discovering it via a marker-less-caption heuristic at that
/// scale risks the same ambiguity this file exists to avoid; recorded as a documented limitation, not
/// chased with a riskier heuristic.
/// </para>
/// </summary>
public sealed class PdfDocument041GoldBuilderProbe
{
    private const string DocxRelativePath = @"todo10_8\heading_corpus_95_word\03_tai_chinh_ke_toan\041_IBRD_Financial_Statements_June_2025.docx";
    private const int TocStart = 7;
    private const int TocEnd = 60; // exclusive - page 3's Section/sub-topic Contents list only

    // Section-level headings, already found directly in the body (packet inspection), not TOC-derived.
    private static readonly string[] SectionTitles =
    [
        "Section I: Overview", "Section II: Executive Summary", "Section III: Financial Results",
        "Section IV: Lending Activities", "Section V: Other Development Activities",
        "Section VI: Investment Activities", "Section VII: Borrowing Activities",
        "Section VIII: Capital Activities", "Section IX: Risk Management",
        "Section X: Contractual Obligations", "Section XI: Pension & Other Post-Retirement Benefits",
        "Section XIV: Reconciliations of Components of Allocable Income",
    ];

    // "Section XII"/"Section XIII" titles wrap to a second line in the body - handled as multiline pairs.
    private static readonly (string First, string Second)[] SectionTitleWraps =
    [
        ("Section XII: Critical Accounting Policies and the Use", "of Estimates"),
        ("Section XIII: Governance and Controls", ""),
    ];

    private static readonly string[] PrimaryStatementTitles =
        ["BALANCE SHEETS", "STATEMENTS OF COMPREHENSIVE INCOME", "STATEMENTS OF CHANGES IN EQUITY", "STATEMENTS OF CASH FLOWS"];

    private static readonly string[] NoteTitles =
    [
        "NOTE A—SUMMARY OF SIGNIFICANT ACCOUNTING AND RELATED",
        "NOTE B—CAPITAL STOCK MAINTENANCE OF VALUE AND",
        "NOTE C—INVESTMENTS", "NOTE D—LOANS AND OTHER EXPOSURES", "NOTE E—BORROWINGS",
        "NOTE F—DERIVATIVE INSTRUMENTS", "NOTE G — RETAINED EARNINGS AND BOARD OF GOVERNORS",
        "NOTE H—TRANSACTIONS WITH AFFILIATED ORGANIZATIONS",
        "NOTE I—ACCUMULATED OTHER COMPREHENSIVE INCOME", "NOTE J— FAIR VALUE DISCLOSURES",
        "NOTE K—PENSION AND OTHER POSTRETIREMENT BENEFITS",
        "NOTE L—TRUST FUNDS ADMINISTRATION AND OTHER SERVICES", "NOTE M—SEGMENT REPORTING",
        "NOTE N—CONTINGENCIES",
    ];

    // These are source-review decisions for the ambiguous canonical matches reported by
    // PdfOccurrenceBridgeProposal. They are deliberately occurrence indexes, not a new matching
    // rule: the other same-text lines were read in their local source context and are narrative
    // references, table labels, or note-internal material outside this B2 population. Net Income
    // has two independently structural occurrences, so it intentionally maps to two lines.
    private static readonly IReadOnlyDictionary<string, int[]> ReviewedAmbiguousHeadingLines =
        new Dictionary<string, int[]>(StringComparer.Ordinal)
        {
            ["041/statement/balance-sheets"] = [4475],
            ["041/statement/statements-of-cash-flows"] = [4720],
            ["041/note/note-f-derivative-instruments"] = [7071],
            ["041/note/note-j-fair-value-disclosures"] = [7475],
            ["041/note/note-k-pension-and-other-postretirement-benefits"] = [7958],
            ["041/subtopic/2"] = [447, 624],
            ["041/subtopic/11"] = [2229],
            ["041/subtopic/16"] = [2813],
            ["041/subtopic/17"] = [3366],
            ["041/subtopic/19"] = [3819],
            ["041/subtopic/20"] = [3983],
            ["041/subtopic/22"] = [4037],
        };

    [Fact]
    public void Report()
    {
        var auditOutput = Environment.GetEnvironmentVariable("BENCH_041_GOLD_AUDIT");
        var goldOutput = Environment.GetEnvironmentVariable("BENCH_041_GOLD_JSON");
        if (string.IsNullOrWhiteSpace(auditOutput) || string.IsNullOrWhiteSpace(goldOutput)) return;

        var docxPath = Path.Combine(PdfExtractorQualityBenchmarkProbe.RepositoryRoot(), DocxRelativePath);
        var lines = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(docxPath).Lines;

        var audit = new StringBuilder();
        void Line(string value) => audit.AppendLine(value);
        var occurrences = new List<PdfReviewedOccurrence>();

        // Single-line exact titles resolved by canonical text, via the project's own established tool.
        var singleLineGold = SectionTitles.Select(t => (StableId: $"041/section/{Slug(t)}", Text: t))
            .Concat(PrimaryStatementTitles.Select(t => (StableId: $"041/statement/{Slug(t)}", Text: t)))
            .Concat(NoteTitles.Select(t => (StableId: $"041/note/{Slug(t)}", Text: t)))
            .ToList();

        var subtopics = ExtractSubtopicChecklist(lines);
        Line($"subtopic checklist extracted from TOC right column: {subtopics.Count}");
        var subtopicGold = subtopics.Select((t, i) => (StableId: $"041/subtopic/{i}", Text: t)).ToList();

        var allGold = singleLineGold.Concat(subtopicGold).ToList();
        var proposals = PdfOccurrenceBridgeProposal.Propose(lines, allGold);

        var proposed = 0;
        var ambiguous = 0;
        var unresolved = 0;
        foreach (var proposal in proposals)
        {
            if (proposal.Status == "proposed")
            {
                proposed++;
                occurrences.Add(new PdfReviewedOccurrence(
                    proposal.GoldStableId, proposal.GoldText, lines[proposal.Matches[0].Index].Page,
                    proposal.Matches, "reviewed", "single-line", 1));
            }
            else if (proposal.Status == "ambiguous_for_review")
            {
                ambiguous++;
                Line($"AMBIGUOUS [{proposal.GoldStableId}] \"{proposal.GoldText}\" matched {proposal.Matches.Count} lines:");
                foreach (var m in proposal.Matches) Line($"    index={m.Index} page={lines[m.Index].Page}");
                if (ReviewedAmbiguousHeadingLines.TryGetValue(proposal.GoldStableId, out var reviewedIndexes))
                {
                    foreach (var index in reviewedIndexes)
                    {
                        EmitReviewedLine(occurrences, lines, proposal.GoldStableId, index, "ambiguous-source-review");
                        Line($"    REVIEWED HEADING index={index}; other same-text occurrences are excluded by local source role.");
                    }
                }
                else
                {
                    Line("    REVIEWED NOT ADDED: no uniquely source-grounded structural occurrence in this population.");
                }
            }
            else
            {
                unresolved++;
                Line($"UNRESOLVED [{proposal.GoldStableId}] \"{proposal.GoldText}\" - no exact canonical match in source.");
            }
        }
        Line($"single-line+subtopic resolution: proposed={proposed} ambiguous={ambiguous} unresolved={unresolved}");
        Line("");

        // Multiline Section titles (wrap to a second line) and multiline Note titles - handled directly,
        // same as 001's Chuong/Phan multiline pattern: find the first line by canonical text, take the
        // line immediately after as the continuation.
        foreach (var (first, second) in SectionTitleWraps) EmitMultiline(occurrences, lines, first, second, "section", Line);
        foreach (var note in NoteTitles) EmitNoteContinuation(occurrences, lines, note, Line);

        Line("");
        Line($"TOTAL gold occurrences = {occurrences.Count}");
        Line("Out of scope, documented: List of Tables/Figures/Boxes captions (not headings); note-internal sub-headings (no accessible checklist).");
        Line("The unresolved truncated TOC label is not fabricated: it has no exact canonical source span.");

        File.WriteAllText(auditOutput, audit.ToString());

        var bridge = new PdfReviewedOccurrenceBridge(
            "041_IBRD_Financial_Statements_June_2025.docx",
            Sha256(docxPath),
            "not_recorded_bridge_does_not_enforce_pdf_sha_at_load",
            "not_recorded_no_answer_key_source",
            ExtractionFingerprint(lines),
            occurrences.DistinctBy(o => o.GoldStableId).OrderBy(o => o.Lines[0].Index).ToArray());
        File.WriteAllText(goldOutput, JsonSerializer.Serialize(bridge, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
    }

    /// <summary>
    /// Fixed B2 self-audit for the frozen bridge. It verifies identity against the current raw source
    /// extraction, not against candidate/rank/scope/model output, so a stale line index or an edited
    /// source cannot silently become benchmark gold.
    /// </summary>
    [Fact]
    public void FrozenBridgeIsOccurrenceSafeAndSourceGrounded()
    {
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var docxPath = Path.Combine(root, DocxRelativePath);
        var bridgePath = Path.Combine(root, "keys", "occurrence-bridge",
            "041_IBRD_Financial_Statements_June_2025.occurrence-bridge.json");
        Assert.True(File.Exists(bridgePath), "041 B2 bridge must be frozen before it contributes to census.");

        var lines = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(docxPath).Lines;
        var bridge = PdfReviewedOccurrenceBridge.Load(File.ReadAllText(bridgePath));

        Assert.Equal(Sha256(docxPath), bridge.DocxSha256);
        Assert.Equal(ExtractionFingerprint(lines), bridge.PdfLineExtractionFingerprint);
        Assert.All(bridge.Occurrences, occurrence => Assert.Equal("reviewed", occurrence.ReviewStatus));
        Assert.Equal(bridge.Occurrences.Count, bridge.Occurrences.Select(o => o.GoldStableId).Distinct().Count());

        foreach (var occurrence in bridge.Occurrences)
        foreach (var required in occurrence.RequiredLines)
        {
            Assert.InRange(required.Index, 0, lines.Count - 1);
            Assert.Equal(PdfCandidateProvenance.LineId(lines[required.Index]), required.LineId);
            Assert.Equal(lines[required.Index].Text, required.Text);
        }
    }

    /// <summary>Right-column TOC entries only (Left ~264-265) - the unambiguous sub-topic list, page 3.</summary>
    private static List<string> ExtractSubtopicChecklist(IReadOnlyList<PdfLine> lines)
    {
        var trailingPage = new Regex(@"\s+\d[\d\s]*$");
        var result = new List<string>();
        for (var i = TocStart; i < TocEnd; i++)
        {
            var line = lines[i];
            if (line.Left is < 260 or > 270) continue;
            var title = trailingPage.Replace(line.Text.Trim(), "").Trim();
            if (title.Length > 0) result.Add(title);
        }
        return result;
    }

    private static void EmitMultiline(List<PdfReviewedOccurrence> occurrences, IReadOnlyList<PdfLine> lines,
        string first, string second, string kind, Action<string> log)
    {
        var target = PdfTextUtilities.CanonicalForMatch(first);
        var matches = lines.Select((l, i) => (l, i)).Where(x => PdfTextUtilities.CanonicalForMatch(x.l.Text) == target).ToArray();
        if (matches.Length != 1) { log($"MULTILINE-UNRESOLVED [{kind}] \"{first}\" matches={matches.Length}"); return; }

        var index = matches[0].i;
        var indexes = string.IsNullOrEmpty(second) ? [index] : new[] { index, index + 1 };
        var goldLines = indexes.Select(i => new PdfReviewedOccurrenceLine(i, PdfCandidateProvenance.LineId(lines[i]), lines[i].Text)).ToArray();
        occurrences.Add(new PdfReviewedOccurrence(
            $"041/{kind}/{Slug(first)}", string.Join(" ", goldLines.Select(l => l.Text)), lines[index].Page, goldLines, "reviewed", kind, 1));
    }

    private static void EmitReviewedLine(
        List<PdfReviewedOccurrence> occurrences,
        IReadOnlyList<PdfLine> lines,
        string stableId,
        int index,
        string method)
    {
        var line = lines[index];
        occurrences.Add(new PdfReviewedOccurrence(
            $"{stableId}/{index}", line.Text, line.Page,
            [new PdfReviewedOccurrenceLine(index, PdfCandidateProvenance.LineId(line), line.Text)],
            "reviewed", method, 1));
    }

    /// <summary>A Note title's own continuation line, when its heading wraps (mirrors 001's Chuong/Phan handling).</summary>
    private static void EmitNoteContinuation(List<PdfReviewedOccurrence> occurrences, IReadOnlyList<PdfLine> lines, string title, Action<string> log)
    {
        var target = PdfTextUtilities.CanonicalForMatch(title);
        var matches = lines.Select((l, i) => (l, i)).Where(x => PdfTextUtilities.CanonicalForMatch(x.l.Text) == target).ToArray();
        if (matches.Length != 1) return; // already handled via the single-line resolver above if unique
        var index = matches[0].i;
        var next = lines[index + 1];
        // A continuation line is short, ALL-CAPS, and immediately follows - the same signature 001
        // validated for Phan/Chuong continuations. If the next line doesn't look like a continuation,
        // this title was already fully captured as single-line and nothing more is added.
        var nextText = next.Text.Trim();
        if (nextText.Length == 0 || !nextText.Any(char.IsLetter) || nextText != nextText.ToUpperInvariant()) return;
        if (System.Text.RegularExpressions.Regex.IsMatch(nextText, @"^\d")) return;

        var stableId = $"041/note/{Slug(title)}";
        var existing = occurrences.FirstOrDefault(o => o.GoldStableId == stableId);
        if (existing is null) return;
        occurrences.Remove(existing);
        var goldLines = existing.Lines.Append(new PdfReviewedOccurrenceLine(index + 1, PdfCandidateProvenance.LineId(next), next.Text)).ToArray();
        occurrences.Add(existing with { Lines = goldLines, GoldText = existing.GoldText + " " + next.Text });
        log($"NOTE-CONTINUATION [{stableId}] extended with index={index + 1}: {Trim(next.Text)}");
    }

    private static string Slug(string value) => Regex.Replace(value, @"[^A-Za-z0-9]+", "-").Trim('-').ToLowerInvariant();
    private static string Trim(string value) => value.Length <= 60 ? value : value[..60] + "...";

    private static string ExtractionFingerprint(IReadOnlyList<PdfLine> lines) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(string.Join('\n', lines.Select((l, i) => $"{i}|{PdfCandidateProvenance.LineId(l)}"))))).ToLowerInvariant();

    private static string Sha256(string path) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}
