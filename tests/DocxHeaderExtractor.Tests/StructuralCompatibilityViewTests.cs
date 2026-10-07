using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Semantics.Validation;

namespace DocxHeaderExtractor.Tests;

public sealed class StructuralCompatibilityViewTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Compatibility_views_are_serialized_but_only_reflect_admitted_graph(bool web)
    {
        var graph = ValidatedStructureFactory.Create([Element("root"), Element("child")],
            [new StructuralRelationProposal("root", "child", StructuralRelationType.ParentChild)]);
        Assert.Same(graph.Elements, graph.OutlineElements);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<ValidatedStructuralElement>)graph.OutlineElements).Clear());
        var options = web ? new JsonSerializerOptions(JsonSerializerDefaults.Web) : new();
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(graph, options));
        var elements = json.RootElement.GetProperty("elements");
        var outline = json.RootElement.GetProperty(web ? "outlineElements" : "OutlineElements");
        Assert.Equal(elements.GetRawText(), outline.GetRawText());
        Assert.Null(elements[0].GetProperty("parentId").GetString());
        Assert.Equal("root", elements[1].GetProperty("parentId").GetString());
        Assert.Equal("root", Assert.Single(graph.Relations).FromId);
        Assert.Equal("child", Assert.Single(graph.Relations).ToId);
    }

    [Fact]
    public void Deserialized_parent_view_cannot_create_or_override_relation_authority()
    {
        var input = Element("child") with { ParentId = "untrusted-parent" };
        var deserialized = JsonSerializer.Deserialize<ValidatedStructuralElement>(JsonSerializer.Serialize(input))!;
        Assert.Equal("untrusted-parent", deserialized.ParentId);
        var noRelations = ValidatedStructureFactory.Create([deserialized]);
        Assert.Empty(noRelations.Relations);
        Assert.Null(Assert.Single(noRelations.Elements).ParentId);
        var explicitRelations = ValidatedStructureFactory.Create([Element("root"), deserialized],
            [new StructuralRelationProposal("root", "child", StructuralRelationType.ParentChild)]);
        Assert.Equal("root", explicitRelations.Elements.Single(element => element.Id == "child").ParentId);
        Assert.Equal("untrusted-parent", deserialized.ParentId);
    }

    [Fact]
    public void Production_graph_projections_do_not_depend_on_compatibility_alias_or_parent_view()
    {
        foreach (var file in new[] { "HeadingOutlineProjection.cs", "CanonicalGroundingProjection.cs",
                     "StructuralSectionProjection.cs" })
        {
            var source = File.ReadAllText(TestRepository.Path(
                "src/DocxHeaderExtractor.DocumentProcessing/Projection/" + file));
            Assert.DoesNotContain(".OutlineElements", source, StringComparison.Ordinal);
            Assert.DoesNotContain("element.ParentId", source, StringComparison.Ordinal);
            Assert.Contains("structure.Elements", source, StringComparison.Ordinal);
        }
        var sections = File.ReadAllText(TestRepository.Path(
            "src/DocxHeaderExtractor.DocumentProcessing/Projection/StructuralSectionProjection.cs"));
        Assert.Contains("structure.Relations", sections, StringComparison.Ordinal);
        Assert.Contains("StructuralRelationType.ParentChild", sections, StringComparison.Ordinal);
    }

    private static ValidatedStructuralElement Element(string id) => new()
    {
        Id = id, Type = StructuralElementType.Heading, Role = ProposedRole.HeadingTopic,
        Sources = [new SourceReference(id, 0, new StructuralSpan(0, 4))], Text = "Head",
        Validation = new StructuralValidation(true, true, true, true, 1, true, true, true, null),
        Decision = new StructuralDecision("model", "RequiresReview", "bound"),
    };
}
