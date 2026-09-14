using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using UglyToad.PdfPig;

namespace DocxHeaderExtractor.Eval.Accuracy99;

/// <summary>
/// A source occurrence identifies a physical source object. It does not carry a
/// semantic relation and cannot be used to infer one.
/// </summary>
public enum SourceOccurrenceKind
{
    [JsonStringEnumMemberName("TEXT")] Text,
    [JsonStringEnumMemberName("IMAGE")] Image,
    [JsonStringEnumMemberName("IMAGE_REGION")] ImageRegion,
}

public abstract record SourceOccurrence(
    string DocumentId,
    string SourceSha256,
    SourceOccurrenceKind Kind,
    string StableOccurrenceId,
    bool SourceLineageVerified);

public sealed record PdfSourceBoundingBox(double Left, double Bottom, double Right, double Top)
{
    public bool IsValid =>
        double.IsFinite(Left) && double.IsFinite(Bottom) &&
        double.IsFinite(Right) && double.IsFinite(Top) &&
        Right > Left && Top > Bottom;

    public string Canonical => string.Join(",", [
        Left.ToString("R", CultureInfo.InvariantCulture),
        Bottom.ToString("R", CultureInfo.InvariantCulture),
        Right.ToString("R", CultureInfo.InvariantCulture),
        Top.ToString("R", CultureInfo.InvariantCulture)]);
}

public sealed record PdfImageTransform(
    double A, double B, double C, double D, double E, double F)
{
    public bool IsValid =>
        double.IsFinite(A) && double.IsFinite(B) && double.IsFinite(C) &&
        double.IsFinite(D) && double.IsFinite(E) && double.IsFinite(F);

    public string Canonical => string.Join(",", [
        A.ToString("R", CultureInfo.InvariantCulture),
        B.ToString("R", CultureInfo.InvariantCulture),
        C.ToString("R", CultureInfo.InvariantCulture),
        D.ToString("R", CultureInfo.InvariantCulture),
        E.ToString("R", CultureInfo.InvariantCulture),
        F.ToString("R", CultureInfo.InvariantCulture)]);
}

public sealed record PdfTextSourceOccurrence(
    string DocumentId,
    string SourceSha256,
    string NativeSourceId,
    string VerbatimText,
    int Page,
    PdfSourceBoundingBox? BoundingBox,
    string StableOccurrenceId,
    bool SourceLineageVerified)
    : SourceOccurrence(DocumentId, SourceSha256, SourceOccurrenceKind.Text, StableOccurrenceId, SourceLineageVerified);

public sealed record PdfImageSourceOccurrence(
    string DocumentId,
    string SourceSha256,
    int Page,
    string XObjectResourceName,
    int PlacementIndex,
    int ImageWidth,
    int ImageHeight,
    string ImageSha256,
    PdfSourceBoundingBox? PagePlacementBoundingBox,
    PdfImageTransform? TransformMatrix,
    string StableOccurrenceId,
    bool SourceLineageVerified)
    : SourceOccurrence(DocumentId, SourceSha256, SourceOccurrenceKind.Image, StableOccurrenceId, SourceLineageVerified);

public sealed record PdfImagePixelBoundingBox(int Left, int Top, int Right, int Bottom)
{
    public bool IsValid => Right > Left && Bottom > Top && Left >= 0 && Top >= 0;
    public string Canonical => string.Join(",", [Left, Top, Right, Bottom]);
}

public sealed record PdfImageNormalizedBoundingBox(double X, double Y, double Width, double Height)
{
    public bool IsValid =>
        double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Width) && double.IsFinite(Height) &&
        Width > 0 && Height > 0 && X >= 0 && Y >= 0 && X + Width <= 1 && Y + Height <= 1;
}

public sealed record PdfImageRegionSourceOccurrence(
    string DocumentId,
    string SourceSha256,
    string ParentImageOccurrenceId,
    PdfImagePixelBoundingBox PixelBoundingBox,
    PdfImageNormalizedBoundingBox NormalizedBoundingBox,
    string RegionImageSha256,
    string RegionLocatorMethod,
    string? TextEvidence,
    string StableOccurrenceId,
    bool SourceLineageVerified)
    : SourceOccurrence(DocumentId, SourceSha256, SourceOccurrenceKind.ImageRegion, StableOccurrenceId, SourceLineageVerified);

public static class PdfSourceOccurrenceIdentityFactory
{
    public static string CreateImageOccurrenceId(
        string sourceSha256,
        int page,
        string xObjectResourceName,
        int placementIndex,
        int imageWidth,
        int imageHeight,
        string imageSha256,
        PdfSourceBoundingBox? pagePlacementBoundingBox,
        PdfImageTransform? transformMatrix)
    {
        RequireNonEmpty(sourceSha256, nameof(sourceSha256));
        RequireNonEmpty(xObjectResourceName, nameof(xObjectResourceName));
        RequireNonEmpty(imageSha256, nameof(imageSha256));
        if (page < 1) throw new ArgumentOutOfRangeException(nameof(page));
        if (placementIndex < 0) throw new ArgumentOutOfRangeException(nameof(placementIndex));
        if (imageWidth <= 0) throw new ArgumentOutOfRangeException(nameof(imageWidth));
        if (imageHeight <= 0) throw new ArgumentOutOfRangeException(nameof(imageHeight));
        if (pagePlacementBoundingBox is { IsValid: false })
            throw new ArgumentException("A supplied PDF placement bounding box must be finite and non-empty.", nameof(pagePlacementBoundingBox));
        if (transformMatrix is { IsValid: false })
            throw new ArgumentException("A supplied PDF transform must be finite.", nameof(transformMatrix));

        var key = string.Join("|", [
            sourceSha256, page.ToString(CultureInfo.InvariantCulture), xObjectResourceName,
            placementIndex.ToString(CultureInfo.InvariantCulture),
            imageWidth.ToString(CultureInfo.InvariantCulture), imageHeight.ToString(CultureInfo.InvariantCulture),
            imageSha256, pagePlacementBoundingBox?.Canonical ?? "<bbox-unavailable>",
            transformMatrix?.Canonical ?? "<transform-unavailable>"]);
        return "pdf-image:" + Sha256(key);
    }

    public static string CreateImageRegionOccurrenceId(
        string parentImageOccurrenceId,
        PdfImagePixelBoundingBox pixelBoundingBox,
        string regionImageSha256)
    {
        RequireNonEmpty(parentImageOccurrenceId, nameof(parentImageOccurrenceId));
        RequireNonEmpty(regionImageSha256, nameof(regionImageSha256));
        ArgumentNullException.ThrowIfNull(pixelBoundingBox);
        if (!pixelBoundingBox.IsValid)
            throw new ArgumentException("A pixel region must be non-empty and non-negative.", nameof(pixelBoundingBox));
        return "pdf-image-region:" + Sha256(string.Join("|", [
            parentImageOccurrenceId, pixelBoundingBox.Canonical, regionImageSha256]));
    }

    private static void RequireNonEmpty(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Value is required.", parameterName);
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public sealed record PdfImageSourceReadResult(
    string DocumentId,
    string SourceSha256,
    IReadOnlyList<PdfImageSourceOccurrence> Occurrences);

/// <summary>
/// Reads image XObject identity from PdfPig's source-backed image and operation
/// objects. It deliberately does not OCR or assign a region inside an image.
/// </summary>
public static class PdfImageSourceOccurrenceReader
{
    public static PdfImageSourceReadResult Read(string pdfPath, string documentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        if (!File.Exists(pdfPath)) throw new FileNotFoundException("PDF source not found.", pdfPath);

        var sourceSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pdfPath))).ToLowerInvariant();
        using var document = PdfDocument.Open(pdfPath);
        var occurrences = new List<PdfImageSourceOccurrence>();

        foreach (var page in document.GetPages())
        {
            var operations = page.Operations.ToArray();
            var imageOperations = operations
                .Select((operation, index) => (operation, index))
                .Where(item => item.operation.GetType().Name == "InvokeNamedXObject")
                .ToArray();
            var images = page.GetImages().ToArray();
            if (images.Length == 0) continue;
            if (imageOperations.Length < images.Length)
                throw new InvalidDataException($"Could not map every page image to a named XObject on page {page.Number}.");

            for (var imageIndex = 0; imageIndex < images.Length; imageIndex++)
            {
                var image = images[imageIndex];
                var imageOperation = imageOperations[imageIndex];
                var resourceName = ReadStringProperty(imageOperation.operation, "Name")
                    ?? throw new InvalidDataException($"Image {imageIndex} on page {page.Number} has no source XObject name.");
                var raw = image.RawMemory.ToArray();
                var imageSha256 = Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant();
                var bounds = image.BoundingBox;
                var bbox = new PdfSourceBoundingBox(bounds.Left, bounds.Bottom, bounds.Right, bounds.Top);
                var transform = ReadTransform(operations.Take(imageOperation.index).Reverse()
                    .FirstOrDefault(operation => operation.GetType().Name == "ModifyCurrentTransformationMatrix"));
                var stableId = PdfSourceOccurrenceIdentityFactory.CreateImageOccurrenceId(
                    sourceSha256, page.Number, resourceName, imageIndex,
                    image.WidthInSamples, image.HeightInSamples, imageSha256, bbox, transform);
                occurrences.Add(new PdfImageSourceOccurrence(
                    documentId, sourceSha256, page.Number, resourceName, imageIndex,
                    image.WidthInSamples, image.HeightInSamples, imageSha256, bbox, transform,
                    stableId, true));
            }
        }

        return new(documentId, sourceSha256, occurrences);
    }

    private static string? ReadStringProperty(object value, string propertyName) =>
        value.GetType().GetProperty(propertyName)?.GetValue(value)?.ToString();

    private static PdfImageTransform? ReadTransform(object? operation)
    {
        if (operation is null) return null;
        var values = operation.GetType().GetProperty("Value")?.GetValue(operation) as double[];
        return values is { Length: 6 }
            ? new PdfImageTransform(values[0], values[1], values[2], values[3], values[4], values[5])
            : null;
    }
}

public enum PdfSourceOccurrenceBindingStatus
{
    [JsonStringEnumMemberName("EXACT_BOUND")] ExactBound,
    [JsonStringEnumMemberName("SOURCE_NOT_FOUND")] SourceNotFound,
    [JsonStringEnumMemberName("SOURCE_HASH_MISMATCH")] SourceHashMismatch,
    [JsonStringEnumMemberName("KIND_MISMATCH")] KindMismatch,
    [JsonStringEnumMemberName("AMBIGUOUS")] Ambiguous,
    [JsonStringEnumMemberName("REGION_UNBOUND")] RegionUnbound,
}

public sealed record PdfSourceOccurrenceQuery(
    string DocumentId,
    string ExpectedSourceSha256,
    SourceOccurrenceKind Kind,
    string StableOccurrenceId);

public sealed record PdfSourceOccurrenceBinding(
    PdfSourceOccurrenceBindingStatus Status,
    SourceOccurrence? Occurrence,
    IReadOnlyList<string> Evidence);

/// <summary>
/// Binds source identity only. No semantic relation, Gold label, or model output is
/// an input, so relation mutation cannot alter the result.
/// </summary>
public static class PdfSourceOccurrenceBinderV1
{
    public const string Version = "pdf-source-occurrence-binder-v1";

    public static PdfSourceOccurrenceBinding Bind(
        PdfSourceOccurrenceQuery query,
        string actualDocumentId,
        string actualSourceSha256,
        IReadOnlyList<SourceOccurrence> sourceOccurrences)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(sourceOccurrences);
        if (!string.Equals(query.DocumentId, actualDocumentId, StringComparison.Ordinal))
            return Failure(PdfSourceOccurrenceBindingStatus.SourceNotFound, "document identity does not match");
        if (!string.Equals(query.ExpectedSourceSha256, actualSourceSha256, StringComparison.OrdinalIgnoreCase))
            return Failure(PdfSourceOccurrenceBindingStatus.SourceHashMismatch, "source SHA256 does not match");

        var matches = sourceOccurrences
            .Where(item => item.SourceLineageVerified)
            .Where(item => string.Equals(item.DocumentId, actualDocumentId, StringComparison.Ordinal))
            .Where(item => string.Equals(item.SourceSha256, actualSourceSha256, StringComparison.OrdinalIgnoreCase))
            .Where(item => item.Kind == query.Kind)
            .Where(item => string.Equals(item.StableOccurrenceId, query.StableOccurrenceId, StringComparison.Ordinal))
            .ToArray();

        if (matches.Length == 0) return Failure(PdfSourceOccurrenceBindingStatus.SourceNotFound, "no verified source occurrence matched");
        if (matches.Length > 1) return Failure(PdfSourceOccurrenceBindingStatus.Ambiguous, "source identity matched more than one occurrence");
        return new(PdfSourceOccurrenceBindingStatus.ExactBound, matches[0], [
            "source document identity verified", "source SHA256 verified", "source occurrence kind verified",
            "stable source occurrence identity verified"]);
    }

    private static PdfSourceOccurrenceBinding Failure(PdfSourceOccurrenceBindingStatus status, string evidence) =>
        new(status, null, [evidence]);
}
