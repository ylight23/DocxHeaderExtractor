using System.Text.Json;
using DocxHeaderExtractor.Eval.StrictGoldOccurrence;

namespace DocxHeaderExtractor.Tests;

public sealed class StrictGoldOccurrenceMaterializerTests
{
    [Fact]
    public void Frozen_strict_gold_materializes_all_311_occurrences()
    {
        var report = StrictGoldOccurrenceMaterializer.MaterializeAll(FindRepositoryRoot(), out _);

        Assert.Equal("PASS", report.Status);
        Assert.Equal(6, report.ExpectedDocuments);
        Assert.Equal(6, report.MaterializedDocuments);
        Assert.Equal(311, report.ExpectedOccurrences);
        Assert.Equal(311, report.MaterializedOccurrences);
        Assert.All(report.PerDocument, item =>
        {
            Assert.Equal(item.Expected, item.Materialized);
            Assert.True(item.OccurrenceEvaluable);
            Assert.True(item.CharacterSpanEvaluable);
            Assert.Equal("PASS", item.Status);
        });
    }

    [Fact]
    public void Multiple_headings_can_share_one_source_id_but_have_distinct_occurrence_ids()
    {
        var report = StrictGoldOccurrenceMaterializer.MaterializeAll(FindRepositoryRoot(), out var artifacts);
        var legal = Assert.Single(artifacts, item => item.DocumentId == "DOC-0205");

        Assert.Equal(71, legal.Bindings.Count);
        Assert.Single(legal.Bindings.Select(item => item.SourceId).Distinct());
        Assert.Equal(71, legal.Bindings.Select(item => item.HeadingOccurrenceId).Distinct().Count());
        Assert.All(legal.Bindings, item => Assert.Contains(
            $"{item.SourceId}@{item.HeadingSpan.Start}:{item.HeadingSpan.End}",
            item.HeadingOccurrenceId,
            StringComparison.Ordinal));
        Assert.Equal("PASS", report.Status);
    }

    [Fact]
    public void Exact_substring_binding_returns_raw_source_coordinates()
    {
        var used = new HashSet<StrictGoldOccurrenceSpan>();

        Assert.True(StrictGoldOccurrenceMaterializer.TryBindExactSubstring(
            "prefix Figure 3 caption suffix",
            "Figure 3 caption",
            used,
            0,
            out var span,
            out var method,
            out var rule));

        Assert.Equal(new StrictGoldOccurrenceSpan(7, 23), span);
        Assert.Equal("exact-raw-text-substring", method);
        Assert.Equal("ordinal", rule);
    }

    [Fact]
    public void Duplicate_text_uses_consumed_spans_for_one_to_one_document_order_binding()
    {
        var used = new HashSet<StrictGoldOccurrenceSpan>();
        const string raw = "Title body Title";

        Assert.True(StrictGoldOccurrenceMaterializer.TryBindExactSubstring(raw, "Title", used, 0,
            out var first, out _, out _));
        used.Add(first);
        Assert.True(StrictGoldOccurrenceMaterializer.TryBindExactSubstring(raw, "Title", used, first.End,
            out var second, out _, out _));

        Assert.Equal(new StrictGoldOccurrenceSpan(0, 5), first);
        Assert.Equal(new StrictGoldOccurrenceSpan(11, 16), second);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Ambiguous_duplicate_without_a_discriminator_fails_closed()
    {
        var bound = StrictGoldOccurrenceMaterializer.TryBindUniqueExactSubstring(
            "Title body Title",
            "Title",
            out var span,
            out var method,
            out _);

        Assert.False(bound);
        Assert.Equal(new StrictGoldOccurrenceSpan(0, 0), span);
        Assert.Equal("ambiguous-or-unbound", method);
    }

    [Fact]
    public void Normalized_binding_maps_back_to_the_original_raw_span()
    {
        var bound = StrictGoldOccurrenceMaterializer.TryBindExactSubstring(
            "prefix 1.Global office update suffix",
            "1. Global office update",
            new HashSet<StrictGoldOccurrenceSpan>(),
            0,
            out var span,
            out var method,
            out var rule);

        Assert.True(bound);
        Assert.Equal(new StrictGoldOccurrenceSpan(7, 29), span);
        Assert.Equal("reversible-normalized-text-map", method);
        Assert.Contains("reversible-offset-map", rule, StringComparison.Ordinal);
        Assert.Equal("1.Global office update", "prefix 1.Global office update suffix"[span.Start..span.End]);
    }

    [Fact]
    public void Invalid_or_missing_text_never_materializes_a_span()
    {
        var bound = StrictGoldOccurrenceMaterializer.TryBindExactSubstring(
            "source",
            "missing",
            new HashSet<StrictGoldOccurrenceSpan>(),
            0,
            out var span,
            out _,
            out _);

        Assert.False(bound);
        Assert.Equal(new StrictGoldOccurrenceSpan(0, 0), span);
    }

    [Fact]
    public void Every_materialized_span_is_within_its_raw_source_text()
    {
        var report = StrictGoldOccurrenceMaterializer.MaterializeAll(FindRepositoryRoot(), out var artifacts);

        Assert.Equal("PASS", report.Status);
        foreach (var binding in artifacts.SelectMany(item => item.Bindings))
        {
            Assert.InRange(binding.HeadingSpan.Start, 0, binding.RawSourceText.Length == 0 ? 0 : int.MaxValue);
            Assert.True(binding.HeadingSpan.End > binding.HeadingSpan.Start);
            Assert.Equal(binding.HeadingSpan.End - binding.HeadingSpan.Start, binding.RawSourceText.Length);
            Assert.True(binding.ExactRawSubstringVerified);
        }
    }

    [Theory]
    [InlineData("DOC-0001", 7)]
    [InlineData("DOC-0205", 71)]
    [InlineData("DOC-0252", 27)]
    [InlineData("DOC-0256", 24)]
    [InlineData("DOC-0258", 24)]
    [InlineData("DOC-0264", 158)]
    public void Artifact_counts_match_the_frozen_semantic_totals(string documentId, int expected)
    {
        using var artifact = LoadArtifact(documentId);
        var root = artifact.RootElement;

        Assert.Equal("PASS", root.GetProperty("status").GetString());
        Assert.Equal(expected, root.GetProperty("semanticHeadingTotal").GetInt32());
        Assert.Equal(expected, root.GetProperty("materializedOccurrenceCount").GetInt32());
        Assert.Equal(expected, root.GetProperty("bindings").GetArrayLength());
    }

    [Fact]
    public void DOC_0258_keeps_the_current_24_heading_projection_not_the_historical_27_key()
    {
        using var artifact = LoadArtifact("DOC-0258");
        Assert.Equal(24, artifact.RootElement.GetProperty("materializedOccurrenceCount").GetInt32());
        Assert.DoesNotContain(
            artifact.RootElement.GetProperty("bindings").EnumerateArray(),
            item => item.GetProperty("rawSourceText").GetString()?.StartsWith("DAY ", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void DOC_0264_uses_the_approved_158_marker_set_and_excludes_the_order_preamble()
    {
        using var artifact = LoadArtifact("DOC-0264");
        var bindings = artifact.RootElement.GetProperty("bindings").EnumerateArray().ToArray();

        Assert.Equal(158, bindings.Length);
        Assert.Equal(1, bindings.Count(item => item.GetProperty("semanticRole").GetString() == "title"));
        Assert.Equal(10, bindings.Count(item => item.GetProperty("semanticRole").GetString() == "chapter"));
        Assert.Equal(12, bindings.Count(item => item.GetProperty("semanticRole").GetString() == "section"));
        Assert.Equal(135, bindings.Count(item => item.GetProperty("semanticRole").GetString() == "article"));
        Assert.DoesNotContain(bindings, item =>
            item.GetProperty("rawSourceText").GetString()?.Contains(
                "Order On the promulgation of law", StringComparison.Ordinal) == true);
    }

    private static JsonDocument LoadArtifact(string documentId) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "eval", "a99-closed-loop", "strict-gold-occurrence-v1",
            $"{documentId}.occurrence-gold-v1.json")));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DocxHeaderExtractor.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
