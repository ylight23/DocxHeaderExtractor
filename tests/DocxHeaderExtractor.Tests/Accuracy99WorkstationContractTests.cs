namespace DocxHeaderExtractor.Tests;

public sealed class Accuracy99WorkstationContractTests
{
    [Fact]
    public void Workstation_is_static_offline_and_keeps_review_contract_visible()
    {
        var root = FindRepositoryRoot();
        var directory = Path.Combine(root, "eval", "accuracy99", "adjudication", "workstation");
        var html = File.ReadAllText(Path.Combine(directory, "index.html"));
        var script = File.ReadAllText(Path.Combine(directory, "app.js"));

        Assert.True(File.Exists(Path.Combine(directory, "styles.css")));
        Assert.True(File.Exists(Path.Combine(directory, "README.md")));
        Assert.Contains("type=\"file\"", html, StringComparison.Ordinal);
        Assert.Contains("app.js", html, StringComparison.Ordinal);
        Assert.Contains("FileReader", script, StringComparison.Ordinal);
        Assert.Contains("crypto.subtle", script, StringComparison.Ordinal);
        Assert.Contains("review.completed.jsonl", script, StringComparison.Ordinal);
        Assert.Contains("HEADING", script, StringComparison.Ordinal);
        Assert.Contains("NON_HEADING", script, StringComparison.Ordinal);
        Assert.Contains("UNCERTAIN", script, StringComparison.Ordinal);
        Assert.Contains("EXCLUDED", script, StringComparison.Ordinal);
        Assert.DoesNotContain("fetch(", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("XMLHttpRequest", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("openrouter", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sglang", script, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DocxHeaderExtractor.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
