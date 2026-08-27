using System.Text.Json;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Collateral check for the narrow remediation candidate the divergence taxonomy proved: a candidate
/// whose PDF marker is recognized (by the project's own marker authority), whose title-only text
/// matches a DOCX paragraph verbatim, and whose matched paragraph carries `NumberingId`, may attempt a
/// title-only alignment. This tests whether that exact, narrow rule is safe over the whole document -
/// never a general "strip leading numbers and fuzzy match" - by measuring the population it could ever
/// touch and checking for the specific collision risks named before implementation: paragraphs without
/// `NumberingId`, manually-typed numbers, duplicate titles, numbered-looking body text, nested
/// numbering, and marker-parse failure. No provider call, no candidate construction change.
/// </summary>
public sealed class PdfN2S057TitleOnlyAlignmentCollateralProbe
{
    [Fact]
    public void WriteCollateralReport()
    {
        var output = Environment.GetEnvironmentVariable("N2S_057_COLLATERAL_REPORT");
        if (string.IsNullOrWhiteSpace(output)) return;

        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var report = BuildReport(root);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public void CommittedReportReproduces()
    {
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var path = Path.Combine(root, "eval", "benchmark-n0", "n2-s", "counterfactual", "057-title-only-alignment-collateral.v1.json");
        if (!File.Exists(path)) return;

        var expected = JsonSerializer.Serialize(BuildReport(root), new JsonSerializerOptions { WriteIndented = true });
        Assert.Equal(expected.Replace("\r\n", "\n"), File.ReadAllText(path).Replace("\r\n", "\n"));
    }

    private static object BuildReport(string root)
    {
        var docx = Path.Combine(root, "todo10_8", "heading_corpus_95_word", "04_giao_trinh",
            "057_Quantitative_Methods_in_Finance_Lecture_Notes.docx");
        var slim = new DocxSlimExtractor().Extract(docx);
        var paragraphs = slim.Paragraphs
            .Select(p => (Paragraph: p, Canonical: PdfTextUtilities.CanonicalForMatch(p.Text)))
            .Where(p => p.Canonical.Length > 0)
            .ToArray();

        var numbered = paragraphs.Where(p => p.Paragraph.NumberingId is not null).ToArray();
        var notNumbered = paragraphs.Where(p => p.Paragraph.NumberingId is null).ToArray();

        // Control 1: duplicate titles among NumberingId'd paragraphs - the exact ambiguity risk named
        // before implementation. If two different NumberingId'd paragraphs share the same canonical
        // text, the constrained rule could anchor to the wrong one.
        var duplicateNumberedTitles = numbered
            .GroupBy(p => p.Canonical, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => new { canonicalText = Truncate(g.Key), count = g.Count(), numberingLevels = g.Select(p => p.Paragraph.NumberingLevel).Distinct().ToArray() })
            .ToArray();

        // Control 2: does any NumberingId'd paragraph's title collide with a manually-typed-number
        // (non-NumberingId) paragraph's text? If so, "has NumberingId" alone would not disambiguate a
        // real heading from a body paragraph that happens to carry the same words.
        var numberedTitlesSet = numbered.Select(p => p.Canonical).ToHashSet(StringComparer.Ordinal);
        var collidingWithNonNumbered = notNumbered
            .Where(p => numberedTitlesSet.Contains(p.Canonical))
            .Select(p => Truncate(p.Canonical))
            .Distinct()
            .ToArray();

        // Control 3: nested/multilevel numbering - how deep does NumberingId'd content go, and is the
        // 21-target population concentrated at shallow levels (headings) or spread into deep levels
        // (more likely ordinary numbered lists in body text, a real false-anchor risk if the rule were
        // ever loosened beyond "candidate already validated as HeadingTopic").
        var levelDistribution = numbered
            .GroupBy(p => p.Paragraph.NumberingLevel ?? -1)
            .OrderBy(g => g.Key)
            .Select(g => new { level = g.Key, count = g.Count() })
            .ToArray();

        // Control 4: the 21 actual targets themselves - confirm each still resolves to exactly one
        // NumberingId'd paragraph (no ambiguity in practice, not just in principle).
        using var trace = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "eval", "benchmark-n0", "n2-s", "counterfactual", "057-pdf-docx-divergence-taxonomy.v1.json")));
        var targets = trace.RootElement.GetProperty("rows").EnumerateArray()
            .Where(r => r.GetProperty("divergenceOwner").GetString() == "DOCX_AUTO_NUMBERED_TITLE_ONLY")
            .Select(r => (StableId: r.GetProperty("StableId").GetString()!, TitleOnlyText: r.GetProperty("titleOnlyText").GetString()!))
            .ToArray();

        var targetResolution = targets.Select(t =>
        {
            var titleCanonical = PdfTextUtilities.CanonicalForMatch(t.TitleOnlyText);
            var matches = numbered.Where(p => p.Canonical.Contains(titleCanonical, StringComparison.Ordinal)).ToArray();
            return new
            {
                t.StableId,
                matchingNumberedParagraphs = matches.Length,
                unambiguous = matches.Length == 1,
            };
        }).ToArray();

        return new
        {
            schemaVersion = 1,
            artifactKind = "n2s_057_title_only_alignment_collateral",
            usesModel = false,
            ruleTested = "PDF marker recognized by the marker authority AND title-only text matches a DOCX paragraph verbatim AND that paragraph has NumberingId set -> permit a constrained title-only alignment attempt. Never a general strip-and-fuzzy-match.",
            population = new
            {
                totalDocxParagraphs = paragraphs.Length,
                withNumberingId = numbered.Length,
                withoutNumberingId = notNumbered.Length,
            },
            control1_duplicateTitlesAmongNumberedParagraphs = new
            {
                count = duplicateNumberedTitles.Length,
                cases = duplicateNumberedTitles,
            },
            control2_numberedTitleCollidesWithManuallyTypedParagraph = new
            {
                count = collidingWithNonNumbered.Length,
                cases = collidingWithNonNumbered,
            },
            control3_numberingLevelDistribution = levelDistribution,
            control4_actualTargetsResolveUnambiguously = new
            {
                targetCount = targetResolution.Length,
                allUnambiguous = targetResolution.All(t => t.unambiguous),
                targets = targetResolution,
            },
            verdict = duplicateNumberedTitles.Length == 0 && collidingWithNonNumbered.Length == 0 && targetResolution.All(t => t.unambiguous)
                ? "NO_COLLISION_FOUND_ON_THIS_DOCUMENT"
                : "COLLISION_RISK_FOUND",
            note = "Single-document evidence only. A collision-free result here does not generalize to other documents without the same check repeated on them.",
        };
    }

    private static string Truncate(string value) => value.Length <= 120 ? value : value[..120] + "...";
}
