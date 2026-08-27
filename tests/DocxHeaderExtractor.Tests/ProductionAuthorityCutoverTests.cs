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
}
