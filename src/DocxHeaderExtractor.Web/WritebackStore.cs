using System.Collections.Concurrent;

namespace DocxHeaderExtractor.Web;

/// <summary>
/// Giữ tạm bản .docx đã gắn outline giữa lúc run kết thúc và lúc trình duyệt tải về.
/// <para>
/// Có giới hạn cứng về số mục, tổng dung lượng và tuổi, và mỗi mục chỉ lấy được đúng một lần.
/// Server này nhận tài liệu của người khác nên nội dung phải rời bộ nhớ càng sớm càng tốt; một
/// cache không giới hạn sẽ biến chính nó thành nơi tồn đọng tài liệu nhạy cảm.
/// </para>
/// </summary>
public sealed class WritebackStore
{
    private const int MaxEntries = 8;
    private const long MaxTotalBytes = 64L * 1024 * 1024;
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();

    public sealed record Entry(byte[] Content, string FileName, DateTimeOffset Created);

    public void Put(Guid runId, byte[] content, string fileName)
    {
        Prune();
        _entries[runId] = new Entry(content, fileName, DateTimeOffset.UtcNow);
        Prune();
    }

    /// <summary>Lấy và xoá. Tải một lần rồi thôi — không để link sống lâu hơn phiên làm việc.</summary>
    public Entry? Take(Guid runId)
    {
        Prune();
        return _entries.TryRemove(runId, out var entry) ? entry : null;
    }

    private void Prune()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (key, entry) in _entries)
            if (now - entry.Created > Ttl)
                _entries.TryRemove(key, out _);

        while (_entries.Count > MaxEntries ||
               _entries.Values.Sum(e => (long)e.Content.Length) > MaxTotalBytes)
        {
            var oldest = _entries.OrderBy(kv => kv.Value.Created).FirstOrDefault();
            if (oldest.Value is null) break;
            _entries.TryRemove(oldest.Key, out _);
        }
    }
}
