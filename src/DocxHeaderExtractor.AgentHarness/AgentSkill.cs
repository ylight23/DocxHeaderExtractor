using System.Security.Cryptography;
using System.Text;
using DocxHeaderExtractor.Application.Skills;

namespace DocxHeaderExtractor.AgentHarness;

/// <summary>
/// Ràng buộc mà skill đặt lên harness. Đây là phần DUY NHẤT của SKILL.md có hiệu lực lúc chạy:
/// nó khai báo cấu hình tối thiểu mà một run hợp lệ phải có. Phần văn xuôi còn lại là tài liệu
/// cho người và không bao giờ được nạp vào prompt — một file hướng dẫn sửa được tự do mà lại
/// chảy thẳng vào prompt thì chính nó trở thành đường tiêm chỉ thị.
/// </summary>
public sealed record AgentSkillRequirements
{
    public IReadOnlyList<string> Guardrails { get; init; } = [];
    public IReadOnlyList<string> Validators { get; init; } = [];

    /// <summary>Cấm mọi hành động ghi khi vẫn còn mục chờ người duyệt.</summary>
    public bool HumanReviewBeforeWriteback { get; init; } = true;

    /// <summary>Trần số lượt sửa; harness chỉ được cấu hình thấp hơn hoặc bằng.</summary>
    public int MaxRepairAttempts { get; init; } = 1;
}

public sealed record AgentSkill(
    string Name,
    string Description,
    string Version,
    string Digest,
    string Path,
    AgentSkillRequirements Requires,
    IReadOnlyList<string> Sections)
{
    public SkillDescriptor ToDescriptor() => new(
        Name, Version, Digest, SkillLifecycle.Active, [], Requires.Guardrails, Requires.Validators,
        Requires.HumanReviewBeforeWriteback, Requires.MaxRepairAttempts);

    public override string ToString() => $"{Name}@{Version} ({Digest})";
}

public sealed class AgentSkillException(string message) : InvalidOperationException(message);

/// <summary>
/// Nạp policy skill từ đĩa. Bộ đọc cố tình hẹp: chỉ chấp nhận front matter YAML một tầng với
/// đúng các khoá đã biết, giới hạn kích thước, và khoá lạ là lỗi chứ không phải bỏ qua âm thầm.
/// </summary>
public static class AgentSkillLoader
{
    public const string DefaultRelativePath = "skills/heading-extraction/SKILL.md";
    private const int MaxBytes = 64 * 1024;

    public static AgentSkill LoadDefault()
    {
        var path = Locate()
                   ?? throw new AgentSkillException(
                       $"Không tìm thấy policy skill ({DefaultRelativePath}). " +
                       "Harness không chạy khi thiếu hợp đồng skill; đặt biến môi trường DHX_SKILL " +
                       "nếu file nằm ngoài thư mục ứng dụng.");
        return Load(path);
    }

    public static string? Locate()
    {
        var env = Environment.GetEnvironmentVariable("DHX_SKILL");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return System.IO.Path.GetFullPath(env);

        foreach (var root in Roots())
        {
            var candidate = System.IO.Path.Combine(root, DefaultRelativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return System.IO.Path.GetFullPath(candidate);
        }

        return null;

        static IEnumerable<string> Roots()
        {
            yield return AppContext.BaseDirectory;
            yield return Directory.GetCurrentDirectory();

            // bin/Debug/net9.0 → lên gốc repo, cho lúc chạy từ IDE/test runner.
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (var i = 0; i < 6 && dir?.Parent is not null; i++)
            {
                dir = dir.Parent;
                yield return dir.FullName;
            }
        }
    }

    public static AgentSkill Load(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        var info = new FileInfo(full);
        if (!info.Exists) throw new AgentSkillException($"Không tìm thấy skill: {full}");
        if (info.Length > MaxBytes)
            throw new AgentSkillException($"Skill vượt {MaxBytes} byte: {full}");

        var bytes = File.ReadAllBytes(full);
        var digest = Convert.ToHexString(SHA256.HashData(bytes))[..12].ToLowerInvariant();
        var text = new UTF8Encoding(false).GetString(bytes).ReplaceLineEndings("\n");

        return Parse(text, full, digest);
    }

    public static AgentSkill Parse(string text, string path, string digest)
    {
        var lines = text.Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---")
            throw new AgentSkillException($"Skill thiếu front matter mở đầu bằng '---': {path}");

        var end = Array.FindIndex(lines, 1, l => l.Trim() == "---");
        if (end < 0) throw new AgentSkillException($"Skill thiếu '---' đóng front matter: {path}");

        var front = ParseFrontMatter(lines[1..end], path);
        var sections = lines[(end + 1)..]
            .Where(l => l.StartsWith("## ", StringComparison.Ordinal))
            .Select(l => l[3..].Trim())
            .ToArray();

        var name = Required(front, "name", path);
        var version = Required(front, "version", path);
        var description = front.GetValueOrDefault("description")?.Scalar ?? "";

        return new AgentSkill(
            name, description, version, digest, path,
            ReadRequirements(front, path), sections);
    }

    private static AgentSkillRequirements ReadRequirements(
        IReadOnlyDictionary<string, FrontMatterValue> front,
        string path)
    {
        if (!front.TryGetValue("requires", out var requires)) return new AgentSkillRequirements();
        var map = requires.Map
                  ?? throw new AgentSkillException($"'requires' phải là khối con: {path}");

        foreach (var key in map.Keys)
            if (key is not ("guardrails" or "validators" or "humanReviewBeforeWriteback" or "maxRepairAttempts"))
                throw new AgentSkillException($"Khoá 'requires.{key}' không được hỗ trợ: {path}");

        return new AgentSkillRequirements
        {
            Guardrails = map.GetValueOrDefault("guardrails")?.List ?? [],
            Validators = map.GetValueOrDefault("validators")?.List ?? [],
            HumanReviewBeforeWriteback =
                ReadBool(map, "humanReviewBeforeWriteback", true, path),
            MaxRepairAttempts = ReadInt(map, "maxRepairAttempts", 1, 0, 8, path),
        };
    }

    private sealed record FrontMatterValue(
        string? Scalar,
        IReadOnlyList<string>? List,
        IReadOnlyDictionary<string, FrontMatterValue>? Map);

    private static Dictionary<string, FrontMatterValue> ParseFrontMatter(string[] lines, string path)
    {
        var root = new Dictionary<string, FrontMatterValue>(StringComparer.Ordinal);
        Dictionary<string, FrontMatterValue>? nested = null;

        foreach (var raw in lines)
        {
            if (raw.Trim().Length == 0 || raw.TrimStart().StartsWith('#')) continue;

            var indented = raw.Length > 0 && (raw[0] == ' ' || raw[0] == '\t');
            var colon = raw.IndexOf(':');
            if (colon < 0) throw new AgentSkillException($"Dòng front matter thiếu ':' — {raw.Trim()} ({path})");

            var key = raw[..colon].Trim();
            var value = raw[(colon + 1)..].Trim();
            if (key.Length == 0) throw new AgentSkillException($"Khoá rỗng trong front matter: {path}");

            if (indented)
            {
                if (nested is null)
                    throw new AgentSkillException($"Dòng thụt lề không thuộc khối nào: {raw.Trim()} ({path})");
                nested[key] = ReadValue(value, path);
                continue;
            }

            if (value.Length == 0)
            {
                nested = new Dictionary<string, FrontMatterValue>(StringComparer.Ordinal);
                root[key] = new FrontMatterValue(null, null, nested);
                continue;
            }

            nested = null;
            root[key] = ReadValue(value, path);
        }

        return root;
    }

    private static FrontMatterValue ReadValue(string value, string path)
    {
        if (!value.StartsWith('[')) return new FrontMatterValue(value, null, null);
        if (!value.EndsWith(']'))
            throw new AgentSkillException($"Danh sách front matter thiếu ']': {value} ({path})");

        var items = value[1..^1]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(i => i.Trim('"', '\''))
            .Where(i => i.Length > 0)
            .ToArray();
        return new FrontMatterValue(null, items, null);
    }

    private static string Required(
        IReadOnlyDictionary<string, FrontMatterValue> front, string key, string path) =>
        front.GetValueOrDefault(key)?.Scalar is { Length: > 0 } value
            ? value
            : throw new AgentSkillException($"Skill thiếu khoá bắt buộc '{key}': {path}");

    private static bool ReadBool(
        IReadOnlyDictionary<string, FrontMatterValue> map, string key, bool fallback, string path)
    {
        if (map.GetValueOrDefault(key)?.Scalar is not { } raw) return fallback;
        return raw.ToLowerInvariant() switch
        {
            "true" or "yes" => true,
            "false" or "no" => false,
            _ => throw new AgentSkillException($"'{key}' phải là true/false, nhận '{raw}' ({path})"),
        };
    }

    private static int ReadInt(
        IReadOnlyDictionary<string, FrontMatterValue> map,
        string key, int fallback, int min, int max, string path)
    {
        if (map.GetValueOrDefault(key)?.Scalar is not { } raw) return fallback;
        if (!int.TryParse(raw, out var value))
            throw new AgentSkillException($"'{key}' phải là số nguyên, nhận '{raw}' ({path})");
        if (value < min || value > max)
            throw new AgentSkillException($"'{key}' phải nằm trong {min}..{max}, nhận {value} ({path})");
        return value;
    }
}
