using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// M11-B4.1 broad release smoke, model-free. Runs the product route with the model lane disabled
/// across the corpus and checks operational and contract invariants only.
/// <para>
/// This is not an accuracy benchmark and needs no gold. A document producing few headings or none is
/// not a finding here; a crash, an emitted record without canonical grounding, a fabricated level, a
/// product whose fingerprint does not match its own source, or a route that silently changes identity
/// is.
/// </para>
/// <para>
/// It cannot reach the model-backed PDF-first lane, which is why a small live canary is run
/// separately. What it does cover is every deterministic route the corpus exercises, at a breadth no
/// live run would be worth.
/// </para>
/// </summary>
public sealed class PdfReleaseSmokeProbe
{
    [Fact]
    public async Task Report()
    {
        var output = Environment.GetEnvironmentVariable("M11_B4_REPORT");
        if (string.IsNullOrWhiteSpace(output)) return;

        var corpus = Path.Combine(RepositoryRoot(), "todo10_8", "heading_corpus_95_word");
        var rows = new List<Row>();

        foreach (var path in Directory.EnumerateFiles(corpus, "*.docx", SearchOption.AllDirectories)
                     .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            try
            {
                using var pipeline = new HeaderExtractionPipeline(new PipelineOptions { DisableLlm = true });
                var outline = await pipeline.RunAsync(path);
                rows.Add(Inspect(name, path, outline));
            }
            catch (Exception ex)
            {
                rows.Add(new Row(name, "FATAL", 0, 0, 0, 0, 0, 0, false, [$"{ex.GetType().Name}: {ex.Message}"]));
            }
        }

        var violations = rows.SelectMany(r => r.Violations.Select(v => $"{r.Document}: {v}")).ToArray();
        var report = new
        {
            contract = "operational_and_contract_invariants_only; not an accuracy benchmark; model lane disabled",
            documents = rows.Count,
            fatal = rows.Count(r => r.Route == "FATAL"),
            withViolations = rows.Count(r => r.Violations.Count > 0),
            routes = rows.GroupBy(r => r.Route).OrderByDescending(g => g.Count())
                .ToDictionary(g => g.Key, g => g.Count()),
            violations,
            rows,
        };

        var options = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        File.WriteAllText(output, JsonSerializer.Serialize(report, options));
    }

    private static Row Inspect(string name, string path, DocumentOutline outline)
    {
        var violations = new List<string>();
        var product = outline.ProductOutput;

        // Every emitted product record must carry a canonical anchor and a level the writeback could
        // act on or none at all. A level outside 1..9 would be a fabricated one.
        var ungrounded = 0;
        var invalidLevel = 0;
        var unresolvedLevel = 0;
        if (product is not null)
        {
            foreach (var heading in product.Headings)
            {
                if (string.IsNullOrWhiteSpace(heading.StableId)) { ungrounded++; violations.Add($"emitted record without stable id: {heading.Id}"); }
                if (heading.Level is null) unresolvedLevel++;
                else if (heading.Level is < 1 or > 9) { invalidLevel++; violations.Add($"level out of range: {heading.Id}={heading.Level}"); }
                if (heading.ParagraphIndex < 0) violations.Add($"negative paragraph index: {heading.Id}");
            }

            var actual = Sha256(path);
            if (!string.Equals(actual, product.SourceDocumentSha256, StringComparison.OrdinalIgnoreCase))
                violations.Add("product fingerprint does not match its own source document");
        }

        // The compatibility shell must not carry headings the product does not know about: that is
        // the shape a silent legacy resurrection would take.
        if (product is not null && outline.Headings.Count != product.Headings.Count &&
            outline.DeterministicRoute?.StartsWith("pdf", StringComparison.OrdinalIgnoreCase) == true)
            violations.Add($"outline/product heading count diverges: {outline.Headings.Count} vs {product.Headings.Count}");

        foreach (var heading in outline.Headings)
        {
            if (heading.Level is < 1 or > 9) violations.Add($"outline level out of range: {heading.Level}");
            if (heading.Index < 0) violations.Add("outline heading with negative index");
        }

        return new Row(
            name,
            outline.DeterministicRoute ?? "(none)",
            outline.Headings.Count,
            product?.Headings.Count ?? -1,
            ungrounded,
            invalidLevel,
            unresolvedLevel,
            outline.Headings.Count(h => h.DecisionStatus == HeadingDecisionStatus.RequiresReview),
            product is not null,
            violations);
    }

    private static string Sha256(string path) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private sealed record Row(
        string Document,
        string Route,
        int OutlineHeadings,
        int ProductHeadings,
        int Ungrounded,
        int InvalidLevel,
        int UnresolvedLevel,
        int RequiresReview,
        bool HasProductOutput,
        IReadOnlyList<string> Violations);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DocxHeaderExtractor.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? AppContext.BaseDirectory;
    }
}
