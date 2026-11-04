using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Output;
using DocxHeaderExtractor.Sources.Docx.Changes;

namespace DocxHeaderExtractor.DocumentProcessing.Gather;

/// <summary>One paragraph as the store keeps it: where it is and a hash of everything the mapping compares (no text).</summary>
public sealed record StoredParagraph(string Location, int Ordinal, string Signature);

/// <summary>
/// A stored outline: the headings the pipeline gave for one exact file under one pipeline configuration, and the paragraph signatures of that
/// file so a later version can be mapped onto it. <see cref="UnsupportedRegions"/> names the kinds of region the reader could not read in full.
/// </summary>
public sealed record StoredOutline(string Version, string SourceSha256, string ConfigKey, string Name, DateTimeOffset Created,
    IReadOnlyList<StoredParagraph> Paragraphs, IReadOnlyList<string> UnsupportedRegions, IReadOnlyList<HeadingRecord> Headings);

/// <summary>
/// Outlines already extracted, on disk. An entry is found only by the file's SHA-256 and the configuration key, so a changed prompt, model or
/// build (whatever the caller puts in the key) is a miss, never a stale answer. The latest entry of a file name is also remembered, so a new
/// version of that file can be mapped onto it (<see cref="OutlineMap"/>). Entries are written to a temporary file and moved into place;
/// an existing entry is never rewritten. A store that cannot be read or written only costs a model run.
/// </summary>
public sealed class OutlineStore(string root)
{
    public const string Version = "OUTLINE_STORE_V1";
    public const string DefaultRoot = ".dhx-evidence/outline";

    static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>The configuration key: a hash of the store version and every part the caller names (prompt hashes, model, build identity).</summary>
    public static string ConfigKey(params string[] parts) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Version + "\n" + string.Join("\n", parts))))[..16];

    /// <summary>The signature the mapping compares: text, container, style, numbering, alignment, run formatting and cell facts.</summary>
    public static string Signature(ParagraphFingerprint p)
    {
        var text = new StringBuilder()
            .Append(p.RawText).Append('\u001f').Append(p.Container).Append('\u001f').Append(p.StyleId).Append('\u001f')
            .Append(p.NumId).Append('\u001f').Append(p.Ilvl).Append('\u001f').Append(p.Alignment).Append('\u001f')
            .Append(p.Cell is { } c ? $"{c.GridSpan}|{c.VerticalMerge}|{c.HorizontalMerge}|{c.TableDepth}" : "");
        foreach (var r in p.Runs)
            text.Append('\u001e').Append($"{r.Start}|{r.End}|{r.Bold}|{r.Italic}|{r.Underline}|{r.FontAscii}|{r.FontSize}|{r.Color}");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    public static IReadOnlyList<StoredParagraph> Paragraphs(DocxVersion version) =>
        version.Paragraphs.Select(p => new StoredParagraph(p.Location, p.Ordinal, Signature(p))).ToArray();

    string EntryPath(string sha, string key) => Path.Combine(root, sha[..Math.Min(16, sha.Length)], key + ".json");
    string NamePath(string name, string key) => Path.Combine(root, "names",
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name.ToLowerInvariant())))[..16] + "-" + key + ".txt");

    /// <summary>The outline stored for this exact file and configuration, or null.</summary>
    public StoredOutline? Find(string sourceSha256, string configKey)
    {
        var path = EntryPath(sourceSha256, configKey);
        if (!File.Exists(path)) return null;
        try
        {
            var entry = JsonSerializer.Deserialize<StoredOutline>(File.ReadAllText(path), Json);
            return entry is { Version: Version } && entry.SourceSha256 == sourceSha256 && entry.ConfigKey == configKey ? entry : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or NotSupportedException) { return null; }
    }

    /// <summary>The latest outline stored under this file name and configuration (any content), or null.</summary>
    public StoredOutline? Latest(string name, string configKey)
    {
        try
        {
            var path = NamePath(name, configKey);
            return File.Exists(path) ? Find(File.ReadAllText(path).Trim(), configKey) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public void Store(StoredOutline outline)
    {
        try
        {
            var path = EntryPath(outline.SourceSha256, outline.ConfigKey);
            if (!File.Exists(path)) WriteNew(path, JsonSerializer.Serialize(outline, Json));
            var name = NamePath(outline.Name, outline.ConfigKey);
            Directory.CreateDirectory(Path.GetDirectoryName(name)!);
            File.WriteAllText(name, outline.SourceSha256);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    static void WriteNew(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, text);
        try { File.Move(temp, path, overwrite: false); }
        catch (IOException) { File.Delete(temp); }   // another run stored the same file first
    }
}
