using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Sources.Cache;
using MessagePack;

namespace DocxHeaderExtractor.Sources.Pdf;

/// <summary>
/// The measured page geometry and the rendered page images of a PDF, kept between runs under the source cache. The key is the file's bytes and
/// path, this build of the code, the render options, and a fingerprint of every parser line (alias, page, box) the measurements read, so any change
/// to the source or the options is a miss. A miss, a hash mismatch or an unreadable entry is rebuilt, never an error. With no cache root the
/// build runs every time.
/// </summary>
internal static class PdfVisualCache
{
    const string GeometrySchema = "pdf-geometry-cache-v1";
    const string VisualSchema = "pdf-visual-cache-v1";

    /// <summary>A fingerprint of the parser lines in reading order: the alias, page and box JSON of each. Geometry and images read only these.</summary>
    public static string Fingerprint(IEnumerable<(string Alias, int? Page, string? Box)> lines)
    {
        var text = new StringBuilder();
        foreach (var (alias, page, box) in lines) text.Append(alias).Append('|').Append(page).Append('|').Append(box).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    public static IReadOnlyDictionary<int, JsonObject> Geometry(string pdfPath, string? cacheRoot, string fingerprint,
        Func<IReadOnlyDictionary<int, JsonObject>> build)
    {
        if (cacheRoot is null) return build();
        var (file, key) = Entry(pdfPath, cacheRoot, GeometrySchema, "raster", fingerprint, "geometry-");
        if (CacheStore.TryLoadBinary<GeometryCachePart>(file, GeometrySchema, key) is { } cached && TryParseGeometry(cached, out var pages))
            return pages;
        var built = build();
        CacheStore.StoreBinary(file, GeometrySchema, key,
            new GeometryCachePart(built.ToDictionary(kv => kv.Key, kv => kv.Value.ToJsonString())));
        return built;
    }

    public static (IReadOnlyDictionary<int, byte[]> Images, IReadOnlyDictionary<int, PdfPageImageCrop> Crops) Visual(string pdfPath, string? cacheRoot,
        string options, string fingerprint, Func<(IReadOnlyDictionary<int, byte[]> Images, IReadOnlyDictionary<int, PdfPageImageCrop> Crops)> build)
    {
        if (cacheRoot is null) return build();
        var (file, key) = Entry(pdfPath, cacheRoot, VisualSchema, options, fingerprint, "visual-");
        if (CacheStore.TryLoadBinary<VisualCachePart>(file, VisualSchema, key) is { } cached && TryParseCrops(cached, out var crops))
            return (cached.Images, crops);
        var built = build();
        CacheStore.StoreBinary(file, VisualSchema, key, new VisualCachePart(
            built.Images.ToDictionary(kv => kv.Key, kv => kv.Value),
            built.Crops.ToDictionary(kv => kv.Key, kv => JsonSerializer.Serialize(kv.Value))));
        return built;
    }

    static (string File, string Key) Entry(string pdfPath, string cacheRoot, string schema, string options, string fingerprint, string prefix)
    {
        var fileSha = CacheStore.FileSha(pdfPath);
        var key = CacheStore.Key(schema, fileSha, Path.GetFullPath(pdfPath), options + "|" + fingerprint);
        return (CacheStore.EntryPath(cacheRoot, fileSha, key, prefix, ".msgpack"), key);
    }

    static bool TryParseGeometry(GeometryCachePart cached, out IReadOnlyDictionary<int, JsonObject> pages)
    {
        pages = new Dictionary<int, JsonObject>();
        try
        {
            var parsed = new Dictionary<int, JsonObject>();
            foreach (var (page, text) in cached.Pages) parsed[page] = JsonNode.Parse(text)!.AsObject();
            pages = parsed;
            return true;
        }
        catch (JsonException) { return false; }
    }

    static bool TryParseCrops(VisualCachePart cached, out IReadOnlyDictionary<int, PdfPageImageCrop> crops)
    {
        crops = new Dictionary<int, PdfPageImageCrop>();
        try
        {
            var parsed = new Dictionary<int, PdfPageImageCrop>();
            foreach (var (page, text) in cached.Crops) parsed[page] = JsonSerializer.Deserialize<PdfPageImageCrop>(text)!;
            crops = parsed;
            return true;
        }
        catch (JsonException) { return false; }
    }
}

[MessagePackObject(AllowPrivate = true)]
internal sealed record GeometryCachePart([property: Key(0)] Dictionary<int, string> Pages);

[MessagePackObject(AllowPrivate = true)]
internal sealed record VisualCachePart([property: Key(0)] Dictionary<int, byte[]> Images, [property: Key(1)] Dictionary<int, string> Crops);
