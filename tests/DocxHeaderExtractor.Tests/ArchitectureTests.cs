using System.Text.RegularExpressions;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The dependency rules of docs/architecture/current.md, read from the projects' own files: each project may reference only the projects in
/// its row. A new reference that is not in the table fails here, not in review. Hosts (Cli, Web) may also reference HeadingAgent, for its
/// diagnostics and prompt catalog; that is the one declared exception.
/// </summary>
public sealed class ArchitectureTests
{
    static readonly Dictionary<string, string[]> Allowed = new(StringComparer.Ordinal)
    {
        ["Contracts"] = [],
        ["Agents"] = [],
        ["Conversation"] = ["Agents"],
        ["Sources"] = ["Contracts"],
        ["HeadingAgent"] = ["Agents", "Conversation", "Sources"],
        ["DocumentProcessing"] = ["Contracts", "Sources"],
        ["Infrastructure"] = ["Contracts", "Sources"],
        ["App"] = ["Agents", "Contracts", "DocumentProcessing", "HeadingAgent", "Sources"],
        ["Cli"] = ["App", "Contracts", "DocumentProcessing", "Sources"],
        ["Web"] = ["App", "Contracts", "DocumentProcessing", "Infrastructure", "Sources"],
    };

    /// <summary>The hosts reach the agents only through App: their source names no type of the heading agent.</summary>
    static readonly string[] Hosts = ["Cli", "Web"];

    [Fact]
    public void Hosts_name_no_type_of_the_heading_agent()
    {
        var named = Hosts.SelectMany(host => Directory.EnumerateFiles(TestRepository.Path($"src/DocxHeaderExtractor.{host}"), "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Where(f => File.ReadAllText(f).Contains("DocxHeaderExtractor.HeadingAgent", StringComparison.Ordinal)))
            .Select(f => Path.GetRelativePath(TestRepository.Root(), f))
            .ToArray();
        Assert.True(named.Length == 0, "Hosts must go through App: " + string.Join(", ", named));
    }

    static IEnumerable<(string Project, string[] References)> ProjectsUnderSrc()
    {
        var src = TestRepository.Path("src");
        foreach (var file in Directory.EnumerateFiles(src, "*.csproj", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var name = Path.GetFileNameWithoutExtension(file).Replace("DocxHeaderExtractor.", "", StringComparison.Ordinal);
            var refs = Regex.Matches(File.ReadAllText(file), "ProjectReference Include=\"[^\"]*?([^\\\\/\"]+)\\.csproj\"")
                .Select(m => m.Groups[1].Value.Replace("DocxHeaderExtractor.", "", StringComparison.Ordinal))
                .ToArray();
            yield return (name, refs);
        }
    }

    [Fact]
    public void Every_project_is_in_the_dependency_table()
    {
        var found = ProjectsUnderSrc().Select(p => p.Project).ToArray();
        // Guards against an empty search: the rules below would pass vacuously.
        Assert.Equal(Allowed.Count, found.Length);
        Assert.Empty(found.Where(p => !Allowed.ContainsKey(p)));
    }

    [Fact]
    public void No_project_references_a_project_outside_its_row()
    {
        var violations = ProjectsUnderSrc()
            .Where(p => Allowed.ContainsKey(p.Project))
            .SelectMany(p => p.References.Where(r => !Allowed[p.Project].Contains(r, StringComparer.Ordinal))
                .Select(r => $"{p.Project} -> {r}"))
            .ToArray();
        Assert.True(violations.Length == 0, "Dependency rule broken: " + string.Join("; ", violations));
    }
}
