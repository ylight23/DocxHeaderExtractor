using System.Text.Json;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Validates B3's real production function against 057's actual 22 targets (21
/// `DOCX_AUTO_NUMBERED_TITLE_ONLY` + 1 `FULL_TEXT_FOUND_VERBATIM`) using the real candidate block text
/// and the real <see cref="SourceFactsBuilder"/> marker parser - not the ground-truth,
/// stableId-derived marker the earlier diagnostic probes used. The marker parser's decimal regex
/// requires a terminating <c>.</c> or <c>)</c> immediately after the full numeric value
/// (<c>9.4.</c> matches in full; <c>9.4 Title</c> with no following punctuation only matches <c>9.</c>,
/// leaving a stray digit stuck to the title) - a real, pre-existing characteristic of the shared
/// marker parser this fix must use as-is, not work around with a new regex. This measures how many of
/// the 21 real targets the production fix actually recovers given that exact limitation, rather than
/// assuming the diagnostic-probe recovery count carries over unchanged.
/// </summary>
public sealed class PdfB3RealTargetValidationProbe
{
    [Fact]
    public void WriteValidation()
    {
        var output = Environment.GetEnvironmentVariable("B3_REAL_VALIDATION_REPORT");
        if (string.IsNullOrWhiteSpace(output)) return;

        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var report = BuildReport(root);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public void CommittedValidationReproduces()
    {
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var path = Path.Combine(root, "eval", "benchmark-n0", "n2-s", "counterfactual", "057-b3-real-target-validation.v1.json");
        if (!File.Exists(path)) return;

        var expected = JsonSerializer.Serialize(BuildReport(root), new JsonSerializerOptions { WriteIndented = true });
        Assert.Equal(expected.Replace("\r\n", "\n"), File.ReadAllText(path).Replace("\r\n", "\n"));
    }

    private static object BuildReport(string root)
    {
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

        // The real resolved span, from the real canonical checkpoint - not the full candidate window
        // text - is what B3's production code actually receives at the real call site.
        var checkpoint = new PdfStageCheckpoint(
            Path.Combine(root, "eval", "benchmark-n0", "n2-s", "checkpoints", "057-n2-s.jsonl"),
            resume: false, documentIdentity: "057_Quantitative_Methods_in_Finance_Lecture_Notes.pdf");
        var headingSpans = checkpoint.ReadCompletedSpanResolutions();

        var rows = sourceFactIdByStableId.Select(pair =>
        {
            var (stableId, sourceFactId) = (pair.Key, pair.Value);
            if (!candidatesById.TryGetValue(sourceFactId, out var block))
                return new { stableId, sourceFactId, resolved = false, reason = "candidate_not_in_current_snapshot", blockText = (string?)null, headingText = (string?)null, matchedParagraphIndex = (int?)null };

            var headingSpan = headingSpans.GetValueOrDefault(sourceFactId);
            var match = PdfLayoutEvidenceOutline.FindAutoNumberedTitleOnlyMatch(paragraphs, block, headingSpan);
            return new
            {
                stableId,
                sourceFactId,
                resolved = match is not null,
                reason = match is not null ? "matched" : headingSpan is null ? "no_checkpointed_span" : "no_unique_numbering_id_title_match",
                blockText = Truncate(block.Text),
                headingText = Truncate(headingSpan is { } s && s.Start >= 0 && s.End <= block.Text.Length && s.End > s.Start ? block.Text[s.Start..s.End] : block.Text),
                matchedParagraphIndex = match?.Paragraph.Index,
            };
        }).OrderBy(r => r.stableId, StringComparer.Ordinal).ToArray();

        return new
        {
            schemaVersion = 1,
            artifactKind = "n2s_057_b3_real_target_validation",
            usesModel = false,
            purpose = "Measures B3's real production function against 057's actual 22 targets, using the real marker parser (not the ground-truth stableId-derived marker the diagnostic probes used).",
            targetCount = rows.Length,
            resolvedCount = rows.Count(r => r.resolved),
            unresolvedCount = rows.Count(r => !r.resolved),
            rows,
        };
    }

    private static string? Truncate(string? value) => value is null ? null : value.Length <= 140 ? value : value[..140] + "...";
}
