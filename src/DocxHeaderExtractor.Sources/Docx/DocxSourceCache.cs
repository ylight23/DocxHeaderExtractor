using DocxHeaderExtractor.Contracts.Models;
using DocxHeaderExtractor.Contracts.Docx;
using DocxHeaderExtractor.Sources.Docx.Reader;
using DocxHeaderExtractor.Sources.Cache;

namespace DocxHeaderExtractor.Sources.Docx;

/// <summary>
/// The DOCX source document (the read of the package), kept between runs as MessagePack. The key is the file's bytes and full path, the
/// extraction options the read depends on, and this build of the code. A read that throws is never stored, so an invalid package fails every time.
/// </summary>
internal static class DocxSourceCache
{
    public const string Schema = "docx-source-document-cache-v2";

    /// <summary>The source document of <paramref name="docxPath"/>: read from the package, or from <paramref name="cacheRoot"/> when a valid read is stored.
    /// A null <paramref name="cacheRoot"/> reads every time.</summary>
    public static SourceDocument ReadOrLoad(string docxPath, string? cacheRoot, ExtractionOptions? extraction)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(docxPath);
        if (cacheRoot is null) return new OpenXmlDocumentSource(extraction).Read(docxPath);

        var fileSha = CacheStore.FileSha(docxPath);
        var fullPath = Path.GetFullPath(docxPath);
        var key = CacheStore.Key(Schema, fileSha, fullPath, CacheStore.Fingerprint(extraction ?? new ExtractionOptions()));
        var file = CacheStore.EntryPath(cacheRoot, fileSha, key, "docx-", ".msgpack");

        if (CacheStore.TryLoadBinary<SourceDocument>(file, Schema, key) is { } cached) return cached;

        var read = new OpenXmlDocumentSource(extraction).Read(docxPath);
        CacheStore.StoreBinary(file, Schema, key, read);
        return read;
    }
}
