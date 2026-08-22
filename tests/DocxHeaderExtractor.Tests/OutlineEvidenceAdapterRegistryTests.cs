using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class OutlineEvidenceAdapterRegistryTests
{
    [Fact]
    public void Select_uses_policy_priority_not_adapter_evaluation_order()
    {
        var selected = OutlineEvidenceAdapterRegistry.Select([
            Result("auto:pdf-bold-label", 80),
            Result("auto:pdf-toc-dictionary", 20),
        ]);

        Assert.NotNull(selected);
        Assert.Equal("auto:pdf-toc-dictionary", selected.Route);
    }

    [Fact]
    public void Select_ignores_empty_adapter_even_when_it_has_higher_priority()
    {
        var selected = OutlineEvidenceAdapterRegistry.Select([
            new OutlineEvidenceAdapterResult("auto:pdf-bookmarks", 10, [], "no-bookmarks"),
            Result("auto:pdf-toc-dictionary", 20),
        ]);

        Assert.NotNull(selected);
        Assert.Equal("auto:pdf-toc-dictionary", selected.Route);
    }

    private static OutlineEvidenceAdapterResult Result(string route, int priority) => new(
        route,
        priority,
        [new HeadingRecord { Index = 1, Level = 1, Text = "Introduction", Source = HeadingSource.Structure }],
        "test");
}
