using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Output;
using DocxHeaderExtractor.Sources.Docx.Changes;
using DocxHeaderExtractor.DocumentProcessing.Audit;

namespace DocxHeaderExtractor.DocumentProcessing.Gather;

/// <summary>
/// One paragraph as the store keeps it: where it is and a hash of everything the mapping compares (no text). <see cref="Unverified"/> is true
/// when part of what the signature should cover could not be read (an unresolved numbering definition): two equal signatures are then not
/// evidence of an unchanged paragraph, and the mapping never pairs it.
/// </summary>
public sealed record StoredParagraph(string Location, int Ordinal, string Signature, bool Unverified = false)
{
    /// <summary>The same paragraph by signature, only when both sides were read in full.</summary>
    public static bool Same(StoredParagraph a, StoredParagraph b) => !a.Unverified && !b.Unverified && a.Signature == b.Signature;
}

/// <summary>
/// A stored outline version: the headings the pipeline gave for one exact file under one pipeline configuration, and the paragraph signatures of
/// that file so a later version can be mapped onto it. <see cref="UnsupportedRegions"/> names the kinds of region the reader could not read in full.
/// <see cref="Revision"/> numbers the versions of one file name under one configuration (1 = V1, 2 = V2, …); <see cref="PreviousSha256"/> is the
/// file the version before it was made from. Older versions are never overwritten.
/// </summary>
public sealed record StoredOutline(string Version, string SourceSha256, string ConfigKey, string Name, DateTimeOffset Created,
    IReadOnlyList<StoredParagraph> Paragraphs, IReadOnlyList<string> UnsupportedRegions, IReadOnlyList<HeadingRecord> Headings,
    int Revision = 1, string? PreviousSha256 = null, IReadOnlyList<StoredHeadingBinding>? HeadingBindings = null,
    PlacementAudit? Placement = null, string? EditId = null, string? EditedBy = null, DateTimeOffset? EditedAt = null,
    string? PreviousOutlineHash = null, bool? SelectionComplete = null, string? SignatureVersion = null);

/// <summary>Private store evidence for an outline heading. Public HeadingRecord JSON deliberately omits these fields.</summary>
public sealed record StoredHeadingBinding(int HeadingIndex, string? StableId, int PartCount, IReadOnlyList<HeadingSourcePart> Parts);

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
    static readonly object EditGate = new();
    public const string Version = "OUTLINE_STORE_V1";
    public const string DefaultRoot = ".dhx-evidence/outline";

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>The configuration key: a hash of the store version and every part the caller names (prompt hashes, model, build identity).</summary>
    public static string ConfigKey(params string[] parts) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Version + "\n" + string.Join("\n", parts))))[..16];

    /// <summary>
    /// The signature the mapping compares: text, container, style, numbering, alignment, run formatting and cell facts. Numbering is the definition
    /// the paragraph's level points to, not the numId, which Word renumbers on save, plus its list relation to the nearest earlier paragraph of
    /// the same definition (same list, another instance, another abstract list; see NumberingKeys.Relate), so splitting or merging lists changes
    /// the signature while a consistent renumbering does not. An unresolved definition keeps the raw numId and the paragraph is unverified.
    /// </summary>
    public static string Signature(ParagraphFingerprint p)
    {
        var text = new StringBuilder()
            .Append(p.RawText).Append('\u001f').Append(p.Container).Append('\u001f').Append(p.StyleId).Append('\u001f')
            .Append(p.NumberingResolved ? p.NumberingKey : "raw:" + p.NumId).Append('\u001f').Append(p.NumberingRelation).Append('\u001f').Append(p.Ilvl).Append('\u001f').Append(p.Alignment).Append('\u001f')
            .Append(p.Cell is { } c ? $"{c.GridSpan}|{c.VerticalMerge}|{c.HorizontalMerge}|{c.TableDepth}" : "");
        foreach (var r in p.Runs)
            text.Append('\u001e').Append($"{r.Start}|{r.End}|{r.Bold}|{r.Italic}|{r.Underline}|{r.FontAscii}|{r.FontSize}|{r.Color}");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>
    /// The version of <see cref="Signature"/>. 2: numbering by its resolved definition instead of the numId. Signatures of different versions are
    /// never compared; an outline stored without a version is version 1.
    /// </summary>
    public const string SignatureVersion = "2";

    public static IReadOnlyList<StoredParagraph> Paragraphs(DocxVersion version) =>
        version.Paragraphs.Select(p => new StoredParagraph(p.Location, p.Ordinal, Signature(p), !p.NumberingResolved)).ToArray();

    string EntryPath(string sha, string key) => Path.Combine(root, sha[..Math.Min(16, sha.Length)], key + ".json");
    string EditRoot(string sha, string key) => Path.Combine(root, "edits", sha[..Math.Min(16, sha.Length)] + "-" + key);
    string HistoryPath(string name, string key) => Path.Combine(root, "names",
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name.ToLowerInvariant())))[..16] + "-" + key + ".json");

    /// <summary>The outline stored for this exact file and configuration, or null.</summary>
    public StoredOutline? Find(string sourceSha256, string configKey)
    {
        var path = EntryPath(sourceSha256, configKey);
        var active = Path.Combine(EditRoot(sourceSha256, configKey), "active.txt");
        string? activeHash = null;
        try
        {
            if (File.Exists(active))
            {
                // A reviewer's edit is active: an unreadable pointer or a missing version is an error to report, never a reason to extract again and
                // silently drop the edit.
                var hash = File.ReadAllText(active).Trim();
                if (hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException("OUTLINE_EDIT_POINTER_INVALID");
                activeHash = hash;
                path = Path.Combine(EditRoot(sourceSha256, configKey), hash + ".json");
                if (!File.Exists(path)) throw new InvalidDataException("OUTLINE_EDIT_VERSION_MISSING");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new InvalidDataException("OUTLINE_EDIT_POINTER_UNREADABLE", e); }
        if (!File.Exists(path)) return null;
        try
        {
            var entry = JsonSerializer.Deserialize<StoredOutline>(File.ReadAllText(path));
            if (entry is not { Version: Version } || entry.SourceSha256 != sourceSha256 || entry.ConfigKey != configKey)
                return activeHash is null ? null : throw new InvalidDataException("OUTLINE_EDIT_VERSION_INVALID");
            var restored = RestoreBindings(entry);
            if (activeHash is null || OutlineApprovals.OutlineHash(restored) == activeHash) return restored;
            throw new InvalidDataException("OUTLINE_EDIT_VERSION_INVALID");
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            if (activeHash is not null) throw new InvalidDataException("OUTLINE_EDIT_VERSION_UNREADABLE", e);
            return null;
        }
    }

    /// <summary>Read a particular immutable edit or the original outline, never treating the current pointer as that version.</summary>
    public StoredOutline? FindVersion(string sha, string key, string hash)
    {
        if (hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c))) return null;
        var editedPath = Path.Combine(EditRoot(sha, key), hash + ".json");
        var path = File.Exists(editedPath) ? editedPath : EntryPath(sha, key);
        try
        {
            if (!File.Exists(path)) return null;
            var entry = JsonSerializer.Deserialize<StoredOutline>(File.ReadAllText(path));
            if (entry is not { Version: Version } || entry.SourceSha256 != sha || entry.ConfigKey != key) return null;
            var restored = RestoreBindings(entry);
            return OutlineApprovals.OutlineHash(restored) == hash ? restored : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or NotSupportedException) { return null; }
    }

    /// <summary>Save a human-edited outline as a separate immutable version and activate it only if the viewed version is still current.</summary>
    public (StoredOutline? Outline, string? Error) StoreEdited(string sha, string key, string expectedHash,
        IReadOnlyList<HeadingRecord> headings, string reviewer)
    {
        if (string.IsNullOrWhiteSpace(reviewer)) return (null, "REVIEWER_REQUIRED");
        lock (EditGate)
        {
            var current = Find(sha, key);
            if (current is null) return (null, "OUTLINE_NOT_FOUND");
            if (OutlineApprovals.OutlineHash(current) != expectedHash) return (null, "OUTLINE_STALE");
            var bindings = headings.Select((h, i) => (h, i)).Where(x => x.h.SourceParts is { Count: > 0 } &&
                x.h.ValidatedSourcePartCount == x.h.SourceParts.Count).Select(x => new StoredHeadingBinding(x.i, x.h.StableId,
                    x.h.ValidatedSourcePartCount, x.h.SourceParts!.ToArray())).ToArray();
            var edited = current with { Headings = headings, HeadingBindings = bindings, Revision = current.Revision + 1, Placement = null,
                EditId = Guid.NewGuid().ToString("N"), EditedBy = reviewer.Trim(), EditedAt = DateTimeOffset.UtcNow, PreviousOutlineHash = expectedHash };
            var hash = OutlineApprovals.OutlineHash(edited);
            if (hash == expectedHash) return (null, "NO_CHANGE");
            try
            {
                var dir = EditRoot(sha, key);
                Directory.CreateDirectory(dir);
                var versionPath = Path.Combine(dir, hash + ".json");
                if (!File.Exists(versionPath)) WriteNew(versionPath, JsonSerializer.Serialize(edited, Json));
                var temp = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".tmp");
                File.WriteAllText(temp, hash);
                File.Move(temp, Path.Combine(dir, "active.txt"), overwrite: true);
                return (Find(sha, key), null);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException) { return (null, "OUTLINE_WRITE_FAILED"); }
        }
    }

    /// <summary>Save a verified fresh extraction without overwriting a reviewer edit or the previous snapshot.</summary>
    public (StoredOutline? Outline, string? Error) StoreRefreshedExtraction(string sha, string key, string expectedHash,
        IReadOnlyList<HeadingRecord> headings, PlacementAudit? placement)
    {
        lock (EditGate)
        {
            var current = Find(sha, key);
            if (current is null) return (null, "OUTLINE_NOT_FOUND");
            if (OutlineApprovals.OutlineHash(current) != expectedHash || current.EditId is not null || current.SelectionComplete is not null)
                return (null, "OUTLINE_STALE");
            var bindings = headings.Select((h, i) => (h, i)).Where(x => x.h.SourceParts is { Count: > 0 } &&
                x.h.ValidatedSourcePartCount == x.h.SourceParts.Count).Select(x => new StoredHeadingBinding(x.i, x.h.StableId,
                    x.h.ValidatedSourcePartCount, x.h.SourceParts!.ToArray())).ToArray();
            var refreshed = current with { Headings = headings, HeadingBindings = bindings, Revision = current.Revision + 1,
                Placement = placement, SelectionComplete = true, EditId = Guid.NewGuid().ToString("N"), PreviousOutlineHash = expectedHash,
                Created = DateTimeOffset.UtcNow };
            var hash = OutlineApprovals.OutlineHash(refreshed);
            try
            {
                var dir = EditRoot(sha, key);
                Directory.CreateDirectory(dir);
                WriteNew(Path.Combine(dir, hash + ".json"), JsonSerializer.Serialize(refreshed, Json));
                var temp = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".tmp");
                File.WriteAllText(temp, hash);
                File.Move(temp, Path.Combine(dir, "active.txt"), overwrite: true);
                return (Find(sha, key), null);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException) { return (null, "OUTLINE_WRITE_FAILED"); }
        }
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
            var bindings = outline.Headings.Select((h, i) => (h, i)).Where(x => x.h.SourceParts is { Count: > 0 } &&
                x.h.ValidatedSourcePartCount == x.h.SourceParts.Count).Select(x => new StoredHeadingBinding(x.i, x.h.StableId,
                    x.h.ValidatedSourcePartCount, x.h.SourceParts!.ToArray())).ToArray();
            var stored = outline with { Revision = (previous?.Number ?? 0) + 1, PreviousSha256 = previous?.SourceSha256, HeadingBindings = bindings };
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

    static StoredOutline RestoreBindings(StoredOutline entry)
    {
        if (entry.HeadingBindings is null) return entry; // Legacy entries have no evidence; never invent it.
        var bindings = entry.HeadingBindings.GroupBy(b => b.HeadingIndex).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single());
        var headings = entry.Headings.Select((h, i) =>
        {
            if (!bindings.TryGetValue(i, out var b) || b.StableId != h.StableId || b.PartCount <= 0 || b.Parts is null || b.Parts.Count != b.PartCount ||
                b.Parts.Any(p => p is null || string.IsNullOrWhiteSpace(p.SourceId) || p.Span is null || p.OriginalText is null ||
                    p.Span.Start < 0 || p.Span.End <= p.Span.Start || p.Span.End > p.OriginalText.Length)) return h;
            return new HeadingRecord { StructuralElementId = h.StructuralElementId, SourceParts = b.Parts.ToArray(), ValidatedSourcePartCount = b.PartCount,
                ScanSources = h.ScanSources, Index = h.Index, StableId = h.StableId, SourceId = h.SourceId, Level = h.Level, Text = h.Text,
                OriginalText = h.OriginalText, HeadingSpan = h.HeadingSpan, InlineBody = h.InlineBody, InlineBodySpan = h.InlineBodySpan,
                BoundarySource = h.BoundarySource, HierarchyResolution = h.HierarchyResolution, StyleId = h.StyleId, Source = h.Source,
                DecisionStatus = h.DecisionStatus, ConfidenceBasis = h.ConfidenceBasis, Disputed = h.Disputed };
        }).ToArray();
        return entry with { Headings = headings };
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
