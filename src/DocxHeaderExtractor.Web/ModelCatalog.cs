using DocxHeaderExtractor.DocumentProcessing.Inference;

namespace DocxHeaderExtractor.Web;

public sealed record ModelEntry(string Name, string Path, double SizeGb, uint SuggestedCtx, bool Recommended);

/// <summary>Quét các file .gguf để người dùng chọn trong giao diện, thay vì gõ đường dẫn.</summary>
public static class ModelCatalog
{
    public static IReadOnlyList<ModelEntry> List()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var found = new List<ModelEntry>();

        foreach (var dir in Directories())
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.gguf"))
            {
                var full = Path.GetFullPath(file);
                if (!seen.Add(full)) continue;
                var fi = new FileInfo(full);
                found.Add(new ModelEntry(
                    Name: fi.Name,
                    Path: full,
                    SizeGb: Math.Round(fi.Length / 1024.0 / 1024 / 1024, 2),
                    SuggestedCtx: SuggestCtx(fi.Name),
                    Recommended: false));
            }
        }

        if (found.Count == 0) return [];

        // Mô hình lớn hơn cho kết quả tốt hơn hẳn ở tác vụ này — đo được: Llama-3.2-3B đạt
        // precision 73%, Qwen2.5-7B đạt 100% trên cùng tài liệu. Kích thước file là ước lượng
        // thô cho năng lực, nhưng đủ để không mặc định chọn nhầm mô hình nhỏ nhất.
        var best = found.MaxBy(m => m.SizeGb)!;

        return
        [
            .. found
                .Select(m => m == best ? m with { Recommended = true } : m)
                .OrderByDescending(m => m.Recommended)
                .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
        ];
    }

    /// <summary>
    /// Dùng đúng profile trong Core để Web/CLI/model loader không đưa ra ba context khác nhau.
    /// Qwen2.5 và Llama 3.2 hiện đều cần 8192 với prompt cố định + ngân sách mặc định.
    /// </summary>
    private static uint SuggestCtx(string fileName) =>
        LocalModelOptions.SuggestedContextForModel(fileName);

    private static IEnumerable<string> Directories()
    {
        yield return Path.Combine(Directory.GetCurrentDirectory(), "models");
        yield return Path.Combine(AppContext.BaseDirectory, "models");
        // bin/Debug/net9.0 → lên gốc repo
        yield return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "models"));

        var env = Environment.GetEnvironmentVariable("DHX_MODEL");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(env));
            if (!string.IsNullOrEmpty(dir)) yield return dir;
        }
    }
}
