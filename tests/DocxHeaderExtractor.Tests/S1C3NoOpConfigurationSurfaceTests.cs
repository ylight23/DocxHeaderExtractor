using DocxHeaderExtractor.Cli;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class S1C3NoOpConfigurationSurfaceTests
{
    [Theory]
    [InlineData("--llm-boundary-cut-fallback")]
    [InlineData("--deterministic-hierarchy")]
    [InlineData("--no-deterministic-hierarchy")]
    [InlineData("--no-global-hierarchy")]
    [InlineData("--no-audit")]
    [InlineData("--no-structural-recovery")]
    public void Removed_no_op_flags_are_rejected(string flag)
    {
        Assert.Throws<ArgumentException>(() => CommandLineOptions.Parse(["input.docx", flag]));
    }

    [Fact]
    public void Pipeline_options_do_not_expose_removed_no_op_properties()
    {
        var propertyNames = typeof(PipelineOptions).GetProperties()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("LlmBoundaryCutFallback", propertyNames);
        Assert.DoesNotContain("AuditNumbering", propertyNames);
        Assert.DoesNotContain("RecoverNumberedSiblings", propertyNames);
        Assert.DoesNotContain("GlobalHierarchy", propertyNames);
        Assert.DoesNotContain("DeterministicHierarchy", propertyNames);
    }
}
