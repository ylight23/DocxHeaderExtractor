using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Application.Features;
using DocxHeaderExtractor.Core.Application.Policy;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class DocumentDiagnosticRunnerTests
{
    [Fact]
    public void Diagnostic_danh_dau_style_tron_va_khong_accept_style_candidate()
    {
        var paragraphs = Enumerable.Range(0, 12)
            .Select(i => new SlimParagraph
            {
                Index = i,
                StableId = $"p[{i}]",
                Text = i % 2 == 0
                    ? $"Heading-like {i}"
                    : $"This is a long body paragraph that was accidentally formatted as Heading 2 and should not be trusted as an outline item {i}.",
                HasBuiltInHeadingStyle = true,
                StyleId = "Heading2",
                StyleName = "heading 2",
                GuessedLevel = 2,
                Role = ParagraphRole.StyledHeading,
                FontSizePt = 12,
            })
            .ToList();
        var slim = new SlimDocument
        {
            FileName = "mixed-style.docx",
            SourcePath = "mixed-style.docx",
            Paragraphs = paragraphs,
            StyleTrust = new StyleTrust(
                StyledCount: 12,
                SuspectCount: 6,
                DistinctLevels: 1,
                SkipsLevels: false,
                Density: 1.0),
            Mode = new DocumentModeReport(
                DocumentMode.CustomStyle,
                Paragraphs: 12,
                StyledHeadings: 12,
                OutlineLevelRatio: 0,
                VietnameseAdminRatio: 0,
                TypedNumberRatio: 0,
                NumberingRatio: 0,
                FormatDiffers: true),
        }.Build();

        var source = DocxSourceFactsBuilder.Build(slim.SourcePath, slim.Paragraphs, [], []);
        var features = NumberingStyleFeatures.FromSourceDocument(source);
        var built = DocxPolicyStateBuilder.Build(source, features,
            new DocumentFeatureDeriver().Derive(source), new ExtractionOptions());
        var policy = new DocxPolicyState(source, features, built.DerivedFeatures, built.Paragraphs,
            slim.StyleTrust, slim.Mode);
        var report = DocumentDiagnosticRunner.Analyze(policy, slim.Mode!);

        Assert.Equal("needs_analysis", report.Status);
        Assert.Equal("mixed_style_signals", report.Reason);
        Assert.True(report.Style.Mixed);
        Assert.False(report.Style.SelectionTrusted);
        Assert.False(report.Style.LevelTrusted);
        Assert.Contains(report.Candidates, c =>
            c.Route == "auto:style-declared" &&
            !c.Accepted &&
            c.Reason == "style_selection_untrusted");
    }

    [Fact]
    public async Task Pipeline_tra_diagnostic_report_trong_outline()
    {
        var root = RepositoryRoot();
        var docx = Path.Combine(
            root, "todo10_8", "heading_corpus_95_word", "07_system_generated",
            "092_RFC9111_HTTP_Caching.docx");

        using var pipeline = new AuthorityExtractionPipeline(new PipelineOptions { DisableLlm = true });
        var outline = await pipeline.RunAsync(docx);

        Assert.NotNull(outline.Diagnostics);
        Assert.Contains(outline.Diagnostics!.Candidates, c =>
            c.Route == "auto:rfc-toc-dictionary" &&
            c.Accepted &&
            c.BodyAnchorRatio >= 0.90);
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DocxHeaderExtractor.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Cannot find repository root.");
    }
}
