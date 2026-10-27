using DocxHeaderExtractor.Agents.Budget;
using DocxHeaderExtractor.HeadingAgent.Formats;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfVisualCacheTests
{
    const string Pdf = "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";

    // The production visual ceiling (PipelineOptions.PdfVisualSelectionMaxRequestBytes); a scope must fit it.
    static RequestPolicy Cap => new() { MaxRequestBytes = 786432 };

    static AgentSource Load(string root, int width = 1200, int quality = 58)
    {
        var source = AgentSource.Load(TestRepository.Path(Pdf), out var failure, policy: Cap, rasterGeometry: true, adaptiveVisualSelection: true,
            visualCropMargins: true, visualMaxWidth: width, visualQuality: quality, visualCacheRoot: root);
        Assert.Equal("OK", failure);
        return source!;
    }

    static string Fresh() => Path.Combine(Path.GetTempPath(), "dhx-visual-cache-" + Guid.NewGuid().ToString("N"));

    static void AssertSame(AgentSource expected, AgentSource actual)
    {
        Assert.Equal(expected.RasterGeometry.Keys.Order(), actual.RasterGeometry.Keys.Order());
        foreach (var page in expected.RasterGeometry.Keys)
            Assert.Equal(expected.RasterGeometry[page].ToJsonString(), actual.RasterGeometry[page].ToJsonString());
        Assert.Equal(expected.VisualImages.Keys.Order(), actual.VisualImages.Keys.Order());
        foreach (var page in expected.VisualImages.Keys) Assert.Equal(expected.VisualImages[page], actual.VisualImages[page]);
        Assert.Equal(expected.VisualCrops.Keys.Order(), actual.VisualCrops.Keys.Order());
        foreach (var page in expected.VisualCrops.Keys) Assert.Equal(expected.VisualCrops[page], actual.VisualCrops[page]);
    }

    [Fact]
    public void A_cached_load_returns_exactly_what_a_fresh_build_returns()
    {
        var root = Fresh();
        try
        {
            var fresh = AgentSource.Load(TestRepository.Path(Pdf), out _, policy: Cap, rasterGeometry: true, adaptiveVisualSelection: true, visualCropMargins: true)!;
            var built = Load(root);
            var cached = Load(root);
            AssertSame(fresh, built);
            AssertSame(fresh, cached);
            Assert.NotEmpty(Directory.EnumerateFiles(root, "geometry-*.msgpack", SearchOption.AllDirectories));
            Assert.NotEmpty(Directory.EnumerateFiles(root, "visual-*.msgpack", SearchOption.AllDirectories));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void A_damaged_entry_is_rebuilt_and_gives_the_same_result()
    {
        var root = Fresh();
        try
        {
            var expected = Load(root);
            foreach (var file in Directory.EnumerateFiles(root, "*.msgpack", SearchOption.AllDirectories))
                File.WriteAllBytes(file, [1, 2, 3, 4]);
            AssertSame(expected, Load(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void A_different_render_option_is_a_different_entry()
    {
        var root = Fresh();
        try
        {
            var standard = Load(root);
            var smaller = Load(root, width: 960, quality: 50);
            Assert.True(smaller.VisualImages[1].Length < standard.VisualImages[1].Length);
            AssertSame(smaller, Load(root, width: 960, quality: 50));
            Assert.Equal(2, Directory.EnumerateFiles(root, "visual-*.msgpack", SearchOption.AllDirectories).Count());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
