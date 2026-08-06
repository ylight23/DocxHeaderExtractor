namespace DocxHeaderExtractor.Mcp;

/// <summary>
/// Chặn path traversal và việc để model đọc tuỳ ý file trên máy. Mọi đường dẫn tương đối được
/// neo vào root đầu tiên; đường dẫn tuyệt đối vẫn phải nằm trong một root đã cho phép.
/// </summary>
public sealed class McpPathPolicy(DhxMcpOptions options)
{
    private readonly DhxMcpOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    public string ResolveReadableDocument(string inputPath)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
            throw new ArgumentException("inputPath không được để trống.", nameof(inputPath));
        if (inputPath.Length > 1_024)
            throw new ArgumentException("inputPath vượt 1024 ký tự.", nameof(inputPath));

        var candidate = Path.IsPathFullyQualified(inputPath)
            ? Path.GetFullPath(inputPath)
            : Path.GetFullPath(inputPath, _options.AllowedRoots[0]);

        if (!_options.AllowedRoots.Any(root => IsWithin(candidate, root)))
            throw new UnauthorizedAccessException(
                $"File nằm ngoài DHX_MCP_ALLOWED_ROOTS: {Path.GetFileName(candidate)}");
        if (!File.Exists(candidate))
            throw new FileNotFoundException($"Không tìm thấy file: {Path.GetFileName(candidate)}", candidate);

        var length = new FileInfo(candidate).Length;
        if (length > _options.MaxInputBytes)
            throw new InvalidOperationException(
                $"File {Path.GetFileName(candidate)} có {length} byte, vượt giới hạn {_options.MaxInputBytes} byte.");

        return candidate;
    }

    internal static bool IsWithin(string candidate, string root)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var fullCandidate = Path.GetFullPath(candidate);
        var fullRoot = DhxMcpOptions.NormalizeRoot(root);
        if (string.Equals(Path.TrimEndingDirectorySeparator(fullCandidate), fullRoot, comparison)) return true;

        var prefix = fullRoot + Path.DirectorySeparatorChar;
        return fullCandidate.StartsWith(prefix, comparison);
    }
}
