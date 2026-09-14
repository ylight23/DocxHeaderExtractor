using System.Text.Json;
using DocxHeaderExtractor.Eval.Accuracy99;

namespace DocxHeaderExtractor.Tests;

public sealed class A99PdfSourceOccurrenceContractsTests
{
    private const string PdfSha = "08af1ba4ddb0adab18a838cb63a5daddf70d2dbf8f731b7b78ba1049bfc3ba12";

    [Fact]
    public void Image_xobject_identity_comes_from_real_pdf_source_facts()
    {
        var result = PdfImageSourceOccurrenceReader.Read(PdfPath(), "DOC-0133");
        var image = Assert.Single(result.Occurrences.Where(item => item.Page == 66));

        Assert.Equal(PdfSha, result.SourceSha256);
        Assert.Equal("/Im1", image.XObjectResourceName);
        Assert.Equal(0, image.PlacementIndex);
        Assert.Equal(737, image.ImageWidth);
        Assert.Equal(928, image.ImageHeight);
        Assert.Equal("889817466fd9cab1cd9ea1275a359504f5e5fbb4e78ebaab97ded1c1d7a10659", image.ImageSha256);
        Assert.Equal(new PdfSourceBoundingBox(72, 130.71002, 540, 720), image.PagePlacementBoundingBox);
        Assert.Equal(new PdfImageTransform(468, 0, 0, 589.28998, 72, 130.71002), image.TransformMatrix);
        Assert.True(image.SourceLineageVerified);
        Assert.StartsWith("pdf-image:", image.StableOccurrenceId, StringComparison.Ordinal);
    }

    [Fact]
    public void Image_identity_is_stable_and_placement_sensitive()
    {
        var first = PdfImageSourceOccurrenceReader.Read(PdfPath(), "DOC-0133").Occurrences.Single(item => item.Page == 66);
        var second = PdfImageSourceOccurrenceReader.Read(PdfPath(), "DOC-0133").Occurrences.Single(item => item.Page == 66);
        Assert.Equal(first, second);

        var sameImageDifferentPlacement = PdfSourceOccurrenceIdentityFactory.CreateImageOccurrenceId(
            PdfSha, 66, "/Im1", 1, first.ImageWidth, first.ImageHeight, first.ImageSha256,
            first.PagePlacementBoundingBox, first.TransformMatrix);
        Assert.NotEqual(first.StableOccurrenceId, sameImageDifferentPlacement);
    }

    [Fact]
    public void Image_region_identity_is_reproducible_without_semantic_fields()
    {
        var imageId = "pdf-image:parent";
        var bbox = new PdfImagePixelBoundingBox(1, 2, 101, 202);
        var first = PdfSourceOccurrenceIdentityFactory.CreateImageRegionOccurrenceId(imageId, bbox, "region-sha");
        var second = PdfSourceOccurrenceIdentityFactory.CreateImageRegionOccurrenceId(
            imageId, new PdfImagePixelBoundingBox(1, 2, 101, 202), "region-sha");
        var changedPixel = PdfSourceOccurrenceIdentityFactory.CreateImageRegionOccurrenceId(
            imageId, new PdfImagePixelBoundingBox(1, 2, 102, 202), "region-sha");

        Assert.Equal(first, second);
        Assert.NotEqual(first, changedPixel);
        var invalid = new PdfImagePixelBoundingBox(-1, 2, 101, 202);
        Assert.False(invalid.IsValid);
        Assert.Throws<ArgumentException>(() => PdfSourceOccurrenceIdentityFactory.CreateImageRegionOccurrenceId(
            imageId, invalid, "region-sha"));
    }

    [Fact]
    public void Relation_mutation_cannot_change_source_binding()
    {
        var occurrence = PdfImageSourceOccurrenceReader.Read(PdfPath(), "DOC-0133").Occurrences.Single(item => item.Page == 66);
        var query = new PdfSourceOccurrenceQuery("DOC-0133", PdfSha, SourceOccurrenceKind.Image, occurrence.StableOccurrenceId);

        var distinct = BindForRelation("DISTINCT_SEMANTIC_NODE", query, occurrence);
        var repeat = BindForRelation("SAME_SEMANTIC_REPEAT", query, occurrence);

        Assert.Equal(PdfSourceOccurrenceBindingStatus.ExactBound, distinct.Status);
        Assert.Equal(PdfSourceOccurrenceBindingStatus.ExactBound, repeat.Status);
        Assert.Equal(JsonSerializer.Serialize(distinct.Occurrence), JsonSerializer.Serialize(repeat.Occurrence));
    }

    [Fact]
    public void V2_historical_binding_artifact_remains_unchanged()
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            File.ReadAllBytes(RepoPath("artifacts/identity-gold/semantic-identity-bindings.user-reviewed.v2.json"))))
            .ToLowerInvariant();
        Assert.Equal("122bf50fee967a2ef9a33aee3bc146d7df842d9e469126744fc0f68a017059b8", hash);
    }

    [Fact]
    public void V3_corrects_endpoint_attribution_without_claiming_inner_region()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(RepoPath(
            "artifacts/identity-gold/semantic-identity-bindings.user-reviewed.v3.json")));
        var root = document.RootElement;
        Assert.Equal("IMAGE_XOBJECT_BOUND_REGION_UNBOUND", root.GetProperty("status").GetString());
        Assert.Equal("BLOCKED_ON_IR018_VISUAL_REGION_LOCALIZATION", root.GetProperty("gate").GetString());

        var item = root.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("id").GetString() == "IR-018");
        Assert.Equal("B002349", item.GetProperty("outer").GetProperty("sourceOccurrenceId").GetString());
        Assert.Equal("PDF_IMAGE_REGION", item.GetProperty("inner").GetProperty("kind").GetString());
        Assert.Equal("/Im1", item.GetProperty("inner").GetProperty("parentImage").GetProperty("xObjectResourceName").GetString());
        Assert.Equal("REGION_UNBOUND", item.GetProperty("inner").GetProperty("regionStatus").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("inner").GetProperty("pixelBBox").ValueKind);
    }

    [Fact]
    public void V3_marks_prior_ir019_to_ir022_as_unchanged_carry_forward()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(RepoPath(
            "artifacts/identity-gold/semantic-identity-bindings.user-reviewed.v3.json")));
        foreach (var id in new[] { "IR-019", "IR-020", "IR-021", "IR-022" })
        {
            var item = document.RootElement.GetProperty("items").EnumerateArray()
                .Single(x => x.GetProperty("id").GetString() == id);
            Assert.True(item.GetProperty("machineEvaluable").GetBoolean());
            Assert.Equal("EXACT_BOUND", item.GetProperty("bindingStatus").GetString());
            Assert.StartsWith("artifacts/identity-gold/semantic-identity-bindings.user-reviewed.v2.json#",
                item.GetProperty("unchangedFromPreviousArtifact").GetString(), StringComparison.Ordinal);
        }
    }

    private static string PdfPath() => RepoPath("todo10_8/heading_corpus_100/03_tai_chinh_ke_toan/048_IBRD_Financial_Statements_March_2025.pdf");

    private static PdfSourceOccurrenceBinding BindForRelation(
        string semanticRelation, PdfSourceOccurrenceQuery query, PdfImageSourceOccurrence occurrence)
    {
        _ = semanticRelation;
        // Relation is deliberately not forwarded: source localization owns no semantic relation input.
        return PdfSourceOccurrenceBinderV1.Bind(query, "DOC-0133", PdfSha, [occurrence]);
    }

    private static string RepoPath(string relative) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../..", relative));
}
