using System.Text.Json;

namespace DocxHeaderExtractor.HeadingAgent;

/// <summary>
/// Selection answers already paid for. A unit's answer is stored under the SHA-256 of its first request, which carries the model, the system
/// prompt, the tools, the instruction and the payload with its facts: when any of them changes the key changes and the unit is asked again.
/// Only COMPLETE outcomes are stored, so a unit that failed is always asked again. Reading one back returns the model's own earlier answer with
/// <see cref="AgentUnitOutcome.Reused"/> set; C# changes no decision. Entries are written to a temporary file and moved into place, and an
/// existing entry is never rewritten. Re-running a document after a change to grouping or placement then costs no selection call; a run that
/// must sample the model again (to measure its variance) passes no cache.
/// </summary>
internal sealed class SelectionCache(string root)
{
    public const string Version = "SELECTION_CACHE_V1";

    sealed record Entry(string Version, string SourceSha256, string FirstRequestSha256, AgentUnitOutcome Outcome);

    string PathOf(string sourceSha256, string firstRequestSha256) =>
        Path.Combine(root, sourceSha256[..Math.Min(16, sourceSha256.Length)], firstRequestSha256 + ".json");

    /// <summary>The stored COMPLETE outcome of the unit whose first request has this hash, or null (none, unreadable, or not this request).</summary>
    public AgentUnitOutcome? TryLoad(string sourceSha256, string firstRequestSha256)
    {
        var path = PathOf(sourceSha256, firstRequestSha256);
        if (!File.Exists(path)) return null;
        try
        {
            var entry = JsonSerializer.Deserialize<Entry>(File.ReadAllText(path));
            if (entry is null || entry.Version != Version || entry.SourceSha256 != sourceSha256 || entry.FirstRequestSha256 != firstRequestSha256
                || entry.Outcome is not { Status: "COMPLETE" } outcome || outcome.RequestSha256s.Count == 0 || outcome.RequestSha256s[0] != firstRequestSha256)
                return null;
            return outcome with { Reused = true };
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or NotSupportedException) { return null; }
    }

    /// <summary>Stores a COMPLETE outcome under its own first request; anything else, or an entry already there, is left alone.</summary>
    public void Store(string sourceSha256, AgentUnitOutcome outcome)
    {
        if (outcome is not { Status: "COMPLETE", Reused: false } || outcome.RequestSha256s.Count == 0) return;
        var path = PathOf(sourceSha256, outcome.RequestSha256s[0]);
        if (File.Exists(path)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new Entry(Version, sourceSha256, outcome.RequestSha256s[0], outcome)));
            try { File.Move(temp, path, overwrite: false); }
            catch (IOException) { File.Delete(temp); }   // another run stored the same unit first
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // a cache that cannot be written only costs a call next time
    }
}
