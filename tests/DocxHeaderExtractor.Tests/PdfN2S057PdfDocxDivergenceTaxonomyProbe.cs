using System.Text.Json;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The representation audit found that 22/23 of 057's undelivered decisionRelevant occurrences don't
/// exist verbatim in the DOCX at all - not just the 9 already classified `NO_DOCX_SOURCE_ANCHOR`, but
/// also the 4 `WindowFragment`-representation cases and most of the 10 marker-depth cases whose
/// grounding-gate fix (<see cref="PdfN2S057MarkerAwareGroundingCounterfactualProbe"/>) would only move
/// them to the same wall. This traces WHY the text is absent, splitting the marker away from the title
/// and testing each separately against the DOCX paragraphs and their own numbering metadata - no
/// provider call, no candidate construction change.
/// </summary>
public sealed class PdfN2S057PdfDocxDivergenceTaxonomyProbe
{
    [Fact]
    public void WriteTaxonomy()
    {
        var output = Environment.GetEnvironmentVariable("N2S_057_DIVERGENCE_REPORT");
        if (string.IsNullOrWhiteSpace(output)) return;

        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var report = BuildReport(root);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public void CommittedTaxonomyReproduces()
    {
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var path = Path.Combine(root, "eval", "benchmark-n0", "n2-s", "counterfactual", "057-pdf-docx-divergence-taxonomy.v1.json");
        if (!File.Exists(path)) return;

        var expected = JsonSerializer.Serialize(BuildReport(root), new JsonSerializerOptions { WriteIndented = true });
        Assert.Equal(expected.Replace("\r\n", "\n"), File.ReadAllText(path).Replace("\r\n", "\n"));
    }

    private static object BuildReport(string root)
    {
        using var repAudit = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "eval", "benchmark-n0", "n2-s", "counterfactual", "057-representation-audit.v1.json")));
        var targets = repAudit.RootElement.GetProperty("rows").EnumerateArray()
            .Select(r => (
                StableId: r.GetProperty("StableId").GetString()!,
                Owner: r.GetProperty("Owner").GetString()!,
                RequiredOnlyText: r.GetProperty("requiredOnlyText").GetString()!))
            .ToArray();

        var docx = Path.Combine(root, "todo10_8", "heading_corpus_95_word", "04_giao_trinh",
            "057_Quantitative_Methods_in_Finance_Lecture_Notes.docx");
        var slim = new DocxSlimExtractor().Extract(docx);
        var paragraphs = slim.Paragraphs
            .Select(p => (Paragraph: p, Canonical: PdfTextUtilities.CanonicalForMatch(p.Text)))
            .Where(p => p.Canonical.Length > 0)
            .ToArray();

        var rows = targets.Select(target =>
        {
            // The marker is the stableId's own suffix (e.g. "057/numbered/10.0.1" -> "10.0.1") -
            // exact, since the silver labeler read it directly from source, never re-derived from a
            // regex that can backtrack to the wrong length when the marker has no trailing "." or ")"
            // in the source text (an earlier version of this probe did exactly that and mis-stripped
            // markers like "9.4" down to "9.").
            var markerText = target.StableId[(target.StableId.LastIndexOf('/') + 1)..];
            var markerPattern = string.Join(@"\s*", markerText.Select(c => Regex.Escape(c.ToString())));
            var stripped = Regex.Match(target.RequiredOnlyText, $@"^\s*{markerPattern}\s*[\.)]?\s*");
            var titleOnly = stripped.Success ? target.RequiredOnlyText[stripped.Length..] : target.RequiredOnlyText;
            var fullCanonical = PdfTextUtilities.CanonicalForMatch(target.RequiredOnlyText);
            var titleCanonical = PdfTextUtilities.CanonicalForMatch(titleOnly);

            var fullMatch = paragraphs.FirstOrDefault(p => p.Canonical.Contains(fullCanonical, StringComparison.Ordinal));
            if (fullMatch.Paragraph is not null)
                return Row(target, titleOnly, "FULL_TEXT_FOUND_VERBATIM", fullMatch.Paragraph.NumberingId is not null);

            var titleMatch = paragraphs.FirstOrDefault(p => titleCanonical.Length > 0 && p.Canonical.Contains(titleCanonical, StringComparison.Ordinal));
            if (titleMatch.Paragraph is not null)
                return Row(target, titleOnly,
                    titleMatch.Paragraph.NumberingId is not null ? "DOCX_AUTO_NUMBERED_TITLE_ONLY" : "TITLE_ONLY_FOUND_NO_NUMBERING",
                    titleMatch.Paragraph.NumberingId is not null);

            // Title split across two adjacent DOCX paragraphs (e.g. a manual line break DOCX
            // represents as a paragraph break, or the marker and title were typed as separate runs
            // the extractor did not join).
            for (var i = 0; i < paragraphs.Length - 1; i++)
            {
                var joined = paragraphs[i].Canonical + paragraphs[i + 1].Canonical;
                if (titleCanonical.Length > 0 && joined.Contains(titleCanonical, StringComparison.Ordinal))
                    return Row(target, titleOnly, "TITLE_SPANS_ADJACENT_DOCX_PARAGRAPHS", null);
            }

            return Row(target, titleOnly, "NOT_FOUND_EVEN_TITLE_ONLY", null);
        }).ToArray();

        var tally = rows.GroupBy(r => (string)((dynamic)r).divergenceOwner, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        return new
        {
            schemaVersion = 1,
            artifactKind = "n2s_057_pdf_docx_divergence_taxonomy",
            usesModel = false,
            hypothesisTested = "DOCX Word-native auto-numbering (NumberingId set, number not literal paragraph text) vs the PDF render, which bakes the visible number into extracted line text.",
            tally,
            rows,
        };
    }

    private static object Row((string StableId, string Owner, string RequiredOnlyText) target, string titleOnly, string owner, bool? matchedParagraphHasNumbering) => new
    {
        target.StableId,
        groundingOwner = target.Owner,
        titleOnlyText = titleOnly,
        divergenceOwner = owner,
        matchedParagraphHasNumbering,
    };
}
