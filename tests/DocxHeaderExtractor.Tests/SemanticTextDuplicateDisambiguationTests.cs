using System.Text.Json;
using DocxHeaderExtractor.Eval.ReasoningRetention;

namespace DocxHeaderExtractor.Tests;

public sealed class SemanticTextDuplicateDisambiguationTests
{
    [Fact]
    public void Unique_exact_text_binds_unchanged()
    {
        var aliases = new[] { new SemanticTextSourceAlias("S1", "body/p1", 1, "Heading") };
        var bound = SemanticTextDuplicateBinder.Bind([new("S1", "Heading", "SECTION")], aliases, out var observations, out var traces);
        var item = Assert.Single(bound);
        Assert.Equal((0, 7), (item.Start, item.End));
        Assert.Equal(SemanticTextBindingStatus.BOUND, Assert.Single(observations).Status);
        Assert.Equal("UNIQUE_TEXT_BIND", Assert.Single(traces).Kind);
    }

    [Fact]
    public void Duplicate_without_discriminator_remains_ambiguous()
    {
        var aliases = new[] { new SemanticTextSourceAlias("S1", "body/p1", 1, "Africa|Africa") };
        var bound = SemanticTextDuplicateBinder.Bind([new("S1", "Africa", "SECTION")], aliases, out var observations, out var traces);
        Assert.Empty(bound);
        Assert.Equal(SemanticTextBindingStatus.AMBIGUOUS_EXACT_TEXT, Assert.Single(observations).Status);
        Assert.Equal("AMBIGUOUS_DUPLICATE_TEXT", Assert.Single(traces).Kind);
    }

    [Fact]
    public void Exact_before_context_resolves_one_occurrence()
    {
        var aliases = new[] { new SemanticTextSourceAlias("S1", "body/p1", 1, "x Africa y Africa z") };
        var bound = SemanticTextDuplicateBinder.Bind([new("S1", "Africa", "SUBSECTION", null, "x ", null)], aliases, out _, out var traces);
        Assert.Equal(2, Assert.Single(bound).Start);
        Assert.Equal("DUPLICATE_RESOLVED_BY_EXACT_CONTEXT", Assert.Single(traces).Kind);
    }

    [Fact]
    public void Exact_after_context_resolves_one_occurrence()
    {
        var aliases = new[] { new SemanticTextSourceAlias("S1", "body/p1", 1, "x Africa y Africa z") };
        var bound = SemanticTextDuplicateBinder.Bind([new("S1", "Africa", "SUBSECTION", null, null, " y")], aliases, out _, out var traces);
        Assert.Equal(2, Assert.Single(bound).Start);
        Assert.Equal("DUPLICATE_RESOLVED_BY_EXACT_CONTEXT", Assert.Single(traces).Kind);
    }

    [Fact]
    public void Both_exact_contexts_resolve_one_occurrence()
    {
        var aliases = new[] { new SemanticTextSourceAlias("S1", "body/p1", 1, "x Africa y Africa z") };
        var bound = SemanticTextDuplicateBinder.Bind([new("S1", "Africa", "SUBSECTION", null, "x ", " y")], aliases, out _, out _);
        Assert.Equal(2, Assert.Single(bound).Start);
    }

    [Fact]
    public void Anchors_matching_multiple_occurrences_remain_ambiguous()
    {
        var aliases = new[] { new SemanticTextSourceAlias("S1", "body/p1", 1, "x Africa y Africa y") };
        var bound = SemanticTextDuplicateBinder.Bind([new("S1", "Africa", "SUBSECTION", null, null, " y")], aliases, out var observations, out var traces);
        Assert.Empty(bound);
        Assert.Equal("AMBIGUOUS_DUPLICATE_TEXT", Assert.Single(traces).Kind);
        Assert.Equal(SemanticTextBindingStatus.AMBIGUOUS_EXACT_TEXT, Assert.Single(observations).Status);
    }

    [Fact]
    public void Invalid_or_hallucinated_context_is_rejected_without_fallback()
    {
        var aliases = new[] { new SemanticTextSourceAlias("S1", "body/p1", 1, "x Africa y Africa z") };
        var bound = SemanticTextDuplicateBinder.Bind([new("S1", "Africa", "SUBSECTION", null, "hallucinated ", null)], aliases, out var observations, out var traces);
        Assert.Empty(bound);
        Assert.Equal("INVALID_CONTEXT_ANCHOR", Assert.Single(traces).Kind);
        Assert.Equal("INVALID_CONTEXT_ANCHOR", Assert.Single(observations).FailureReason);
    }

    [Fact]
    public void No_fuzzy_or_first_match_fallback_and_role_is_preserved()
    {
        var aliases = new[] { new SemanticTextSourceAlias("S1", "body/p1", 1, "Africa|Africa") };
        var fuzzy = SemanticTextDuplicateBinder.Bind([new("S1", "Afric", "CHAPTER")], aliases, out _, out _);
        Assert.Empty(fuzzy);
        var ambiguous = SemanticTextDuplicateBinder.Bind([new("S1", "Africa", "CHAPTER")], aliases, out _, out _);
        Assert.Empty(ambiguous);
        var unique = SemanticTextDuplicateBinder.Bind([new("S2", "Africa", "CHAPTER")], [new SemanticTextSourceAlias("S2", "body/p2", 2, "x Africa")], out _, out _);
        Assert.Equal("CHAPTER", Assert.Single(unique).Role);
    }

    [Fact]
    public void Duplicate_resolution_cannot_create_heading_absent_from_model()
    {
        var aliases = new[] { new SemanticTextSourceAlias("S1", "body/p1", 1, "Africa|Asia") };
        var bound = SemanticTextDuplicateBinder.Bind(Array.Empty<SemanticTextHeading>(), aliases, out var observations, out var traces);
        Assert.Empty(bound);
        Assert.Empty(observations);
        Assert.Empty(traces);
    }

    [Fact]
    public void Utf16_offsets_are_recomputed_exactly()
    {
        var aliases = new[] { new SemanticTextSourceAlias("S1", "body/p1", 1, "😀 Africa|Africa") };
        var bound = SemanticTextDuplicateBinder.Bind([new("S1", "Africa", "SECTION", null, "😀 ", null)], aliases, out _, out _);
        Assert.Equal((3, 9), (Assert.Single(bound).Start, Assert.Single(bound).End));
    }

    [Fact]
    public void Identical_table_and_body_text_stay_source_correct()
    {
        var aliases = new[]
        {
            new SemanticTextSourceAlias("S1", "body[1]/p[1]", 1, "Africa"),
            new SemanticTextSourceAlias("S2", "body[1]/tbl[1]/tr[1]/tc[1]/p[1]", 2, "Africa"),
        };
        var bound = SemanticTextDuplicateBinder.Bind([new("S2", "Africa", "SUBSECTION")], aliases, out _, out _);
        Assert.Equal("body[1]/tbl[1]/tr[1]/tc[1]/p[1]", Assert.Single(bound).SourceId);
    }

    [Fact]
    public void Gold_firewall_and_baseline_reuse_are_persisted()
    {
        var root = RepoRoot();
        using var freeze = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "eval/a99-closed-loop/semantic-text-generalization/DOC-0001/r1/freeze.v1.json")));
        Assert.False(freeze.RootElement.GetProperty("goldReadBeforeFreeze").GetBoolean());
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "eval/a99-closed-loop/semantic-text-duplicate-disambiguation/baseline-manifest.v1.json")));
        Assert.True(manifest.RootElement.GetProperty("baselineReused").GetBoolean());
        Assert.Equal(0, manifest.RootElement.GetProperty("baselineProviderCalls").GetInt32());
        Assert.True(manifest.RootElement.GetProperty("hashVerified").GetBoolean());
    }

    [Fact]
    public void Intervention_excludes_omission_review_and_records_regression()
    {
        var root = RepoRoot();
        using var summary = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "eval/a99-closed-loop/semantic-text-duplicate-disambiguation/summary.v1.json")));
        var json = summary.RootElement;
        Assert.Equal("REVERT_INTERVENTION", json.GetProperty("keepOrRevert").GetString());
        Assert.Equal("DUPLICATE_DISAMBIGUATION_PRECISION_REGRESSION", json.GetProperty("classification").GetString());
        Assert.Equal(0, json.GetProperty("resolvedDuplicates").GetInt32());
        Assert.Equal(0, json.GetProperty("incorrectlyResolvedDuplicateCount").GetInt32());
        Assert.DoesNotContain("omission-review", File.ReadAllText(Path.Combine(root, "eval/a99-closed-loop/semantic-text-duplicate-disambiguation/summary.v1.json")), StringComparison.OrdinalIgnoreCase);
    }

    private static string RepoRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
}
