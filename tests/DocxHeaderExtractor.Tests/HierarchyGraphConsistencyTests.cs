using DocxHeaderExtractor.Contracts.Models;
using DocxHeaderExtractor.Contracts.Models.Inference;
using DocxHeaderExtractor.DocumentProcessing.Materialization;

namespace DocxHeaderExtractor.Tests;

public sealed class HierarchyGraphConsistencyTests
{
    [Fact]
    public void Resolver_derives_depth_from_parent_edges_and_rejects_missing_or_out_of_tree_parents()
    {
        var root = Heading("A", 0, "ROOT");
        var child = Heading("B", 1, "A");
        var outside = Heading("N", 2, "NONE");
        var invalidChild = Heading("C", 3, "N");
        var futureChild = Heading("D", 4, "E");
        var futureParent = Heading("E", 5, "ROOT");

        var result = HeadingHierarchyResolver.DeriveHierarchyFromModelRelations(
            [root, child, outside, invalidChild, futureChild, futureParent]).ToDictionary(item => item.SourceId);

        Assert.Equal(1, result["id:A"].Level);
        Assert.Equal("id:A", result["id:B"].ParentSourceId);
        Assert.Equal(2, result["id:B"].Level);
        Assert.Equal(HeadingHierarchyResolver.OutOfHierarchy, result["id:N"].Resolution);
        Assert.Equal(HeadingHierarchyResolver.Unresolved, result["id:C"].Resolution);
        Assert.Equal(HeadingHierarchyResolver.Unresolved, result["id:D"].Resolution);
        Assert.Equal(HeadingHierarchyResolver.ResolvedRoot, result["id:E"].Resolution);
    }

    static CanonicalSemanticBoundHeading Heading(string alias, int ordinal, string parent) =>
        new(alias, "id:" + alias, ordinal, "heading " + alias, "ESTABLISHES_STRUCTURE", "heading", "document_body",
            ["parent-node:" + parent], 0, 10);
}