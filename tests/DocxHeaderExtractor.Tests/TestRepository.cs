namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Where the repository is, answered once.
/// <para>
/// Sixty-odd test files each carried their own copy of this, under four different names, in two
/// materially different shapes: one walked up from the test binary looking for the solution file,
/// the other hard-coded <c>../../../../..</c>. They resolve to the same directory in the current
/// layout, which is why both survived - but only one of them stays correct if the build output
/// moves, and a suite where half the tests find the repository a different way from the other half
/// is a suite that will one day fail in a way nobody can read.
/// </para>
/// <para>
/// The walk-up is the surviving rule: it asks for the thing it actually wants rather than counting
/// directories, so it does not depend on how deep the output path happens to be.
/// </para>
/// </summary>
internal static class TestRepository
{
    private static readonly Lazy<string> Located = new(Locate);

    /// <summary>The repository root - the directory holding DocxHeaderExtractor.sln.</summary>
    public static string Root() => Located.Value;

    /// <summary>A repository path from forward-slash segments, as the artifacts name them.</summary>
    public static string Path(string relative)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relative);
        return System.IO.Path.Combine(Root(), relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
    }

    private static string Locate()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(System.IO.Path.Combine(dir.FullName, "DocxHeaderExtractor.sln")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new DirectoryNotFoundException(
            $"No DocxHeaderExtractor.sln above {AppContext.BaseDirectory}; the test binary is not inside the repository.");
    }
}
