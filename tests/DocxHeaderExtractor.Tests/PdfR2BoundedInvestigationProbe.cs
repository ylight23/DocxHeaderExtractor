using System.Text.Json;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Bounded, offline-only investigation of R2's two named upstream limitations, run as counterfactuals
/// before any production code changes. Neither candidate fix is implemented in production here.
/// <list type="bullet">
/// <item><b>R2a</b> - a candidate marker grammar that recognizes a multi-level decimal marker
/// delimited by whitespace alone, not requiring a trailing "." or ")". Measured against 057's 22 real
/// targets (positive) and against every `HeadingTopic`-classified candidate in the two available
/// canonical checkpoints, 003 and 057 (negative control: does the looser grammar fire somewhere the
/// strict one did not, and does that look like a real marker or spurious decimal/date/IP-shaped
/// content?).</item>
/// <item><b>R2b</b> - deterministic reconciliation of the model-proposed <c>HeadingSpan</c> to the
/// exact constituent source line it overlaps, rather than trusting the raw character offsets. If the
/// proposed span's start does not fall inside any located line, this abstains (<c>UNRESOLVED</c>), it
/// never guesses or fuzzy-matches.</item>
/// </list>
/// CF1 = R2a alone, CF2 = R2b alone, CF3 = both. No provider call, no candidate construction change,
/// no production code change.
/// </summary>
public sealed class PdfR2BoundedInvestigationProbe
{
    // Candidate-only grammar for CF1/CF3 - never applied to production SourceFactsBuilder.
    private static readonly Regex LooseMultiLevelMarker = new(
        @"^\s*(?<value>\d{1,3}(?:\.\d{1,3}){1,4})\s*[\.)]?(?=\s*\S)", RegexOptions.Compiled);
    private static readonly Regex StrictMarker = new(
        @"^\s*(?<value>\d{1,3}(?:\.\d{1,3}){0,4})\s*[\.)](?=\s*\S)", RegexOptions.Compiled);

    [Fact]
    public void WriteInvestigation()
    {
        var output = Environment.GetEnvironmentVariable("R2_BOUNDED_INVESTIGATION_REPORT");
        if (string.IsNullOrWhiteSpace(output)) return;

        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var report = BuildReport(root);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public void CommittedInvestigationReproduces()
    {
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var path = Path.Combine(root, "eval", "benchmark-n0", "n2-s", "counterfactual", "057-r2-bounded-investigation.v1.json");
        if (!File.Exists(path)) return;

        var expected = JsonSerializer.Serialize(BuildReport(root), new JsonSerializerOptions { WriteIndented = true });
        Assert.Equal(expected.Replace("\r\n", "\n"), File.ReadAllText(path).Replace("\r\n", "\n"));
    }

    private static object BuildReport(string root)
    {
        // --- Positive population: 057's real 22 targets ---
        using var taxonomy = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "eval", "benchmark-n0", "n2-s", "counterfactual", "057-pdf-docx-divergence-taxonomy.v1.json")));
        var targetStableIds = taxonomy.RootElement.GetProperty("rows").EnumerateArray()
            .Where(r => r.GetProperty("divergenceOwner").GetString() is "DOCX_AUTO_NUMBERED_TITLE_ONLY" or "FULL_TEXT_FOUND_VERBATIM")
            .Select(r => r.GetProperty("StableId").GetString()!)
            .ToHashSet(StringComparer.Ordinal);

        using var reconciliation = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "eval", "benchmark-n0", "n2-s", "reconciliation", "057-n2-s-reconciliation.v1.json")));
        var sourceFactIdByStableId = reconciliation.RootElement.GetProperty("occurrences").EnumerateArray()
            .Where(o => targetStableIds.Contains(o.GetProperty("stableId").GetString()!))
            .ToDictionary(o => o.GetProperty("stableId").GetString()!, o => o.GetProperty("coveringSourceFactId").GetString()!, StringComparer.Ordinal);

        var docx = Path.Combine(root, "todo10_8", "heading_corpus_95_word", "04_giao_trinh",
            "057_Quantitative_Methods_in_Finance_Lecture_Notes.docx");
        var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(docx);
        var candidatesById = snapshot.CandidateBlocks.ToDictionary(b => b.Id, StringComparer.Ordinal);

        var slim = new DocxSlimExtractor().Extract(docx);
        var paragraphs = slim.Paragraphs
            .Where(p => p.Role != DocxHeaderExtractor.Core.Models.ParagraphRole.Empty && !p.InTableOfContents && !string.IsNullOrWhiteSpace(p.Text))
            .OrderBy(p => p.Index)
            .Select(p => new PdfLayoutEvidenceOutline.CanonParagraph(p, PdfLayoutEvidenceOutline.CanonicalMap(p.Text)))
            .Where(p => p.Map.Text.Length > 0)
            .ToArray();

        var checkpointPath = Path.Combine(root, "eval", "benchmark-n0", "n2-s", "checkpoints", "057-n2-s.jsonl");
        var checkpoint = new PdfStageCheckpoint(checkpointPath, resume: false, documentIdentity: "057.pdf");
        var headingSpans = checkpoint.ReadCompletedSpanResolutions();

        object RunOne(string stableId, string sourceFactId, bool useLooseMarker, bool useLineReconciliation)
        {
            var block = candidatesById[sourceFactId];
            headingSpans.TryGetValue(sourceFactId, out var modelSpan);

            string headingText;
            string method;
            if (useLineReconciliation)
            {
                var line = LocateOverlappingLine(block, modelSpan);
                if (line is null) return Row(stableId, sourceFactId, false, "R2b_no_overlapping_line_found", null);
                headingText = line;
                method = "line_reconciled";
            }
            else
            {
                headingText = modelSpan is { } s && s.Start >= 0 && s.End > s.Start && s.End <= block.Text.Length
                    ? block.Text[s.Start..s.End]
                    : block.Text;
                method = "raw_model_span";
            }

            var markerMatch = (useLooseMarker ? LooseMultiLevelMarker : StrictMarker).Match(headingText);
            if (!markerMatch.Success) return Row(stableId, sourceFactId, false, "no_marker_recognized", headingText);
            var markerRaw = markerMatch.Value;
            if (markerRaw.Length >= headingText.Length) return Row(stableId, sourceFactId, false, "marker_consumes_whole_text", headingText);

            var titleOnly = headingText[markerRaw.Length..].TrimStart();
            if (titleOnly.Length < 4) return Row(stableId, sourceFactId, false, "title_too_short", headingText);
            var titleCanonical = PdfLayoutEvidenceOutline.CanonicalMap(titleOnly).Text;

            var matches = paragraphs.Where(p => p.Paragraph.NumberingId is not null && p.Map.Text.Contains(titleCanonical, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1) return Row(stableId, sourceFactId, false, matches.Length == 0 ? "zero_numbering_id_match" : "ambiguous_numbering_id_match", headingText);

            return Row(stableId, sourceFactId, true, "matched", headingText, method, markerRaw, titleOnly, matches[0].Paragraph.Index);
        }

        var cf1 = sourceFactIdByStableId.Select(p => RunOne(p.Key, p.Value, useLooseMarker: true, useLineReconciliation: false)).ToArray();
        var cf2 = sourceFactIdByStableId.Select(p => RunOne(p.Key, p.Value, useLooseMarker: false, useLineReconciliation: true)).ToArray();
        var cf3 = sourceFactIdByStableId.Select(p => RunOne(p.Key, p.Value, useLooseMarker: true, useLineReconciliation: true)).ToArray();

        // --- Negative control: does the loose grammar fire where the strict one did not, across every
        // HeadingTopic-classified candidate in the only two canonical checkpoints available? ---
        var negativeControl = new[] { "003", "057" }.Select(stem => BuildNegativeControl(root, stem)).ToArray();

        // --- TOC-contamination check, found while investigating CF1-CF3's low recovery: the earlier
        // divergence taxonomy searched an UNFILTERED paragraph list (including TOC listing lines,
        // which repeat a heading's title with a trailing page number and often carry the same
        // NumberingId style), while production alignment excludes InTableOfContents paragraphs
        // entirely. A target whose only unfiltered match is a TOC line, not a distinct body paragraph,
        // was never really "auto-numbered body text" - it has no valid body anchor at all. ---
        var tocContamination = sourceFactIdByStableId.Keys.Select(stableId =>
        {
            var titleOnlyText = taxonomy.RootElement.GetProperty("rows").EnumerateArray()
                .First(r => r.GetProperty("StableId").GetString() == stableId).GetProperty("titleOnlyText").GetString()!;
            var titleCanonical = PdfTextUtilities.CanonicalForMatch(titleOnlyText);
            var matches = slim.Paragraphs
                .Where(p => PdfTextUtilities.CanonicalForMatch(p.Text).Contains(titleCanonical, StringComparison.Ordinal))
                .Select(p => new { index = p.Index, toc = p.InTableOfContents, numberingId = p.NumberingId })
                .ToArray();
            var hasRealBodyMatch = matches.Any(m => !m.toc);
            var hasRealBodyMatchWithNumberingId = matches.Any(m => !m.toc && m.numberingId is not null);
            return new { stableId, matchCount = matches.Length, matches, hasRealBodyMatch, hasRealBodyMatchWithNumberingId };
        }).OrderBy(r => r.stableId, StringComparer.Ordinal).ToArray();

        return new
        {
            schemaVersion = 1,
            artifactKind = "n2s_r2_bounded_investigation",
            usesModel = false,
            scope = "Counterfactual only - no production code change. R2a: loosened marker grammar (candidate-only regex). R2b: HeadingSpan reconciled to the overlapping constituent source line.",
            positiveResults = new
            {
                cf1_markerGrammarOnly = new { resolved = cf1.Count(r => (bool)((dynamic)r).resolved), outOf = cf1.Length, rows = cf1 },
                cf2_lineReconciliationOnly = new { resolved = cf2.Count(r => (bool)((dynamic)r).resolved), outOf = cf2.Length, rows = cf2 },
                cf3_both = new { resolved = cf3.Count(r => (bool)((dynamic)r).resolved), outOf = cf3.Length, rows = cf3 },
            },
            negativeControl,
            tocContaminationCheck = new
            {
                note = "Corrects the earlier divergence taxonomy, which did not distinguish TOC listing lines from real body paragraphs when it reported 21/23 DOCX_AUTO_NUMBERED_TITLE_ONLY.",
                targetsWithRealBodyMatch = tocContamination.Count(r => r.hasRealBodyMatch),
                targetsWithRealBodyMatchAndNumberingId = tocContamination.Count(r => r.hasRealBodyMatchWithNumberingId),
                targetsWithTocOnlyMatch = tocContamination.Count(r => !r.hasRealBodyMatch && r.matchCount > 0),
                targetsWithNoMatchAtAll = tocContamination.Count(r => r.matchCount == 0),
                rows = tocContamination,
            },
        };
    }

    /// <summary>
    /// Locates the constituent <c>PdfLine</c> that the model's proposed span overlaps, by finding each
    /// line's literal position within <c>block.Text</c> in source order (never assuming a fixed
    /// separator, since candidate text construction differs by candidate kind). Returns null - not a
    /// guess - if the lines cannot be located verbatim or the span does not fall inside any of them.
    /// </summary>
    private static string? LocateOverlappingLine(PdfSemanticBlock block, DocxHeaderExtractor.Core.Models.TextOffsetSpan? span)
    {
        if (span is null) return null;
        var cursor = 0;
        foreach (var line in block.Lines)
        {
            var at = block.Text.IndexOf(line.Text, cursor, StringComparison.Ordinal);
            if (at < 0) return null; // construction not literal - abstain, never guess
            var end = at + line.Text.Length;
            if (span.Start >= at && span.Start < end) return line.Text;
            cursor = end;
        }
        return null;
    }

    private static object BuildNegativeControl(string root, string stem)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var checkpointPath = Path.Combine(root, "eval", "benchmark-n0", "n2-s", "checkpoints", $"{stem}-n2-s.jsonl");
        var roleBlocks = File.ReadLines(checkpointPath)
            .Select(line => JsonSerializer.Deserialize<CheckpointEntry>(line, options))
            .Where(e => e is not null).Cast<CheckpointEntry>()
            .Where(e => e.Lane == "semantic")
            .SelectMany(e => e.Payload.Blocks)
            .Where(b => string.Equals(b.Role, "HeadingTopic", StringComparison.OrdinalIgnoreCase) && b.Confidence >= 0.65)
            .ToArray();

        var docx = stem == "003"
            ? Path.Combine(root, "todo10_8", "heading_corpus_95_word", "01_phap_quy", "003_Luat_Doanh_nghiep_59-2020-QH14.docx")
            : Path.Combine(root, "todo10_8", "heading_corpus_95_word", "04_giao_trinh", "057_Quantitative_Methods_in_Finance_Lecture_Notes.docx");
        var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(docx);
        var textById = snapshot.CandidateBlocks.ToDictionary(b => b.Id, b => b.Text, StringComparer.Ordinal);

        var newlyFiring = new List<object>();
        foreach (var b in roleBlocks)
        {
            if (!textById.TryGetValue(b.Id, out var text)) continue;
            var strict = StrictMarker.Match(text);
            var loose = LooseMultiLevelMarker.Match(text);
            if (loose.Success && (!strict.Success || strict.Value != loose.Value))
                newlyFiring.Add(new { id = b.Id, strictMarker = strict.Success ? strict.Value : null, looseMarker = loose.Value, text = Truncate(text) });
        }

        return new
        {
            stem,
            headingTopicCandidateCount = roleBlocks.Length,
            newlyFiringOrChangedCount = newlyFiring.Count,
            newlyFiring,
        };
    }

    private static object Row(string stableId, string sourceFactId, bool resolved, string reason, string? headingText,
        string? method = null, string? markerRaw = null, string? titleOnly = null, int? matchedParagraphIndex = null) => new
    {
        stableId,
        sourceFactId,
        resolved,
        reason,
        headingText = Truncate(headingText),
        method,
        markerRaw,
        titleOnly,
        matchedParagraphIndex,
    };

    private static string? Truncate(string? value) => value is null ? null : value.Length <= 140 ? value : value[..140] + "...";

    private sealed record CheckpointEntry(string Lane, CheckpointPayload Payload);
    private sealed record CheckpointPayload(IReadOnlyList<CheckpointBlock> Blocks);
    private sealed record CheckpointBlock(string Id, string? Role, double Confidence, string? Reason, bool Resolved, int? Start, int? End);
}
