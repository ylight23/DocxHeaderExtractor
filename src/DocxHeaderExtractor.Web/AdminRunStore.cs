using System.Collections.Concurrent;
using DocxHeaderExtractor.DocumentProcessing.Gather;

namespace DocxHeaderExtractor.Web;

/// <summary>
/// The last admin gather runs, kept in memory so the reader can apply decisions on the uncertain sections and write the summary again without
/// uploading the files again. Holds a copy of the target bytes, never a path; an entry expires after an hour.
/// </summary>
public sealed class AdminRunStore
{
    static readonly TimeSpan Ttl = TimeSpan.FromHours(1);
    const int MaxEntries = 20;

    readonly ConcurrentDictionary<Guid, Entry> entries = new();

    public sealed record Entry(byte[] Target, string TargetName, IReadOnlyList<GatherSection> Decided, IReadOnlyList<GatherUncertain> Uncertain,
        DateTimeOffset Created);

    public void Put(Guid runId, Entry entry)
    {
        Sweep();
        entries[runId] = entry;
        foreach (var old in entries.OrderByDescending(e => e.Value.Created).Skip(MaxEntries)) entries.TryRemove(old.Key, out _);
    }

    public Entry? Get(Guid runId)
    {
        Sweep();
        return entries.TryGetValue(runId, out var entry) ? entry : null;
    }

    void Sweep()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (id, entry) in entries)
            if (now - entry.Created > Ttl) entries.TryRemove(id, out _);
    }
}
