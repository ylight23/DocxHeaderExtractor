using DocxHeaderExtractor.Application.Tasks;

namespace DocxHeaderExtractor.Infrastructure.Sources;

/// <summary>
/// Host-owned file resolver for opaque application resources. A caller must explicitly allow the
/// root directory; the application contract never treats a locator as an unrestricted file path.
/// </summary>
public sealed class FileInputResourceResolver : IInputResourceResolver
{
    private readonly string[] _allowedRoots;

    public FileInputResourceResolver(IEnumerable<string> allowedRoots)
    {
        ArgumentNullException.ThrowIfNull(allowedRoots);
        _allowedRoots = allowedRoots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (_allowedRoots.Length == 0)
            throw new ArgumentException("Cần ít nhất một thư mục nguồn được allowlist.", nameof(allowedRoots));
    }

    public ValueTask<ResolvedInputResource> ResolveAsync(
        InputResource resource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(resource.Locator))
            throw new ArgumentException("Resource locator không được rỗng.", nameof(resource));

        var path = Path.GetFullPath(resource.Locator);
        if (!_allowedRoots.Any(root => IsWithinRoot(path, root)))
            throw new UnauthorizedAccessException("Resource nằm ngoài thư mục nguồn được allowlist.");
        if (!File.Exists(path))
            throw new FileNotFoundException("Không tìm thấy input resource.", path);

        Stream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        return ValueTask.FromResult(new ResolvedInputResource(resource, stream, LeaveOpen: false));
    }

    private static bool IsWithinRoot(string path, string root)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(path, root, StringComparison.OrdinalIgnoreCase);
    }
}
