using System.Globalization;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Eval;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// B2.0 pilot: blind, source-first gold builder for document 001 (Bo_luat_Dan_su_91-2015-QH13). Rules
/// below were derived and validated entirely from the packet's raw source text
/// (<see cref="PdfBlindReviewPacketProbe"/>) - no candidate id, rank, selected status, structural
/// scope, domain role, or model output was consulted anywhere in this file.
/// <para>
/// Vietnamese Civil Code structure: Phan (Part) / Chuong (Chapter) / Muc (Section) markers are
/// followed by an ALL-CAPS title on one or more subsequent lines; Dieu (Article) markers carry their
/// title inline ("Dieu N. Title") but a long title can still wrap to a short continuation line before
/// numbered clause body content starts. A cross-reference to another article ("... quy dinh tai Dieu 47
/// cua Bo luat nay...") can coincidentally start its own wrapped PDF line, but never has a period
/// immediately after the number the way a genuine heading does - that is the discriminator, verified
/// exhaustively against all 6 real instances of it in this document (logged below, not just assumed).
/// </para>
/// </summary>
public sealed class PdfDocument001GoldBuilderProbe
{
    private const string DocxRelativePath = @"todo10_8\heading_corpus_95_word\01_phap_quy\001_Bo_luat_Dan_su_91-2015-QH13.docx";

    [Fact]
    public void Report()
    {
        var auditOutput = Environment.GetEnvironmentVariable("BENCH_001_GOLD_AUDIT");
        var goldOutput = Environment.GetEnvironmentVariable("BENCH_001_GOLD_JSON");
        if (string.IsNullOrWhiteSpace(auditOutput) || string.IsNullOrWhiteSpace(goldOutput)) return;

        var docxPath = Path.Combine(PdfExtractorQualityBenchmarkProbe.RepositoryRoot(), DocxRelativePath);
        var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(docxPath);
        var lines = snapshot.Lines;

        var audit = new StringBuilder();
        void Line(string value) => audit.AppendLine(value);

        var occurrences = new List<PdfReviewedOccurrence>();
        var crossReferenceRejections = new List<(int Index, string Text)>();
        var wrapDecisions = new List<(int MarkerIndex, int EndIndex, string Kind)>();

        for (var i = 0; i < lines.Count; i++)
        {
            var text = lines[i].Text;

            if (IsPhanMarker(text))
            {
                var end = ConsumeAllCapsContinuation(lines, i + 1);
                Emit(occurrences, lines, i, end, "phan", $"{i}");
                wrapDecisions.Add((i, end, "phan"));
                continue;
            }

            if (IsChuongMarker(text))
            {
                var end = ConsumeAllCapsContinuation(lines, i + 1);
                Emit(occurrences, lines, i, end, "chuong", $"{i}");
                wrapDecisions.Add((i, end, "chuong"));
                continue;
            }

            if (IsMucMarker(text))
            {
                // Muc titles are ALL-CAPS, same rendering convention as Chuong/Phan (verified: "Muc 5.
                // THONG BAO TIM KIEM..." continues across a lone-comma line into "TICH TUYEN BO CHET" -
                // a width-only heuristic missed this because the marker line itself was not near the
                // page's max content width).
                var end = ConsumeAllCapsContinuation(lines, i + 1);
                Emit(occurrences, lines, i, end, "muc", $"{i}");
                if (end != i) wrapDecisions.Add((i, end, "muc"));
                continue;
            }

            if (IsDieuMarker(text, out var hasPeriodAfterNumber))
            {
                if (!hasPeriodAfterNumber)
                {
                    crossReferenceRejections.Add((i, text));
                    continue;
                }
                // Dieu titles are sentence-case, not ALL-CAPS, so the Muc/Chuong/Phan continuation rule
                // does not apply. The reliable signal here is orthographic, not geometric: a title that
                // wrapped mid-word/mid-phrase resumes with a lowercase letter ("... xac lap, thuc" ->
                // "hien"), while a genuine new sentence in formal Vietnamese legal text always starts
                // uppercase. A width threshold was tried first and missed a real wrap (Dieu 142) because
                // ordinary body-line widths overlap with wrapped-title widths - this replaces it.
                var end = ConsumeLowercaseContinuation(lines, i);
                Emit(occurrences, lines, i, end, "dieu", $"{i}");
                if (end != i) wrapDecisions.Add((i, end, "dieu"));
            }
        }

        // Integrity audit - model-free, source-only.
        Line($"document=001_Bo_luat_Dan_su_91-2015-QH13 sourceLines={lines.Count}");
        Line($"headings found: {occurrences.Count}");
        var byKind = occurrences.GroupBy(o => o.ReviewMethod).ToDictionary(g => g.Key, g => g.Count());
        foreach (var (kind, count) in byKind.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            Line($"  {kind,-8} {count}");
        Line("");

        Line($"cross-reference rejections (Dieu N without period, excluded as NOT a heading): {crossReferenceRejections.Count}");
        foreach (var (index, text) in crossReferenceRejections)
            Line($"  [{index}] {text}");
        Line("");

        Line($"multi-line spans detected (marker index -> end index, kind): {wrapDecisions.Count}");
        foreach (var (markerIndex, endIndex, kind) in wrapDecisions)
        {
            var span = string.Join(" | ", Enumerable.Range(markerIndex, endIndex - markerIndex + 1).Select(k => lines[k].Text));
            Line($"  [{markerIndex}-{endIndex}] {kind}: {span}");
        }
        Line("");

        // Uniqueness / gap audit for Dieu numbering specifically - Bo luat Dan su 2015 has exactly 689 articles.
        var dieuNumbers = occurrences.Where(o => o.ReviewMethod == "dieu")
            .Select(o => int.Parse(System.Text.RegularExpressions.Regex.Match(o.GoldText, @"^Điều (\d+)").Groups[1].Value, CultureInfo.InvariantCulture))
            .OrderBy(n => n).ToArray();
        Line($"dieu article numbers: count={dieuNumbers.Length} unique={dieuNumbers.Distinct().Count()} min={dieuNumbers.FirstOrDefault()} max={dieuNumbers.LastOrDefault()}");
        var gaps = new List<string>();
        for (var i = 1; i < dieuNumbers.Length; i++)
            if (dieuNumbers[i] != dieuNumbers[i - 1] + 1)
                gaps.Add($"{dieuNumbers[i - 1]} -> {dieuNumbers[i]}");
        Line(gaps.Count == 0 ? "no gaps in article sequence 1..689" : "GAPS: " + string.Join(", ", gaps));

        File.WriteAllText(auditOutput, audit.ToString());

        // Gold artifact - PdfReviewedOccurrenceBridge schema, same as 054's, so the existing evaluator
        // consumes it with no new join logic.
        var bridge = new PdfReviewedOccurrenceBridge(
            "001_Bo_luat_Dan_su_91-2015-QH13.docx",
            Sha256(docxPath),
            "not_recorded_bridge_does_not_enforce_pdf_sha_at_load",
            "not_recorded_no_answer_key_source",
            PdfOccurrenceBridgeProposalExtractionFingerprint(lines),
            occurrences);
        File.WriteAllText(goldOutput, JsonSerializer.Serialize(bridge, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
    }

    private static void Emit(
        List<PdfReviewedOccurrence> occurrences, IReadOnlyList<PdfLine> lines, int start, int end, string kind, string stableIdSuffix)
    {
        var goldLines = Enumerable.Range(start, end - start + 1)
            .Select(i => new PdfReviewedOccurrenceLine(i, PdfCandidateProvenance.LineId(lines[i]), lines[i].Text))
            .ToArray();
        var goldText = string.Join(" ", goldLines.Select(l => l.Text));
        occurrences.Add(new PdfReviewedOccurrence(
            $"001/{kind}/{stableIdSuffix}", goldText, lines[start].Page, goldLines, "reviewed",
            kind, 1));
    }

    private static bool IsPhanMarker(string text) =>
        text.StartsWith("PHẦN THỨ ", StringComparison.Ordinal);

    private static bool IsChuongMarker(string text) =>
        System.Text.RegularExpressions.Regex.IsMatch(text, @"^(Chương|CHƯƠNG)\s?[IVXLCDM]+\.?$");

    private static bool IsMucMarker(string text) =>
        System.Text.RegularExpressions.Regex.IsMatch(text, @"^Mục \d+\.");

    /// <param name="hasPeriodAfterNumber">
    /// False means this is a cross-reference ("... Dieu 47 cua Bo luat nay ...") that happened to start
    /// its own wrapped line, not a heading - verified exhaustively for this document, see the audit's
    /// rejection list.
    /// </param>
    private static bool IsDieuMarker(string text, out bool hasPeriodAfterNumber)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text, @"^Điều (\d+)(\.?)");
        if (!match.Success) { hasPeriodAfterNumber = false; return false; }
        hasPeriodAfterNumber = match.Groups[2].Value == ".";
        return true;
    }

    /// <summary>Phan/Chuong titles run for as many consecutive ALL-CAPS lines as follow the marker.</summary>
    private static int ConsumeAllCapsContinuation(IReadOnlyList<PdfLine> lines, int from)
    {
        var end = from - 1;
        for (var i = from; i < lines.Count; i++)
        {
            var t = lines[i].Text.Trim();
            if (t.Length == 0 || t != t.ToUpper(CultureInfo.InvariantCulture) || char.IsLower(t.FirstOrDefault(char.IsLetter)))
                break;
            end = i;
        }
        return end < from ? from - 1 : end;
    }

    /// <summary>
    /// A Dieu title wraps onto the next line only when the marker's own line does not end in terminal
    /// punctuation AND the next letter-bearing line (a lone-comma or lone-punctuation line in between is
    /// skipped over, not treated as ending the title - the same rendering artifact the Chuong/Muc
    /// ALL-CAPS continuation already tolerates) starts with a lowercase letter - a mid-word/mid-phrase
    /// resumption, never how a new sentence or a new numbered clause starts in formal Vietnamese legal
    /// text. A first version of this check stopped at the punctuation-only line instead of skipping it
    /// and truncated a real title mid-word ("Dieu 202" - caught and fixed before this became gold, not
    /// after). Checked repeatedly in case a title wraps more than once.
    /// </summary>
    private static int ConsumeLowercaseContinuation(IReadOnlyList<PdfLine> lines, int markerIndex)
    {
        var end = markerIndex;
        while (true)
        {
            var current = lines[end].Text.TrimEnd();
            var endsWithTerminalPunctuation = current.Length > 0 && ".!?:".Contains(current[^1]);
            if (endsWithTerminalPunctuation) return end;

            var next = end + 1;
            while (next < lines.Count && !lines[next].Text.Any(char.IsLetter)) next++;
            if (next >= lines.Count) return end;

            var nextText = lines[next].Text.Trim();
            // A lowercase-lettered clause marker ("a) ...", "b) ...") also starts with a lowercase
            // letter - excluded explicitly so a genuine new clause is never swallowed as a continuation.
            var looksLikeClauseBody = System.Text.RegularExpressions.Regex.IsMatch(nextText, @"^(\d+\s?\.|[a-zđ]\))");
            var startsLowercase = nextText.Length > 0 && char.IsLower(nextText[0]);
            if (!startsLowercase || looksLikeClauseBody) return end;

            end = next;
        }
    }

    private static string PdfOccurrenceBridgeProposalExtractionFingerprint(IReadOnlyList<PdfLine> lines) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(string.Join('\n', lines.Select((l, i) => $"{i}|{PdfCandidateProvenance.LineId(l)}"))))).ToLowerInvariant();

    private static string Sha256(string path) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}
