using DocxHeaderExtractor.DocumentProcessing.Review;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DocxHeaderExtractor.Infrastructure.Learning;

public sealed record VerifiedCorrection(
    string Id,
    string SourceFile,
    string StableId,
    string Text,
    int? PredictedLevel,
    int CorrectedLevel,
    DateTimeOffset CreatedUtc);

/// <summary>
/// Bộ nhớ correction cục bộ, append-only. Chỉ lưu thay đổi thật sự của người dùng; không coi dự
/// đoán được chấp nhận hàng loạt là ground truth, và không có gì trong pipeline đọc ngược lại nó.
/// </summary>
public sealed class CorrectionMemory
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);

    public CorrectionMemory(string path)
    {
        _path = Path.GetFullPath(path);
        Load();
    }

    public int Count => _ids.Count;

    public static string DefaultPath() =>
        Environment.GetEnvironmentVariable("DHX_CORRECTION_MEMORY")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DocxHeaderExtractor", "verified-corrections.jsonl");

    public async Task<int> SaveChangedAsync(ReviewBundle bundle, CancellationToken ct = default)
    {
        var candidates = bundle.Rows
            .Where(r => r.CorrectedLevel is { } corrected && corrected != r.PredictedLevel)
            .Where(r => !string.IsNullOrWhiteSpace(r.Text) && r.Text.Length <= 8_000)
            .Select(r => Create(bundle.SourceFile, r, r.CorrectedLevel!.Value))
            .ToList();
        if (candidates.Count == 0) return 0;

        await _gate.WaitAsync(ct);
        try
        {
            var fresh = candidates.Where(x => _ids.Add(x.Id)).ToList();
            if (fresh.Count == 0) return 0;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read,
                16 * 1024, FileOptions.Asynchronous);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            foreach (var item in fresh)
                await writer.WriteLineAsync(JsonSerializer.Serialize(item, JsonOptions).AsMemory(), ct);
            await writer.FlushAsync(ct);
            return fresh.Count;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;
        foreach (var line in File.ReadLines(_path))
        {
            try
            {
                var item = JsonSerializer.Deserialize<VerifiedCorrection>(line, JsonOptions);
                if (item is not null) _ids.Add(item.Id);
            }
            catch (JsonException) { /* Bỏ qua một dòng hỏng, giữ các correction còn lại. */ }
        }
    }

    private static VerifiedCorrection Create(string sourceFile, ReviewRow row, int correctedLevel)
    {
        var canonical = $"{Normalize(row.Text)}\n{correctedLevel}";
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant()[..24];
        return new VerifiedCorrection(id, Path.GetFileName(sourceFile), row.StableId,
            row.Text, row.PredictedLevel, correctedLevel, DateTimeOffset.UtcNow);
    }

    private static string Normalize(string text) =>
        WhitespaceRx.Replace(text, " ").Trim().ToLowerInvariant();

    private static readonly Regex WhitespaceRx = new(@"\s+", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
