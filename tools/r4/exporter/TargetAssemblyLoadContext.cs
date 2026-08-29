using System.Reflection;
using System.Runtime.Loader;

public sealed class TargetAssemblyLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly string[] _fallbackRoots;

    public TargetAssemblyLoadContext(string mainAssemblyPath)
        : base(isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
        var targetRoot = Path.GetDirectoryName(mainAssemblyPath)!;
        var worktree = Path.GetFullPath(Path.Combine(targetRoot, "..", "..", "..", "..", ".."));
        _fallbackRoots = [
            targetRoot,
            Path.Combine(worktree,
                "tests", "DocxHeaderExtractor.Tests", "bin", "Release", "net9.0")
        ];
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        if (path is not null) return LoadFromAssemblyPath(path);
        foreach (var root in _fallbackRoots)
        {
            var candidate = Path.Combine(Path.GetFullPath(root), assemblyName.Name + ".dll");
            if (File.Exists(candidate)) return LoadFromAssemblyPath(candidate);
        }
        return null;
    }
}
