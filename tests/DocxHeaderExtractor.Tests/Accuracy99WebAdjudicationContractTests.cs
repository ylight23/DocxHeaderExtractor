namespace DocxHeaderExtractor.Tests;

public sealed class Accuracy99WebAdjudicationContractTests
{
    [Fact]
    public void Web_adjudication_view_is_separate_source_first_and_keeps_product_route()
    {
        var root = FindRepositoryRoot();
        var web = Path.Combine(root, "src", "DocxHeaderExtractor.Web", "wwwroot");
        var html = File.ReadAllText(Path.Combine(web, "accuracy99", "adjudication.html"));
        var script = File.ReadAllText(Path.Combine(web, "accuracy99", "adjudication.js"));
        var product = File.ReadAllText(Path.Combine(web, "index.html"));

        Assert.True(File.Exists(Path.Combine(web, "accuracy99", "adjudication.css")));
        Assert.Contains("Accuracy-99 Human Adjudication", product, StringComparison.Ordinal);
        Assert.Contains("/api/extract", product, StringComparison.Ordinal);
        foreach (var token in new[] { "Human Review", "docxFile", "HEADING", "NON_HEADING", "UNCERTAIN", "EXCLUDED", "Next Unreviewed", "EXACT_REBOUND", "REVIEW_REQUIRED", "AMBIGUOUS", "Structural type", "LEVEL_NOT_REVIEWED", "PARENT_REVIEWED", "Validate review", "Finalize GOLD_FROZEN", "Run &amp; Compare", "predictionsIncluded" })
            Assert.Contains(token, html + script, StringComparison.Ordinal);
        Assert.Contains("/api/accuracy99/review/source", script, StringComparison.Ordinal);
        Assert.Contains("/api/accuracy99/review/session", script, StringComparison.Ordinal);
        Assert.Contains("crypto.subtle", script, StringComparison.Ordinal);
        Assert.DoesNotContain("packetFiles", html + script, StringComparison.Ordinal);
        Assert.DoesNotContain("review.jsonl" + "\"", html + script, StringComparison.Ordinal);
        Assert.Contains("Source-first mode never loads prediction, model, confidence, or current pipeline result.", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Synthetic_fixture_is_a_valid_unlabeled_round_trip_packet()
    {
        var path = Path.Combine(FindRepositoryRoot(), "tests", "fixtures", "accuracy99", "adjudication-ui-smoke.jsonl");
        var lines = File.ReadAllLines(path).Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
        using var manifest = System.Text.Json.JsonDocument.Parse(lines[0]);
        Assert.Equal("synthetic-ui-smoke", manifest.RootElement.GetProperty("datasetId").GetString());
        Assert.False(manifest.RootElement.GetProperty("predictionsIncluded").GetBoolean());
        Assert.Equal(6, lines.Length - 1);
        var ids = lines.Skip(1).Select(line => System.Text.Json.JsonDocument.Parse(line).RootElement.GetProperty("sourceId").GetString()).ToArray();
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("round-trip-preserve", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void Web_runtime_uses_the_canonical_tool_adapter()
    {
        var root = FindRepositoryRoot();
        var program = File.ReadAllText(Path.Combine(root, "src", "DocxHeaderExtractor.Web", "Program.cs"));
        var tool = File.ReadAllText(Path.Combine(root, "src", "DocxHeaderExtractor.AgentHarness", "DocumentExtractionTool.cs"));
        Assert.Contains("/api/extract", program, StringComparison.Ordinal);
        Assert.Contains("PipelineDocumentExtractionTool", program, StringComparison.Ordinal);
        Assert.Contains("new DocumentProcessingService(", tool, StringComparison.Ordinal);
        Assert.Contains("new AuthorityExtractionPipeline(options", tool, StringComparison.Ordinal);
        Assert.Contains("ProcessStructureOnlyAsync", tool, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DocxHeaderExtractor.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
