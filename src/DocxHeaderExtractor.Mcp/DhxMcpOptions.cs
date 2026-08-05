namespace DocxHeaderExtractor.Mcp;

/// <summary>
/// Cấu hình quyền của MCP host. Đây là cấu hình do người vận hành đặt, không phải tham số mà
/// model được phép thay đổi trong một tool call.
/// </summary>
public sealed class DhxMcpOptions
{
    public required IReadOnlyList<string> AllowedRoots { get; init; }

    public long MaxInputBytes { get; init; } = 50L * 1024 * 1024;

    /// <summary>Tắt LLM hoàn toàn; hữu ích để kiểm tra parser mà không cần LM Studio server.</summary>
    public bool RulesOnly { get; init; }

    public static DhxMcpOptions FromEnvironment()
    {
        var rawRoots = Environment.GetEnvironmentVariable("DHX_MCP_ALLOWED_ROOTS");
        if (string.IsNullOrWhiteSpace(rawRoots))
            throw new InvalidOperationException(
                "Thiếu DHX_MCP_ALLOWED_ROOTS. MCP phải được giới hạn vào ít nhất một thư mục tuyệt đối.");

        var roots = rawRoots
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeRoot)
            .Distinct(PathComparer)
            .ToArray();

        if (roots.Length == 0)
            throw new InvalidOperationException("DHX_MCP_ALLOWED_ROOTS không chứa thư mục hợp lệ.");
        foreach (var root in roots)
            if (!Directory.Exists(root))
                throw new DirectoryNotFoundException($"Thư mục MCP được phép không tồn tại: {root}");

        var maxBytes = 50L * 1024 * 1024;
        var rawMax = Environment.GetEnvironmentVariable("DHX_MCP_MAX_INPUT_BYTES");
        if (!string.IsNullOrWhiteSpace(rawMax) &&
            (!long.TryParse(rawMax, out maxBytes) || maxBytes is < 1_024 or > 2L * 1024 * 1024 * 1024))
            throw new InvalidOperationException(
                "DHX_MCP_MAX_INPUT_BYTES phải nằm trong khoảng 1024..2147483648 byte.");

        return new DhxMcpOptions
        {
            AllowedRoots = roots,
            MaxInputBytes = maxBytes,
            RulesOnly = ReadBool("DHX_MCP_RULES_ONLY"),
        };
    }

    internal static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal static string NormalizeRoot(string path)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new InvalidOperationException($"Thư mục MCP phải là đường dẫn tuyệt đối: {path}");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static bool ReadBool(string name)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw)) return false;
        return raw.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => throw new InvalidOperationException($"{name} phải là true/false."),
        };
    }
}
