using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Pipeline;
using DocxHeaderExtractor.Core.Eval;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfTaggedHeadingProbeTests
{
    [Fact]
    public void Structure_tree_h_tags_are_read_through_mcid_for_legal_pdf_001()
    {
        var root = RepositoryRoot();
        var docx = Path.Combine(root, "todo10_8", "heading_corpus_95_word", "01_phap_quy", "001_Bo_luat_Dan_su_91-2015-QH13.docx");
        var pdf = Path.Combine(root, "todo10_8", "heading_corpus_100", "01_phap_quy", "001_Bo_luat_Dan_su_91-2015-QH13.pdf");
        if (!File.Exists(docx) || !File.Exists(pdf)) return;

        var report = PdfTaggedHeadingProbe.Analyze(pdf, new DocxSlimExtractor().Extract(docx));

        Assert.True(report.StructureTree.StructTreeRootResolved);
        Assert.Equal(398, report.StructureTree.HeadingNodes);
        Assert.Equal(398, report.HeadingElements);
        Assert.Contains(report.Candidates, candidate => candidate.Text.StartsWith("Điều 1.", StringComparison.Ordinal));
    }

    [Fact]
    public void Explicit_h_levels_ground_all_candidates_for_minutes_076()
    {
        var root = RepositoryRoot();
        var docx = Path.Combine(root, "todo10_8", "heading_corpus_95_word", "05_bien_ban_hop", "076_ICP_IACG08_Minutes_2023.docx");
        var pdf = Path.Combine(root, "todo10_8", "heading_corpus_100", "05_bien_ban_hop", "076_ICP_IACG08_Minutes_2023.pdf");
        if (!File.Exists(docx) || !File.Exists(pdf)) return;

        var slim = new DocxSlimExtractor().Extract(docx);
        var report = PdfTaggedHeadingProbe.Analyze(pdf, slim);

        Assert.Equal("ok", report.Status);
        Assert.Equal(16, report.HeadingElements);
        Assert.Equal(16, report.DocxAligned);
        Assert.All(report.Candidates, c => Assert.Matches("^H[1-6]$", c.Tag));
        Assert.All(report.Candidates, c => Assert.NotNull(c.HeadingSpan));
    }

    [Fact]
    public void Tagged_pdf_076_has_perfect_precision_but_misses_coverage_review_headings()
    {
        var root = RepositoryRoot();
        var docx = Path.Combine(root, "todo10_8", "heading_corpus_95_word", "05_bien_ban_hop", "076_ICP_IACG08_Minutes_2023.docx");
        var pdf = Path.Combine(root, "todo10_8", "heading_corpus_100", "05_bien_ban_hop", "076_ICP_IACG08_Minutes_2023.pdf");
        var keyPath = Path.Combine(root, "keys", "tagged-pdf-coverage", "076_ICP_IACG08_Minutes_2023.key");
        if (!File.Exists(docx) || !File.Exists(pdf) || !File.Exists(keyPath)) return;

        var report = PdfTaggedHeadingProbe.Analyze(pdf, new DocxSlimExtractor().Extract(docx));
        var key = AnswerKey.Load(keyPath);
        var truth = key.PositiveEntries.Where(e => e.StableId is not null && e.Text is not null).ToList();
        var hits = report.Candidates
            .Select(candidate => new
            {
                Candidate = candidate,
                Truth = truth.FirstOrDefault(entry =>
                    entry.StableId == candidate.DocxStableId &&
                    CanonicallyMatches(entry.Text!, candidate.CanonicalText))
            })
            .Where(match => match.Truth is not null)
            .ToList();

        Assert.Equal(24, truth.Count);
        Assert.Equal(16, report.Candidates.Count);
        Assert.Equal(16, hits.Count);
        Assert.All(hits, match => Assert.Equal(match.Truth!.Level, match.Candidate.Level));
        Assert.Equal(8, truth.Count(entry => !hits.Any(match => match.Truth == entry)));
    }

    [Fact]
    public void Tagged_pdf_078_confirms_the_coverage_recall_limit_on_holdout()
    {
        var root = RepositoryRoot();
        var docx = Path.Combine(root, "todo10_8", "heading_corpus_95_word", "05_bien_ban_hop", "078_ICP_IACG07_Minutes_May_2023.docx");
        var pdf = Path.Combine(root, "todo10_8", "heading_corpus_100", "05_bien_ban_hop", "078_ICP_IACG07_Minutes_May_2023.pdf");
        var keyPath = Path.Combine(root, "keys", "tagged-pdf-coverage", "078_ICP_IACG07_Minutes_May_2023.key");
        if (!File.Exists(docx) || !File.Exists(pdf) || !File.Exists(keyPath)) return;

        var report = PdfTaggedHeadingProbe.Analyze(pdf, new DocxSlimExtractor().Extract(docx));
        var key = AnswerKey.Load(keyPath);
        var truth = key.PositiveEntries.Where(e => e.StableId is not null && e.Text is not null).ToList();
        var hits = report.Candidates
            .Select(candidate => new
            {
                Candidate = candidate,
                Truth = truth.FirstOrDefault(entry =>
                    entry.StableId == candidate.DocxStableId &&
                    CanonicallyMatches(entry.Text!, candidate.CanonicalText))
            })
            .Where(match => match.Truth is not null)
            .ToList();

        Assert.Equal(27, truth.Count);
        Assert.Equal(20, report.Candidates.Count);
        Assert.Equal(20, hits.Count);
        Assert.All(hits, match => Assert.Equal(match.Truth!.Level, match.Candidate.Level));
        Assert.Equal(7, truth.Count(entry => !hits.Any(match => match.Truth == entry)));
    }

    [Theory]
    [InlineData("076_ICP_IACG08_Minutes_2023")]
    [InlineData("078_ICP_IACG07_Minutes_May_2023")]
    public void Repeated_label_marker_recovers_all_agenda_days_on_both_minutes(string stem)
    {
        var root = RepositoryRoot();
        var pdf = Path.Combine(root, "todo10_8", "heading_corpus_100", "05_bien_ban_hop", $"{stem}.pdf");
        if (!File.Exists(pdf)) return;

        var report = PdfRepeatedLabelMarkerProbe.Analyze(pdf);
        var days = Assert.Single(report.Series, series => series.Label == "day");

        Assert.Equal("ok", report.Status);
        Assert.Equal([1, 2, 3, 4], days.Markers.Select(marker => marker.Number));
    }

    [Theory]
    [InlineData("076_ICP_IACG08_Minutes_2023", 24, 20)]
    [InlineData("078_ICP_IACG07_Minutes_May_2023", 27, 24)]
    public void Tags_plus_day_markers_improve_recall_without_false_positives(
        string stem,
        int expectedTruth,
        int expectedHits)
    {
        var root = RepositoryRoot();
        var docx = Path.Combine(root, "todo10_8", "heading_corpus_95_word", "05_bien_ban_hop", $"{stem}.docx");
        var pdf = Path.Combine(root, "todo10_8", "heading_corpus_100", "05_bien_ban_hop", $"{stem}.pdf");
        var keyPath = Path.Combine(root, "keys", "tagged-pdf-coverage", $"{stem}.key");
        if (!File.Exists(docx) || !File.Exists(pdf) || !File.Exists(keyPath)) return;

        var key = AnswerKey.Load(keyPath);
        var truth = key.PositiveEntries.Where(entry => entry.Text is not null).ToList();
        var tagReport = PdfTaggedHeadingProbe.Analyze(pdf, new DocxSlimExtractor().Extract(docx));
        var tags = tagReport.Candidates.Count(candidate =>
            truth.Any(entry => entry.StableId == candidate.DocxStableId &&
                CanonicallyMatches(entry.Text!, candidate.CanonicalText)));
        var daySeries = Assert.Single(PdfRepeatedLabelMarkerProbe.Analyze(pdf).Series,
            series => series.Label == "day");
        var days = daySeries.Markers.Count(marker =>
            truth.Any(entry => entry.Level == 2 && CanonicallyMatches(entry.Text!, Canonical(marker.Text))));

        Assert.Equal(expectedTruth, truth.Count);
        Assert.Equal(expectedHits, tags + days);
        Assert.Equal(4, days);
    }

    [Theory]
    [InlineData("076_ICP_IACG08_Minutes_2023", 20)]
    [InlineData("078_ICP_IACG07_Minutes_May_2023", 24)]
    public void Tagged_structure_sandbox_fuses_only_grounded_consecutive_markers(string stem, int expectedCount)
    {
        var root = RepositoryRoot();
        var docx = Path.Combine(root, "todo10_8", "heading_corpus_95_word", "05_bien_ban_hop", $"{stem}.docx");
        if (!File.Exists(docx)) return;

        var result = PdfTaggedEvidenceOutline.TryBuild(docx, new DocxSlimExtractor().Extract(docx));

        Assert.True(result.Accepted, result.Reason);
        Assert.Equal(expectedCount, result.Headings.Count);
        Assert.Equal(4, result.Headings.Count(h => h.Text.StartsWith("DAY ", StringComparison.OrdinalIgnoreCase)));
        Assert.All(result.Headings.Where(h => h.Text.StartsWith("DAY ", StringComparison.OrdinalIgnoreCase)),
            heading => Assert.Equal(2, heading.Level));
        var keyPath = Path.Combine(root, "keys", "tagged-pdf-coverage", $"{stem}.key");
        if (!File.Exists(keyPath)) return;
        var titles = AnswerKey.Load(keyPath).PositiveEntries.Select(entry => entry.Text!).ToList();
        Assert.Equal(expectedCount, result.Headings.Count(heading =>
            titles.Any(title => CanonicallyMatches(title, Canonical(heading.Text)))));
    }

    [Theory]
    [InlineData("076_ICP_IACG08_Minutes_2023", 16)]
    [InlineData("078_ICP_IACG07_Minutes_May_2023", 20)]
    public void Tagged_title_probe_recovers_clean_visual_prefixes(string stem, int expectedMatches)
    {
        var root = RepositoryRoot();
        var docx = Path.Combine(root, "todo10_8", "heading_corpus_95_word", "05_bien_ban_hop", $"{stem}.docx");
        var pdf = Path.Combine(root, "todo10_8", "heading_corpus_100", "05_bien_ban_hop", $"{stem}.pdf");
        var keyPath = Path.Combine(root, "keys", "tagged-pdf-coverage", $"{stem}.key");
        if (!File.Exists(docx) || !File.Exists(pdf) || !File.Exists(keyPath)) return;

        var tags = PdfTaggedHeadingProbe.Analyze(pdf, new DocxSlimExtractor().Extract(docx));
        var grounded = PdfTaggedTitleGroundingProbe.Analyze(pdf, tags);
        var titles = AnswerKey.Load(keyPath).PositiveEntries.Select(entry => entry.Text!).ToList();
        var matches = grounded.Candidates.Count(candidate => candidate.GroundedTitle is not null &&
            titles.Any(title => CanonicallyMatches(title, Canonical(candidate.GroundedTitle))));

        Assert.Equal(expectedMatches, matches);
    }

    [Theory]
    [InlineData("076_ICP_IACG08_Minutes_2023", 24, 20)]
    [InlineData("078_ICP_IACG07_Minutes_May_2023", 27, 24)]
    public async Task Production_tagged_route_matches_the_coverage_key(string stem, int expectedTruth, int expectedHits)
    {
        var root = RepositoryRoot();
        var docx = Path.Combine(root, "todo10_8", "heading_corpus_95_word", "05_bien_ban_hop", $"{stem}.docx");
        var keyPath = Path.Combine(root, "keys", "tagged-pdf-coverage", $"{stem}.key");
        if (!File.Exists(docx) || !File.Exists(keyPath)) return;

        using var pipeline = new AuthorityExtractionPipeline(new PipelineOptions { DisableLlm = true });
        var outline = await pipeline.RunAsync(docx);
        var slim = new DocxSlimExtractor().Extract(docx);
        var key = AnswerKey.Load(keyPath).ResolveStableIds(slim.Paragraphs.ToDictionary(p => p.StableId, p => p.Index));
        var score = Evaluator.Score(docx, outline, [], key);

        Assert.Equal("auto:pdf-tagged-structure", outline.DeterministicRoute);
        Assert.Equal(expectedTruth, key.Count);
        Assert.Equal(expectedHits, score.TruePositive);
        Assert.Equal(expectedHits, score.NavigationTitleHits);
        Assert.Equal(expectedHits, score.NavigationLevelHits);
        Assert.Empty(score.FalsePositives);
    }

    [Fact]
    public void Noisy_structure_tree_tags_are_not_evidence_of_a_usable_outline()
    {
        var root = RepositoryRoot();
        var docx = Path.Combine(root, "todo10_8", "heading_corpus_95_word", "04_giao_trinh", "056_OpenStax_Business_Law_I_Essentials.docx");
        var pdf = Path.Combine(root, "todo10_8", "heading_corpus_100", "04_giao_trinh", "056_OpenStax_Business_Law_I_Essentials.pdf");
        if (!File.Exists(docx) || !File.Exists(pdf)) return;

        var slim = new DocxSlimExtractor().Extract(docx);
        var report = PdfTaggedHeadingProbe.Analyze(pdf, slim);

        Assert.True(report.StructureTree.StructTreeRootResolved);
        Assert.Equal(337, report.HeadingElements);
        Assert.True(report.HeadingElements > 7 * 46); // The independent key has 46 navigation headings.
        Assert.Contains(report.Candidates, c => c.Tag == "H3");
        Assert.Contains(report.Candidates, c => c.Tag == "H4");
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DocxHeaderExtractor.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Cannot find repository root.");
    }

    private static bool CanonicallyMatches(string expected, string actual)
    {
        var canonical = new string(expected.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return actual.Contains(canonical, StringComparison.Ordinal) || canonical.Contains(actual, StringComparison.Ordinal);
    }

    private static string Canonical(string text) =>
        new string(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
