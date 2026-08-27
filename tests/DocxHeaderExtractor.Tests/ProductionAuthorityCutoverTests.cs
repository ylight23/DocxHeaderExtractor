using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class ProductionAuthorityCutoverTests
{
    [Fact]
    public void NormalPipelineOptionsEnterTheCanonicalAuthorityRouteByDefault()
    {
        var options = new PipelineOptions();

        Assert.True(options.PdfFirstValidatedFallback);
    }

    [Fact]
    public async Task NoLlmBuiltInStylesBecomeGroundedProductHeadings()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dhx-authority-{Guid.NewGuid():N}.docx");
        try
        {
            SampleDocumentFactory.Create(path);
            using var pipeline = new AuthorityExtractionPipeline(new PipelineOptions { DisableLlm = true });
            var outline = await pipeline.RunAsync(path);
            Assert.Equal(4, outline.Headings.Count);
            Assert.NotNull(outline.ProductOutput);
            Assert.Equal(4, outline.ProductOutput!.Headings.Count);
            Assert.DoesNotContain(outline.Headings, heading => heading.Text.StartsWith("2.1 ", StringComparison.Ordinal));
            Assert.All(outline.Provenance.Passes, pass => Assert.False(pass.SentDataExternally));
        }
        finally
        {
            LegacyDocConverter.TryDelete(path);
        }
    }

    [Fact]
    public async Task Quarantine_is_applied_before_deterministic_proposal_and_output()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dhx-authority-quarantine-{Guid.NewGuid():N}.docx");
        try
        {
            SampleDocumentFactory.Create(path);
            using var pipeline = new AuthorityExtractionPipeline(new PipelineOptions { DisableLlm = true });
            var outline = await pipeline.RunAsync(path, new HashSet<int> { 0 });
            Assert.Equal(3, outline.Headings.Count);
            Assert.DoesNotContain(outline.Headings, heading => heading.Index == 0);
        }
        finally
        {
            LegacyDocConverter.TryDelete(path);
        }
    }
}
