using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Output;
using DocxHeaderExtractor.Sources.Docx.Changes;

namespace DocxHeaderExtractor.DocumentProcessing.Gather;

/// <summary>One paragraph as the store keeps it: where it is and a hash of everything the mapping compares (no text).</summary>
public sealed record StoredParagraph(string Location, int Ordinal, string Signature);

/// <summary>
/// A stored outline version: the headings the pipeline gave for one exact file under one pipeline configuration, and the paragraph signatures of
/// that file so a later version can be mapped onto it. <see cref="UnsupportedRegions"/> names the kinds of region the reader could not read in full.
/// <see cref="Revision"/> numbers the versions of one file name under one configuration (1 = V1, 2 = V2, …); <see cref="PreviousSha256"/> is the
/// file the version before it was made from. Older versions are never overwritten.
/// </summary>
public sealed record StoredOutline(string Version, string SourceSha256, string ConfigKey, string Name, DateTimeOffset Created,
    IReadOnlyList<StoredParagraph> Paragraphs, IReadOnlyList<string> UnsupportedRegions, IReadOnlyList<HeadingRecord> Headings,
    int Revision = 1, string? PreviousSha256 = null);

/// <summary>One line of a file name's history: the revision number, the file it was made from, and when it was stored.</summary>
public sealed record OutlineRevision(int Number, string SourceSha256, DateTimeOffset Created);

/// <summary>
/// Outlines already extracted, on disk. An entry is found only by the file's SHA-256 and the configuration key, so a changed prompt, model or
/// build (whatever the caller puts in the key) is a miss, never a stale answer. Each file name keeps its history in order (V1, V2, …): a new
/// file under the same name is a new revision, and no earlier revision is overwritten. Entries are written to a temporary file and moved into
/// place. A store that cannot be read or written only costs a model run.
/// </summary>
public sealed class OutlineStore(string root)
{
    public const string Version = "OUTLINE_STORE_V1";
    public const string DefaultRoot = ".dhx-evidence/outline";

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

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
    string HistoryPath(string name, string key) => Path.Combine(root, "names",
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name.ToLowerInvariant())))[..16] + "-" + key + ".json");

    /// <summary>The outline stored for this exact file and configuration, or null.</summary>
    public StoredOutline? Find(string sourceSha256, string configKey)
    {
        var path = EntryPath(sourceSha256, configKey);
        if (!File.Exists(path)) return null;
        try
        {
            var entry = JsonSerializer.Deserialize<StoredOutline>(File.ReadAllText(path));
            return entry is { Version: Version } && entry.SourceSha256 == sourceSha256 && entry.ConfigKey == configKey ? entry : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or NotSupportedException) { return null; }
    }

    /// <summary>Every revision stored under this file name and configuration, V1 first. Empty when there is none or it cannot be read.</summary>
    public IReadOnlyList<OutlineRevision> History(string name, string configKey)
    {
        try
        {
            var path = HistoryPath(name, configKey);
            return File.Exists(path) ? JsonSerializer.Deserialize<List<OutlineRevision>>(File.ReadAllText(path)) ?? [] : [];
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or NotSupportedException) { return []; }
    }

    /// <summary>The newest revision (the highest number) stored under this file name, or null.</summary>
    public StoredOutline? Latest(string name, string configKey) =>
        History(name, configKey).OrderBy(r => r.Number).LastOrDefault() is { } last ? Find(last.SourceSha256, configKey) : null;

    /// <summary>
    /// Stores the outline as the next revision of its file name, unless this exact file already has one. Returns the stored revision, or null when
    /// nothing was stored (the file already has a revision, or the store cannot be written).
    /// </summary>
    public StoredOutline? Store(StoredOutline outline)
    {
        try
        {
            var history = History(outline.Name, outline.ConfigKey).OrderBy(r => r.Number).ToList();
            if (history.Any(r => r.SourceSha256 == outline.SourceSha256)) return null;
            var previous = history.LastOrDefault();
            var stored = outline with { Revision = (previous?.Number ?? 0) + 1, PreviousSha256 = previous?.SourceSha256 };
            var path = EntryPath(outline.SourceSha256, outline.ConfigKey);
            if (!File.Exists(path)) WriteNew(path, JsonSerializer.Serialize(stored, Json));
            history.Add(new OutlineRevision(stored.Revision, outline.SourceSha256, outline.Created));
            var historyPath = HistoryPath(outline.Name, outline.ConfigKey);
            Directory.CreateDirectory(Path.GetDirectoryName(historyPath)!);
            File.WriteAllText(historyPath, JsonSerializer.Serialize(history, Json));
            return stored;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException) { return null; }
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
