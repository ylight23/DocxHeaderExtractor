using A99.LocalTesseractSpike;
using Xunit;

namespace A99.LocalTesseractSpike.Tests;

public sealed class LocalVisualTextContractsTests
{
    [Fact]
    public void One_region_is_an_exact_match()
    {
        var region = Region(1, 2, 101, 42, "Alpha heading", "crop-a");

        var matches = LocalVisualTextMatching.ExactMatches([region], "Alpha heading");

        Assert.Single(matches);
    }

    [Fact]
    public void Duplicate_text_in_two_regions_is_not_unique()
    {
        var regions = new[]
        {
            Region(1, 2, 101, 42, "Alpha", "crop-a"),
            Region(1, 60, 101, 100, "Alpha", "crop-b"),
        };

        Assert.Equal(2, LocalVisualTextMatching.ExactMatches(regions, "Alpha").Count);
    }

    [Fact]
    public void Same_image_and_bbox_reproduce_the_same_region_id()
    {
        var first = LocalVisualTextMatching.CreateStableRegionId("image-a", new(1, 2, 101, 42), "crop-a");
        var second = LocalVisualTextMatching.CreateStableRegionId("image-a", new(1, 2, 101, 42), "crop-a");

        Assert.Equal(first, second);
    }

    [Fact]
    public void One_pixel_bbox_change_changes_region_id()
    {
        var first = LocalVisualTextMatching.CreateStableRegionId("image-a", new(1, 2, 101, 42), "crop-a");
        var second = LocalVisualTextMatching.CreateStableRegionId("image-a", new(1, 2, 102, 42), "crop-a");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Changed_source_image_parent_changes_region_id()
    {
        var first = LocalVisualTextMatching.CreateStableRegionId("image-a", new(1, 2, 101, 42), "crop-a");
        var second = LocalVisualTextMatching.CreateStableRegionId("image-b", new(1, 2, 101, 42), "crop-a");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Relation_is_not_an_input_to_localization_identity()
    {
        var image = new SourceImageAsset("image-a", [1, 2, 3], "image-sha", 10, 10);
        var locator = new FixedLocator([Region(1, 2, 9, 9, "Alpha", "crop-a")]);

        var distinct = locator.Locate(image);
        var repeat = locator.Locate(image);

        Assert.Equal(distinct, repeat);
    }

    [Fact]
    public void Whitespace_and_allowed_apostrophe_normalization_is_narrow()
    {
        var region = Region(1, 2, 101, 42, "Alpha\n  O’Reilly", "crop-a");

        Assert.Single(LocalVisualTextMatching.ExactMatches([region], "Alpha O'Reilly"));
    }

    [Fact]
    public void Punctuation_difference_is_rejected()
    {
        var region = Region(1, 2, 101, 42, "Alpha, Beta", "crop-a");

        Assert.Empty(LocalVisualTextMatching.ExactMatches([region], "Alpha Beta"));
    }

    [Fact]
    public void Missing_token_is_rejected()
    {
        var region = Region(1, 2, 101, 42, "Alpha Beta", "crop-a");

        Assert.Empty(LocalVisualTextMatching.ExactMatches([region], "Alpha"));
    }

    private static VisualTextRegion Region(int left, int top, int right, int bottom, string text, string cropSha) =>
        new(0, new(left, top, right, bottom), text, 0.9f, "fixture", "1", cropSha);

    private sealed class FixedLocator(IReadOnlyList<VisualTextRegion> regions) : ILocalVisualTextLocator
    {
        public IReadOnlyList<VisualTextRegion> Locate(SourceImageAsset image)
        {
            _ = image;
            return regions;
        }
    }
}
