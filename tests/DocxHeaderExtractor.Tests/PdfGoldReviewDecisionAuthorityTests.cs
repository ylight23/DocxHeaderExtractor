using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The human-owned occurrence decision authority for DOC-0252.
///
/// This file is intentionally not a FreezeArtifact. The generated review views are disposable
/// readings of the parser source universe; this authority is the worksheet a person edits. An
/// ordinary test run may validate it, but it must never regenerate or overwrite it.
/// </summary>
public sealed class PdfGoldReviewDecisionAuthorityTests
{
    private const string Pack = "eval/a99-closed-loop/pdf-gold-doc0252";
    private const string SourceSha =
        "a005f25e3bb9754cd6c8c7000682d00eb68238fb8937d3475fe807ffbbd94b61";
    private const string SourceUniverseSha =
        "5dd617b27d7c1479f81fb41468076b5f6ba826a7d86cc9a04f6dfa0fa624c3ec";

    [Fact]
    public void Human_owned_decisions_cover_the_exact_source_universe_without_answers()
    {
        using var authority = Open("review-decisions.v1.json");
        var root = authority.RootElement;

        Assert.Equal("a99_pdf_gold_review_decisions", root.GetProperty("artifactKind").GetString());
        Assert.Equal("a99-pdf-gold-review-decisions-v1",
            root.GetProperty("schemaVersion").GetString());
        Assert.Equal("DOC-0252", root.GetProperty("documentId").GetString());
        Assert.Equal(SourceSha, root.GetProperty("sourceSha256").GetString());
        Assert.Equal(SourceUniverseSha, root.GetProperty("sourceUniverseSha256").GetString());

        var decisions = root.GetProperty("decisions").EnumerateArray().ToArray();
        var source = Open("source-universe.v1.json").RootElement.GetProperty("rows")
            .EnumerateArray().ToArray();

        Assert.Equal(1013, source.Length);
        Assert.Equal(source.Length, decisions.Length);
        Assert.Equal(
            source.Select(row => row.GetProperty("sourceAlias").GetString()),
            decisions.Select(row => row.GetProperty("sourceAlias").GetString()));

        Assert.All(decisions, decision =>
        {
            var properties = decision.EnumerateObject().Select(property => property.Name).ToArray();
            Assert.Equal(["sourceAlias", "humanDecision", "headingClaims"], properties);
            Assert.Null(decision.GetProperty("humanDecision").GetString());
            Assert.Equal(JsonValueKind.Array, decision.GetProperty("headingClaims").ValueKind);
            Assert.Empty(decision.GetProperty("headingClaims").EnumerateArray());
        });
    }

    [Fact]
    public void The_authority_is_bound_to_the_committed_source_universe_hash()
    {
        using var source = Open("source-universe.v1.json");
        Assert.Equal(SourceUniverseSha, CanonicalArtifactHash.OfTextFile(SourcePath()));
        Assert.Equal(
            SourceSha,
            source.RootElement.GetProperty("sourceSha256").GetString());
    }

    private static JsonDocument Open(string name) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), Pack, name)));

    private static string SourcePath() =>
        Path.Combine(RepositoryRoot(), Pack, "source-universe.v1.json");

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DocxHeaderExtractor.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Cannot find repository root.");
    }
}
