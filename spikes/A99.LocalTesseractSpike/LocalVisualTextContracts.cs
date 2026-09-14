using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.Eval.Accuracy99;

namespace A99.LocalTesseractSpike;

public interface ILocalVisualTextLocator
{
    IReadOnlyList<VisualTextRegion> Locate(SourceImageAsset image);
}

public sealed record SourceImageAsset(
    string ParentImageOccurrenceId,
    byte[] ImageBytes,
    string ImageSha256,
    int Width,
    int Height);

public sealed record VisualPixelBoundingBox(int Left, int Top, int Right, int Bottom)
{
    public bool IsValid => Left >= 0 && Top >= 0 && Right > Left && Bottom > Top;
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public string Canonical => string.Join(",", [Left, Top, Right, Bottom]);
}

public sealed record VisualTextRegion(
    int RegionIndex,
    VisualPixelBoundingBox PixelBBox,
    string RawRecognizedText,
    float Confidence,
    string RecognitionEngine,
    string RecognitionVersion,
    string RegionImageSha256);

public static class LocalVisualTextMatching
{
    public static IReadOnlyList<VisualTextRegion> ExactMatches(
        IEnumerable<VisualTextRegion> regions, string expectedSurface)
    {
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSurface);
        var expected = Normalize(expectedSurface);
        return regions.Where(region => Normalize(region.RawRecognizedText) == expected).ToArray();
    }

    public static string Normalize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        value = value.Normalize(NormalizationForm.FormC)
            .Replace('\u2018', '\'').Replace('\u2019', '\'')
            .Replace('\u201B', '\'').Replace('\u2032', '\'');
        return Regex.Replace(value, "\\s+", " ").Trim();
    }

    public static string CreateStableRegionId(
        string parentImageOccurrenceId, VisualPixelBoundingBox bbox, string regionImageSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentImageOccurrenceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(regionImageSha256);
        ArgumentNullException.ThrowIfNull(bbox);
        if (!bbox.IsValid) throw new ArgumentException("Pixel bbox must be non-empty and non-negative.", nameof(bbox));
        var key = string.Join("|", [parentImageOccurrenceId, bbox.Canonical, regionImageSha256]);
        return "pdf-image-region:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
    }
}
