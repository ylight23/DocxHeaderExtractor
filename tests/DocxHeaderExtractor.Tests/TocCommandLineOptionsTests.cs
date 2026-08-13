using DocxHeaderExtractor.Cli;

namespace DocxHeaderExtractor.Tests;

public sealed class TocCommandLineOptionsTests
{
    [Fact]
    public void Toc_partial_duoc_bat_bang_co_rieng()
    {
        var o = CommandLineOptions.Parse(["toc-keys", "corpus", "--toc-match-threshold", "0.4", "--toc-partial"]);

        Assert.Equal("toc-keys", o.Command);
        Assert.Equal(0.4, o.TocMatchThreshold);
        Assert.True(o.TocPartial);
    }
}
