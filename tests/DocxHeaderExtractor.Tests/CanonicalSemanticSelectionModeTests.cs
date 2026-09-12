using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Tests;

public sealed class CanonicalSemanticSelectionModeTests
{
    [Fact]
    public void WholeAlias_UsesParserOwnedTextAndSpan()
    {
        var catalog = new DocumentSourceCatalog([
            new DocumentSourceUnit("S1", 0, "SOURCE HEADING", new SourceAnchor { SourceType = "DOCX_TEXT" }, new StructuralSpan(0, 14)),
        ]);
        var alias = SemanticSourceAliasCatalog.FromCatalog(catalog);
        var proposal = new CanonicalSemanticProposal("S0001", true, "model echo is ignored", SemanticRole: "SECTION", SelectionMode: CanonicalSemanticSelectionMode.WholeAlias);

        var bound = CanonicalSemanticExactBinder.Bind([proposal], alias, out var observations);

        Assert.Single(bound);
        Assert.Equal("SOURCE HEADING", bound[0].Text);
        Assert.Equal(0, bound[0].Start);
        Assert.Equal(14, bound[0].End);
        Assert.Equal(CanonicalSemanticBindingStatus.Bound, observations[0].Status);
    }

    [Fact]
    public void WholeAlias_DuplicatePhysicalAliasIsRejected()
    {
        var catalog = new DocumentSourceCatalog([
            new DocumentSourceUnit("S1", 0, "SOURCE HEADING", new SourceAnchor { SourceType = "DOCX_TEXT" }, new StructuralSpan(0, 14)),
        ]);
        var alias = SemanticSourceAliasCatalog.FromCatalog(catalog);
        var proposals = Enumerable.Range(0, 2).Select(_ => new CanonicalSemanticProposal("S0001", true, null, SelectionMode: CanonicalSemanticSelectionMode.WholeAlias)).ToArray();

        var bound = CanonicalSemanticExactBinder.Bind(proposals, alias, out var observations);

        Assert.Single(bound);
        Assert.Equal(CanonicalSemanticBindingStatus.DuplicateBinding, observations[1].Status);
    }
}
