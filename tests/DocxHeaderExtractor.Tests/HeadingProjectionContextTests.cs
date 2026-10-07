using System.Security.Cryptography;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Materialization;
using DocxHeaderExtractor.DocumentProcessing.Semantics.HeadingAuthority;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Projection;
using DocxHeaderExtractor.DocumentProcessing.Semantics.Canonical;

namespace DocxHeaderExtractor.Tests;

public sealed class HeadingProjectionContextTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Outline_and_grounding_bytes_match_d1f6641_before_metadata_extraction(bool web)
    {
        var graph = ValidatedStructureFactory.Create([Element("h1", "s1", 4)]);
        var context = new HeadingProjectionContext(
            new Dictionary<string, HeadingProjectionMetadata> { ["h1"] = Metadata() },
            new Dictionary<HeadingProjectionSourceKey, string> { [new("h1", "s1")] = "stable-source" });
        var options = web ? new JsonSerializerOptions(JsonSerializerDefaults.Web) : new();
        var before = JsonSerializer.Serialize(graph, options);
        // Captured provider-free from d1f6641 before S2; no artifact rebaseline.
        Assert.Equal("8432cf34f1c32282ca148a2028c6d016ce78514705632ec829509efb180b7d86",
            Hash(HeadingOutlineProjection.Project(graph, projectionContext: context), options));
        Assert.Equal("7ea2c6ae1779fb3205ac6eda1cae5f71596754a5330971670000d7dbc7da12e9",
            Hash(CanonicalGroundingProjection.Project(graph, context), options));
        Assert.Equal(before, JsonSerializer.Serialize(graph, options));
    }

    [Fact]
    public void Context_is_snapshotted_and_scoped_by_element_and_source_identity()
    {
        var metadata = new Dictionary<string, HeadingProjectionMetadata> { ["a"] = Metadata() };
        var ids = new Dictionary<HeadingProjectionSourceKey, string>
        {
            [new("a", "shared")] = "stable-a", [new("b", "shared")] = "stable-b",
        };
        var context = new HeadingProjectionContext(metadata, ids);
        metadata.Clear();
        ids.Clear();
        Assert.NotNull(context.ForElement("a"));
        Assert.Null(context.ForElement("b"));
        Assert.Equal("stable-a", context.StableIdFor("a", "shared"));
        Assert.Equal("stable-b", context.StableIdFor("b", "shared"));
        Assert.Null(context.StableIdFor("a", "other"));
        var retained = context.Retain(new HashSet<string> { "b" });
        Assert.Null(retained.ForElement("a"));
        Assert.Null(retained.StableIdFor("a", "shared"));
        Assert.Equal("stable-b", retained.StableIdFor("b", "shared"));
    }

    [Fact]
    public void Quarantine_keeps_context_for_surviving_elements_without_identity_leakage()
    {
        var graph = ValidatedStructureFactory.Create([Element("a", "s1", 0), Element("b", "s2", 1)]);
        var context = new HeadingProjectionContext(
            new Dictionary<string, HeadingProjectionMetadata> { ["a"] = Metadata(), ["b"] = Metadata() },
            new Dictionary<HeadingProjectionSourceKey, string> { [new("a", "s1")] = "stable-a", [new("b", "s2")] = "stable-b" });
        var authority = new StructuralAuthorityResult(graph, null, "test") { ProjectionContext = context };
        var result = DocxExtractionPipeline.ApplyStructuralQuarantine(authority, new HashSet<int> { 0 });
        Assert.Equal("b", Assert.Single(result.Structure.Elements).Id);
        Assert.Null(result.ProjectionContext.ForElement("a"));
        Assert.NotNull(result.ProjectionContext.ForElement("b"));
        Assert.Equal("stable-b", result.ProjectionContext.StableIdFor("b", "s2"));
        Assert.DoesNotContain("ProjectionContext", JsonSerializer.Serialize(result));
    }

    [Fact]
    public void Multipart_materialization_keeps_raw_parts_and_separate_primary_projection_identity()
    {
        var heading = new ValidatedHeading("s1", new TextOffsetSpan(0, 5), "REGION_STRUCTURE", "document_body", "bound")
        {
            Parts = [new CanonicalSemanticBoundPart("O1", "s1", 0, "Alpha", 0, 5),
                new CanonicalSemanticBoundPart("O2", "s2", 1, "Bravo", 0, 5)],
        };
        var result = HeadingStructureMaterializer.Materialize([heading],
            new Dictionary<string, ResolvedHeadingHierarchy> { ["s1"] = new("s1", 1, null, "model-root", "requires_review") },
            new Dictionary<string, StructureSourceOccurrence>
            {
                ["s1"] = new("s1", 0, "Alpha body", "title"), ["s2"] = new("s2", 1, "Bravo", "title"),
            }, "test", StructuralDecisionOrigin.Model);
        var element = Assert.Single(result.Structure.Elements);
        Assert.Equal(new[] { "s1", "s2" }, element.Sources.Select(source => source.SourceId));
        Assert.All(element.Sources, source => Assert.Equal("s1",
            result.ProjectionContext.StableIdFor(element.Id, source.SourceId)));
        var outline = Assert.Single(HeadingOutlineProjection.Project(result.Structure,
            projectionContext: result.ProjectionContext));
        Assert.Equal("Alpha Bravo", outline.Text);
        Assert.Equal("Alpha body", outline.OriginalText);
        Assert.Equal("s1", outline.StableId);
        var grounding = Assert.Single(CanonicalGroundingProjection.Project(result.Structure, result.ProjectionContext));
        Assert.Equal("Alpha body", grounding.ParagraphText);
        Assert.Equal("s1", grounding.StableId);
    }

    [Fact]
    public void Structural_contracts_do_not_own_projection_metadata_or_compatibility_ids()
    {
        Assert.Null(typeof(ValidatedStructuralElement).GetProperty("ProjectionMetadata"));
        Assert.Null(typeof(SourceReference).GetProperty("StableId"));
        Assert.Null(typeof(CanonicalGrounding).GetMethod("FromValidatedStructure"));
        var groundingSource = File.ReadAllText(TestRepository.Path(
            "src/DocxHeaderExtractor.DocumentProcessing/Semantics/Canonical/CanonicalGrounding.cs"));
        Assert.DoesNotContain("HeadingProjectionContext", groundingSource);
        Assert.DoesNotContain("DocumentProcessing.Projection", groundingSource);
        var root = TestRepository.Path("src/DocxHeaderExtractor.Core/Models");
        foreach (var file in new[] { "HeadingStructuralContracts.cs", "SourceSelectionContracts.cs", "ValidatedStructure.cs" })
        {
            var text = File.ReadAllText(Path.Combine(root, file));
            Assert.DoesNotContain("ProjectionMetadata", text);
            Assert.DoesNotContain("OutlineStableId", text);
            Assert.DoesNotContain("DocumentProcessing.Projection", text);
        }
    }

    private static string Hash<T>(T value, JsonSerializerOptions options) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, options)));

    private static ValidatedStructuralElement Element(string id, string source, int ordinal) => new()
    {
        Id = id, Type = StructuralElementType.Heading, Role = ProposedRole.HeadingTopic,
        Sources = [new SourceReference(source, ordinal, new StructuralSpan(2, 9))], Text = "Heading", Level = 2,
        Validation = new StructuralValidation(true, true, true, true, 1, true, true, true, null),
        Decision = new StructuralDecision("model", "RequiresReview", "source-bound"),
    };

    private static HeadingProjectionMetadata Metadata() => new()
    {
        OutlineSourceId = "outline-source", OutlineSourceOrdinal = 9, OutlineStableId = "outline-stable",
        OutlineHeadingSpan = new StructuralSpan(3, 10), OutlineText = "Display",
        OutlineLevelIsSet = true, OutlineLevel = null, HierarchyResolution = "unresolved",
        OriginalText = "xxHeading body", InlineBody = "body", InlineBodySpan = new StructuralSpan(10, 14),
        BoundarySource = "source-span", StyleId = "style-1",
    };
}
