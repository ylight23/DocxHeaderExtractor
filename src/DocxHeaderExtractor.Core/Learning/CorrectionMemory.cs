using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DocxHeaderExtractor.Core.Eval;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Core.Learning;

public sealed record VerifiedCorrection(
    string Id,
    string SourceFile,
    string StableId,
    string Text,
    int PredictedLevel,
    int CorrectedLevel,
    DateTimeOffset CreatedUtc);

/// <summary>
/// Bộ nhớ correction cục bộ. Chỉ lưu thay đổi thật sự của người dùng; không coi dự đoán được
/// chấp nhận hàng loạt là ground truth. Retrieval chỉ đưa ví dụ vào prompt, không tự sửa kết quả.
/// </summary>
public sealed class CorrectionMemory
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<VerifiedCorrection> _items = [];
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);

    public CorrectionMemory(string path)
    {
        _path = Path.GetFullPath(path);
        Load();
    }

    public string PathOnDisk => _path;
    public int Count => _items.Count;

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
            _items.AddRange(fresh);
            return fresh.Count;
        }
        finally
        {
            _gate.Release();
        }
    }

    public IReadOnlyList<VerifiedCorrection> FindExamples(string documentView, int limit = 3)
    {
        if (_items.Count == 0 || limit <= 0) return [];
        var texts = ExtractTexts(documentView);
        if (texts.Count == 0) return [];
        var ranked = _items
            .Select(c => (Correction: c, Score: texts.Select(t => Similarity(t, c.Text)).DefaultIfEmpty(0).Max()))
            .Where(x => x.Score >= 0.78)
            .OrderByDescending(x => x.Score).ThenByDescending(x => x.Correction.CreatedUtc)
            .Take(limit)
            .Select(x => x.Correction)
            .ToList();
        return ranked;
    }

    public static string InjectExamples(string documentView, IReadOnlyList<VerifiedCorrection> examples)
    {
        if (examples.Count == 0) return documentView;

        var neutralClose = documentView.LastIndexOf("END_DOCUMENT_VIEW", StringComparison.Ordinal);
        if (neutralClose >= 0)
        {
            var sb = new StringBuilder(documentView.Length + examples.Count * 180);
            sb.Append(documentView.AsSpan(0, neutralClose));
            sb.AppendLine("VERIFIED_EXAMPLES advisory=true");
            foreach (var example in examples)
                sb.AppendLine(JsonSerializer.Serialize(new
                {
                    level = example.CorrectedLevel,
                    text = Truncate(example.Text, 300),
                }, JsonOptions));
            sb.AppendLine("END_VERIFIED_EXAMPLES");
            sb.Append(documentView.AsSpan(neutralClose));
            return sb.ToString();
        }

        // Tương thích các review bundle/prompt cũ đã lưu ở dạng XML.
        var xmlClose = documentView.LastIndexOf("</doc>", StringComparison.Ordinal);
        if (xmlClose < 0) return documentView;
        var legacy = new StringBuilder(documentView.Length + examples.Count * 180);
        legacy.Append(documentView.AsSpan(0, xmlClose));
        legacy.Append("<verified_examples advisory=\"1\">");
        foreach (var example in examples)
            legacy.Append("<ex level=\"").Append(example.CorrectedLevel).Append("\">")
                .Append(Escape(Truncate(example.Text, 300))).Append("</ex>");
        legacy.Append("</verified_examples>");
        legacy.Append(documentView.AsSpan(xmlClose));
        return legacy.ToString();
    }

    private static IReadOnlyList<string> ExtractTexts(string documentView)
    {
        if (documentView.Contains("DOCUMENT_VIEW", StringComparison.Ordinal))
            return [.. NeutralContentRx.Matches(documentView)
                .Select(match => match.Groups[1].Value.Trim())
                .Where(text => text.Length > 0)];

        try
        {
            var doc = XDocument.Parse(documentView, LoadOptions.PreserveWhitespace);
            return [.. doc.Descendants("p").Select(x => x.Value.Trim()).Where(x => x.Length > 0)];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Áp dụng ground truth chỉ khi khớp đồng thời tên file, stable ID và nguyên văn paragraph.
    /// Không tổng quát hóa sang file/đoạn khác; model vẫn xử lý các trường hợp tương tự.
    /// </summary>
    public int ApplyExact(string sourceFile, SlimDocument document, List<HeadingRecord> headings)
    {
        var file = System.IO.Path.GetFileName(sourceFile);
        var applicable = _items
            .Where(x => string.Equals(x.SourceFile, file, StringComparison.OrdinalIgnoreCase))
            .GroupBy(x => (x.StableId, Text: Normalize(x.Text)))
            .Select(g => g.OrderByDescending(x => x.CreatedUtc).First())
            .ToList();
        if (applicable.Count == 0) return 0;

        var byIndex = headings.ToDictionary(x => x.Index);
        var applied = 0;
        foreach (var correction in applicable)
        {
            var paragraph = document.Paragraphs.FirstOrDefault(p =>
                string.Equals(p.StableId, correction.StableId, StringComparison.Ordinal) &&
                Normalize(p.Text) == Normalize(correction.Text));
            if (paragraph is null) continue;

            if (correction.CorrectedLevel == 0)
            {
                if (byIndex.Remove(paragraph.Index)) applied++;
                continue;
            }

            if (byIndex.TryGetValue(paragraph.Index, out var existing))
            {
                existing.Level = correction.CorrectedLevel;
                existing.Source = HeadingSource.HumanCorrection;
                existing.Confidence = 1;
                existing.Disputed = false;
            }
            else
            {
                byIndex[paragraph.Index] = new HeadingRecord
                {
                    Index = paragraph.Index,
                    StableId = paragraph.StableId,
                    Level = correction.CorrectedLevel,
                    Text = paragraph.Text,
                    StyleId = paragraph.StyleId,
                    Source = HeadingSource.HumanCorrection,
                    Confidence = 1,
                };
            }
            applied++;
        }

        headings.Clear();
        headings.AddRange(byIndex.Values.OrderBy(x => x.Index));
        return applied;
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;
        foreach (var line in File.ReadLines(_path))
        {
            try
            {
                var item = JsonSerializer.Deserialize<VerifiedCorrection>(line, JsonOptions);
                if (item is null || !_ids.Add(item.Id)) continue;
                _items.Add(item);
            }
            catch (JsonException) { /* Bỏ qua một dòng hỏng, giữ các correction còn lại. */ }
        }
    }

    private static VerifiedCorrection Create(string sourceFile, ReviewRow row, int correctedLevel)
    {
        var canonical = $"{Normalize(row.Text)}\n{correctedLevel}";
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant()[..24];
        return new VerifiedCorrection(id, System.IO.Path.GetFileName(sourceFile), row.StableId,
            row.Text, row.PredictedLevel, correctedLevel, DateTimeOffset.UtcNow);
    }

    private static double Similarity(string candidate, string memory)
    {
        var a = Normalize(candidate);
        var b = Normalize(memory);
        if (a == b) return 1;

        var at = NumberingAudit.Parse(candidate);
        var bt = NumberingAudit.Parse(memory);
        if (at is null || bt is null || at.Value.Signature != bt.Value.Signature) return 0;

        var aw = Words(candidate);
        var bw = Words(memory);
        var shared = aw.Intersect(bw).Count();
        if (shared < 2) return 0;
        return (double)shared / aw.Union(bw).Count();
    }

    private static HashSet<string> Words(string text) =>
        WordRx.Matches(text.ToLowerInvariant()).Select(m => m.Value).Where(x => x.Any(char.IsLetter)).ToHashSet();

    private static string Normalize(string text) =>
        WhitespaceRx.Replace(text, " ").Trim().ToLowerInvariant();

    private static string Escape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    private static readonly Regex WordRx = new(@"\p{L}[\p{L}\p{N}]*", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRx = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex NeutralContentRx = new(
        @"(?:^|\n)content:\r?\n {4}([^\r\n]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
