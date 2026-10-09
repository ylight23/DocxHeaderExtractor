using System.Globalization;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;
using DocxHeaderExtractor.V5Qualification.P7;

namespace DocxHeaderExtractor.Tests;

public sealed class P7SpatialEvidenceLedgerTests
{
    [Fact]
    public void Ledger_is_deterministic_under_source_parser_and_query_enumeration_permutations()
    {
        var source = Fixture();
        var queries = Queries(source);
        var first = PdfSpatialEvidenceLedgerBuilder.Build(source.Snapshot, source.Details, queries);
        var reversed = PdfSpatialEvidenceLedgerBuilder.Build(source.Snapshot with { Atoms = source.Snapshot.Atoms.Reverse().ToArray() },
            source.Details with { Blocks = source.Details.Blocks.Reverse().ToArray() },
            queries.Reverse().Select(query => query with { Subjects = query.Subjects.Reverse().ToArray() }).ToArray());
        Assert.Equal(first.CanonicalBytes(), reversed.CanonicalBytes());
        Assert.Equal(first.LedgerSha256, reversed.LedgerSha256);
        Assert.All(first.Facts, fact => Assert.StartsWith("SPATIAL-", fact.FactId));
        Assert.Equal(first.Facts.Count, first.Facts.Select(fact => fact.FactId).Distinct().Count());
    }

    [Fact]
    public void Same_row_and_distinct_regions_are_verifiable_geometry_not_semantic_authority()
    {
        var source = Fixture();
        var subjects = source.Snapshot.Atoms.Take(3).Select(atom => atom.Alias).ToArray();
        var ledger = PdfSpatialEvidenceLedgerBuilder.Build(source.Snapshot, source.Details, Queries(source));
        var row = ledger.Facts.Single(fact => fact.Type == SpatialFactTypes.SameRow);
        var regions = ledger.Facts.Single(fact => fact.Type == SpatialFactTypes.DistinctHorizontalRegions);
        Assert.True(row.Value!.Value.GetBoolean());
        Assert.True(regions.Value!.Value.GetBoolean());
        Assert.Contains("GEOMETRIC_ROW_PROXY", row.Provenance.Rule);
        Assert.All(ledger.Facts, fact => Assert.Equal(PdfSpatialEvidenceLedgerBuilder.GeometryBasis, fact.Provenance.Basis));
        Assert.Empty(PdfSpatialEvidenceValidator.Validate(source.Snapshot, source.Details, ledger,
            subjects.ToHashSet(StringComparer.Ordinal), [new(row.FactId, subjects), new(regions.FactId, subjects)] , Queries(source)));
        // It is evidence, not a heading/table decision contract. No semantic labels or authority outputs.
        var names = typeof(PdfSpatialEvidenceFact).GetProperties().Select(property => property.Name).ToArray();
        Assert.DoesNotContain("SemanticRole", names);
        Assert.DoesNotContain("HeadingMembers", names);
        Assert.DoesNotContain("Decision", names);
    }

    [Fact]
    public void Default_inventory_is_linear_gold_free_and_allows_multiple_relations_per_occurrence()
    {
        var source = Fixture();
        var ledger = PdfSpatialEvidenceLedgerBuilder.Build(source.Snapshot, source.Details);
        Assert.Equal(4 + 3 * (3 + 2), ledger.Facts.Count);
        Assert.True(ledger.Facts.Count(fact => fact.Subjects.Contains(source.Snapshot.Atoms[1].Alias)) > 1);
        Assert.Equal(source.Snapshot.Atoms.Select(atom => atom.Alias), ledger.Observations.Select(observation => observation.Alias));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("zero-height")]
    [InlineData("negative-height")]
    [InlineData("nan")]
    [InlineData("infinity")]
    public void Missing_or_invalid_native_bounds_are_not_false_and_font_size_does_not_fill_them(string corruption)
    {
        var source = Fixture();
        var block = source.Details.Blocks[0];
        var line = block.Lines[0];
        line = corruption switch
        {
            "missing" => line with { Top = null, Bottom = null },
            "zero-height" => line with { Top = line.Bottom },
            "negative-height" => line with { Top = line.Bottom - 1 },
            "nan" => line with { Top = double.NaN },
            _ => line with { Top = double.PositiveInfinity },
        };
        var details = source.Details with { Blocks = source.Details.Blocks.Select(value => value.Id == block.Id
            ? value with { Lines = [line] } : value).ToArray() };
        var ledger = PdfSpatialEvidenceLedgerBuilder.Build(source.Snapshot, details, Queries(source));
        var row = ledger.Facts.Single(fact => fact.Type == SpatialFactTypes.SameRow);
        Assert.Equal("NOT_AVAILABLE", row.Availability);
        Assert.Null(row.Value);
        Assert.NotNull(row.MissingReason);
        Assert.Equal("NO_VERIFIABLE_MEASUREMENT", row.VerificationStatus);
        var issues = PdfSpatialEvidenceValidator.Validate(source.Snapshot, details, ledger,
            row.Subjects.ToHashSet(StringComparer.Ordinal), [new(row.FactId, row.Subjects, SpatialCanonical.Element(false))], Queries(source));
        Assert.Contains(issues, issue => issue.Code == "FACT_NOT_AVAILABLE");
        Assert.Contains(issues, issue => issue.Code == "FACT_VALUE_MISMATCH");
        Assert.Equal("OBSERVED", ledger.Facts.Single(fact => fact.Type == SpatialFactTypes.SamePage).Availability);
    }

    [Fact]
    public void Parser_context_degenerate_y_is_not_used_instead_of_glyph_bounds()
    {
        var source = Fixture();
        Assert.All(source.Details.Contexts.Values, context => Assert.Equal(context.Source.TopY, context.Source.BottomY));
        var ledger = PdfSpatialEvidenceLedgerBuilder.Build(source.Snapshot, source.Details);
        Assert.All(ledger.Observations, observation => Assert.True(observation.Bounds!.Top > observation.Bounds.Bottom));
    }

    [Fact]
    public void Pairwise_vertical_overlap_does_not_imply_three_subject_common_row()
    {
        var source = PdfSourceAdapter.BuildWithDetails([
            Line(1, 0, 10, 10, 20, "A"), Line(1, 20, 30, 15, 25, "B"), Line(1, 40, 50, 20, 30, "C")], new string('a', 64));
        var aliases = source.Snapshot.Atoms.Select(atom => atom.Alias).ToArray();
        var queries = new[] { new SpatialRelationQuery(SpatialFactTypes.SameRow, aliases[..2]),
            new SpatialRelationQuery(SpatialFactTypes.SameRow, aliases[1..]), new SpatialRelationQuery(SpatialFactTypes.SameRow, aliases) };
        var ledger = PdfSpatialEvidenceLedgerBuilder.Build(source.Snapshot, source.Details, queries);
        Assert.Equal(2, ledger.Facts.Count(fact => fact.Type == SpatialFactTypes.SameRow && fact.Value!.Value.GetBoolean()));
        Assert.False(ledger.Facts.Single(fact => fact.Subjects.Count == 3).Value!.Value.GetBoolean());
    }

    [Fact]
    public void Cross_page_relations_and_touching_horizontal_intervals_follow_explicit_rules()
    {
        var source = PdfSourceAdapter.BuildWithDetails([
            Line(1, 0, 10, 10, 20, "A"), Line(1, 10, 20, 10, 20, "B"), Line(2, 20, 30, 10, 20, "C")], new string('a', 64));
        var ledger = PdfSpatialEvidenceLedgerBuilder.Build(source.Snapshot, source.Details);
        Assert.True(ledger.Facts.Single(fact => fact.Type == SpatialFactTypes.DistinctHorizontalRegions &&
            fact.Subjects.SequenceEqual(source.Snapshot.Atoms.Take(2).Select(atom => atom.Alias))).Value!.Value.GetBoolean());
        Assert.All(ledger.Facts.Where(fact => fact.Subjects.Count == 3), fact => Assert.False(fact.Value!.Value.GetBoolean()));
    }

    [Theory]
    [InlineData("unknown", "UNKNOWN_FACT_REFERENCE")]
    [InlineData("subjects", "FACT_SUBJECTS_MISMATCH")]
    [InlineData("value", "FACT_VALUE_MISMATCH")]
    [InlineData("scope", "FACT_OUTSIDE_ISSUED_SUBJECTS")]
    [InlineData("duplicate", "DUPLICATE_FACT_REFERENCE")]
    public void Forged_references_assertions_and_out_of_scope_evidence_are_rejected(string corruption, string code)
    {
        var source = Fixture();
        var queries = Queries(source);
        var ledger = PdfSpatialEvidenceLedgerBuilder.Build(source.Snapshot, source.Details, queries);
        var fact = ledger.Facts.Single(value => value.Type == SpatialFactTypes.SameRow);
        var reference = new SpatialEvidenceReference(fact.FactId, fact.Subjects);
        var issued = ledger.Observations.Select(observation => observation.Alias).ToHashSet(StringComparer.Ordinal);
        var references = new[] { reference };
        switch (corruption)
        {
            case "unknown": references = [reference with { FactId = "SPATIAL-invented" }]; break;
            case "subjects": references = [reference with { Subjects = ["invented"] }]; break;
            case "value": references = [reference with { AssertedValue = SpatialCanonical.Element(false) }]; break;
            case "scope": issued.Remove(fact.Subjects[0]); break;
            case "duplicate": references = [reference, reference]; break;
        }
        Assert.Contains(PdfSpatialEvidenceValidator.Validate(source.Snapshot, source.Details, ledger, issued, references, queries), issue => issue.Code == code);
    }

    [Fact]
    public void Ledger_tampering_and_stale_document_identity_cannot_self_certify()
    {
        var source = Fixture();
        var queries = Queries(source);
        var ledger = PdfSpatialEvidenceLedgerBuilder.Build(source.Snapshot, source.Details, queries);
        var forged = new PdfSpatialEvidenceLedger(ledger.SourceSha256, ledger.SourceAliasUniverseSha256,
            ledger.ModelVisibleEvidenceSha256, ledger.Observations, ledger.Facts.Select(fact =>
                fact.Type == SpatialFactTypes.SameRow ? fact with { Value = SpatialCanonical.Element(false) } : fact));
        var issued = ledger.Observations.Select(observation => observation.Alias).ToHashSet(StringComparer.Ordinal);
        Assert.Single(PdfSpatialEvidenceValidator.Validate(source.Snapshot, source.Details, forged, issued, [], queries),
            issue => issue.Code == "LEDGER_DOES_NOT_MATCH_PARSER_SNAPSHOT");
        Assert.Single(PdfSpatialEvidenceValidator.Validate(source.Snapshot with { SourceSha256 = new string('b', 64) },
            source.Details, ledger, issued, [], queries), issue => issue.Code == "LEDGER_DOES_NOT_MATCH_PARSER_SNAPSHOT");
    }

    [Fact]
    public void Unknown_duplicate_or_semantic_query_types_are_rejected()
    {
        var source = Fixture();
        var query = Queries(source)[0];
        foreach (var queries in new[] { new[] { query, query }, new[] { query with { Subjects = ["invented", "unknown"] } },
            new[] { query with { Type = "SAME_HEADING" } }, new[] { query with { Subjects = [query.Subjects[0], query.Subjects[0]] } } })
            Assert.Throws<InvalidOperationException>(() => PdfSpatialEvidenceLedgerBuilder.Build(source.Snapshot, source.Details, queries));
    }

    [Fact]
    public void Observation_and_subject_collections_are_read_only_and_no_source_text_is_published()
    {
        var source = Fixture();
        var ledger = PdfSpatialEvidenceLedgerBuilder.Build(source.Snapshot, source.Details);
        Assert.Throws<NotSupportedException>(() => ((IList<SpatialObservation>)ledger.Observations).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<string>)ledger.Facts[0].Subjects).Clear());
        Assert.DoesNotContain("Private cell A", System.Text.Encoding.UTF8.GetString(ledger.CanonicalBytes()));
        using var json = JsonDocument.Parse(ledger.CanonicalBytes());
        Assert.Equal(PdfSpatialEvidenceLedger.Version, json.RootElement.GetProperty("protocolVersion").GetString());
    }

    [Fact]
    public void Invariant_serialization_and_build_do_not_change_source_or_production_requests()
    {
        var source = Fixture();
        var originalSource = JsonSerializer.Serialize(source.Snapshot.Atoms);
        var request = OccurrenceFunctionProtocolV1.ComposeWithReadOnlyCorrespondences(source.Snapshot.Atoms, [],
            new Dictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>>());
        var first = PdfSpatialEvidenceLedgerBuilder.Build(source.Snapshot, source.Details);
        var oldCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.Equal(first.CanonicalBytes(), PdfSpatialEvidenceLedgerBuilder.Build(source.Snapshot, source.Details).CanonicalBytes());
        }
        finally { CultureInfo.CurrentCulture = oldCulture; }
        Assert.Equal(originalSource, JsonSerializer.Serialize(source.Snapshot.Atoms));
        Assert.Equal(request.UserMessage, OccurrenceFunctionProtocolV1.ComposeWithReadOnlyCorrespondences(source.Snapshot.Atoms, [],
            new Dictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>>()).UserMessage);
    }

    private static PdfSourceBuildResult Fixture() => PdfSourceAdapter.BuildWithDetails([
        Line(1, 0, 10, 10, 20, "Private cell A"), Line(1, 20, 30, 10, 20, "Private cell B"),
        Line(1, 40, 50, 10, 20, "Private cell C"), Line(1, 0, 50, 0, 8, "Private body")], new string('a', 64));

    private static PdfLine Line(int page, double left, double right, double bottom, double top, string text) =>
        new(page, (bottom + top) / 2, 12, text, 1, "", 0, left, right, "Times-Bold", "", Bottom: bottom, Top: top);

    private static IReadOnlyList<SpatialRelationQuery> Queries(PdfSourceBuildResult source) => new[]
    {
        new SpatialRelationQuery(SpatialFactTypes.SameRow, source.Snapshot.Atoms.Take(3).Select(atom => atom.Alias).ToArray()),
        new SpatialRelationQuery(SpatialFactTypes.DistinctHorizontalRegions, source.Snapshot.Atoms.Take(3).Select(atom => atom.Alias).ToArray()),
        new SpatialRelationQuery(SpatialFactTypes.SamePage, source.Snapshot.Atoms.Take(3).Select(atom => atom.Alias).ToArray()),
    };
}
