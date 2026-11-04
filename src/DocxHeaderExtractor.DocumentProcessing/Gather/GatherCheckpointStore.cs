using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DocxHeaderExtractor.DocumentProcessing.Gather;

/// <summary>
/// Optional local checkpoint of completed replies. Identity must include model, provider and generation settings; only identical
/// prompts and identities are reusable. Files contain source-derived information and must be stored in a suitable private directory.
/// </summary>
public sealed class GatherCheckpointStore
{
    internal const string SplitMarker = "__GATHER_SPLIT_V1__";
    readonly string directory;
    readonly string identity;
    sealed record Entry(string Key, string Reply);

    public GatherCheckpointStore(string directory, string identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        this.directory = Path.GetFullPath(directory);
        this.identity = identity;
        Directory.CreateDirectory(this.directory);
    }

    string Key(string system, string user) => Convert.ToHexStringLower(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { "gather-checkpoint-v1", identity, system, user }))));

    internal string? Read(string system, string user)
    {
        var key = Key(system, user);
        var path = Path.Combine(directory, key + ".json");
        if (!File.Exists(path)) return null;
        Entry? entry;
        try { entry = JsonSerializer.Deserialize<Entry>(File.ReadAllText(path)); }
        catch (JsonException ex) { throw new InvalidDataException("GATHER_CHECKPOINT_INVALID", ex); }
        if (entry is null || entry.Key != key || entry.Reply is null) throw new InvalidDataException("GATHER_CHECKPOINT_INVALID");
        return entry.Reply;
    }

    internal void Write(string system, string user, string reply)
    {
        var key = Key(system, user);
        var path = Path.Combine(directory, key + ".json");
        var temp = Path.Combine(directory, key + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(new Entry(key, reply)));
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
