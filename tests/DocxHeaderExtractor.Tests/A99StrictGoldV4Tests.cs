using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

public sealed class A99StrictGoldV4Tests
{
    private static readonly string[] ActiveIds =
    [
        "DOC-0001", "DOC-0116", "DOC-0122", "DOC-0185", "DOC-0200",
        "DOC-0201", "DOC-0205", "DOC-0216", "DOC-0219", "DOC-0243",
        "DOC-0252", "DOC-0256", "DOC-0258", "DOC-0264", "DOC-0265",
    ];

    [Fact]
    public void All_active_documents_are_materialized_as_user_strict_gold()
    {
        var manifest = Load("eval/a99-closed-loop/strict-gold-manifest.v4.json");

        Assert.Equal(15, manifest.RootElement.GetProperty("activeStrictGoldDocumentCount").GetInt32());
        Assert.Equal(0, manifest.RootElement.GetProperty("activeStrictCohortPendingHumanReview").GetInt32());

        foreach (var id in ActiveIds)
        {
            var path = Path.Combine(Root(), "eval", "a99-closed-loop", "strict-gold-v4", $"{id}.strict-gold-v4.json");
            Assert.True(File.Exists(path), id);
            using var artifact = JsonDocument.Parse(File.ReadAllText(path));
            var root = artifact.RootElement;
            Assert.Equal("a99_canonical_strict_gold", root.GetProperty("artifactKind").GetString());
            Assert.Equal("a99-strict-gold-v4", root.GetProperty("schemaVersion").GetString());
            Assert.Equal("USER_PROMOTED_STRICT_GOLD_V4", root.GetProperty("policyVersion").GetString());
            Assert.Equal("STRICT_GOLD", root.GetProperty("goldStatus").GetString());
            Assert.Equal("USER", root.GetProperty("finalAuthority").GetString());
            Assert.True(root.GetProperty("userFinalApproval").GetBoolean());
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("sourceSha256").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("packetSha256").GetString()));
            Assert.Equal(JsonValueKind.Array, root.GetProperty("headings").ValueKind);
        }
    }

    [Theory]
    [InlineData("DOC-0001", 7)]
    [InlineData("DOC-0205", 71)]
    [InlineData("DOC-0252", 27)]
    [InlineData("DOC-0256", 24)]
    [InlineData("DOC-0258", 24)]
    [InlineData("DOC-0264", 158)]
    public void Approved_semantic_totals_are_preserved(string documentId, int expectedTotal)
    {
        using var artifact = Load($"eval/a99-closed-loop/strict-gold-v4/{documentId}.strict-gold-v4.json");
        Assert.Equal(expectedTotal, artifact.RootElement.GetProperty("semanticHeadingTotal").GetInt32());
    }

    [Theory]
    [InlineData("DOC-0001", "DETERMINISTIC")]
    [InlineData("DOC-0205", "HUMAN_ONLY")]
    [InlineData("DOC-0256", "SOURCE_STRUCTURAL")]
    [InlineData("DOC-0116", "HISTORICAL_REVIEWED_REFERENCE")]
    public void Historical_provenance_is_preserved_while_user_approval_controls_gold(string documentId, string provenance)
    {
        using var artifact = Load($"eval/a99-closed-loop/strict-gold-v4/{documentId}.strict-gold-v4.json");
        Assert.Equal(provenance, artifact.RootElement.GetProperty("referenceProvenance").GetString());
        Assert.Equal("STRICT_GOLD", artifact.RootElement.GetProperty("goldStatus").GetString());
        Assert.Equal("USER", artifact.RootElement.GetProperty("finalAuthority").GetString());
    }

    [Fact]
    public void Partial_coverage_remains_strict_gold_without_becoming_exhaustive()
    {
        foreach (var id in new[] { "DOC-0116", "DOC-0122", "DOC-0216", "DOC-0243", "DOC-0185", "DOC-0200", "DOC-0201", "DOC-0219", "DOC-0265" })
        {
            using var artifact = Load($"eval/a99-closed-loop/strict-gold-v4/{id}.strict-gold-v4.json");
            var root = artifact.RootElement;
            Assert.Equal("STRICT_GOLD", root.GetProperty("goldStatus").GetString());
            Assert.Equal("PARTIAL", root.GetProperty("coverage").GetString());
            Assert.False(root.GetProperty("headingSetExhaustive").GetBoolean());
        }
    }

    [Fact]
    public void Doc0264_preserves_approved_total_without_fabricating_heading_rows()
    {
        using var artifact = Load("eval/a99-closed-loop/strict-gold-v4/DOC-0264.strict-gold-v4.json");
        var root = artifact.RootElement;
        Assert.Equal(158, root.GetProperty("semanticHeadingTotal").GetInt32());
        Assert.Empty(root.GetProperty("headings").EnumerateArray());
        Assert.False(root.GetProperty("exactApprovedHeadingListMaterialized").GetBoolean());
        Assert.False(root.GetProperty("capabilities").GetProperty("occurrenceEvaluable").GetBoolean());
    }

    [Fact]
    public void Unsupported_occurrence_span_and_hierarchy_dimensions_stay_closed()
    {
        var matrix = Load("eval/a99-closed-loop/strict-gold-capability-matrix.v4.json").RootElement;

        Assert.Equal(0, matrix.GetProperty("activeOccurrenceEvaluable").GetInt32());
        Assert.Equal(0, matrix.GetProperty("activeCharacterSpanEvaluable").GetInt32());
        Assert.Equal(0, matrix.GetProperty("activeHierarchyEvaluable").GetInt32());
        Assert.False(matrix.GetProperty("baselineReady").GetBoolean());
    }

    [Fact]
    public void Doc0258_keeps_current_24_projection_and_historical_key_hash()
    {
        using var artifact = Load("eval/a99-closed-loop/strict-gold-v4/DOC-0258.strict-gold-v4.json");
        var root = artifact.RootElement;
        Assert.Equal(24, root.GetProperty("observedHeadingCount").GetInt32());
        Assert.Contains(root.GetProperty("referenceHashes").EnumerateObject(), property =>
            property.Name.EndsWith("078_ICP_IACG07_Minutes_May_2023.key", StringComparison.Ordinal));
    }

    [Fact]
    public void Outside_cohort_strict_gold_is_preserved()
    {
        var manifest = Load("eval/a99-closed-loop/strict-gold-manifest.v4.json").RootElement;
        var outside = manifest.GetProperty("globalStrictGoldOutsideActiveCohort").EnumerateArray()
            .ToDictionary(x => x.GetProperty("documentId").GetString()!, x => x);

        Assert.Equal(111, outside["DOC-0202"].GetProperty("semanticHeadingTotal").GetInt32());
        Assert.Equal(317, outside["DOC-0123"].GetProperty("semanticHeadingTotal").GetInt32());
    }

    [Fact]
    public void Promotion_is_provider_free_and_does_not_touch_holdout()
    {
        var policy = Load("eval/a99-closed-loop/strict-gold-authority-policy.v4.json").RootElement;
        var manifest = Load("eval/a99-closed-loop/strict-gold-manifest.v4.json").RootElement;

        Assert.True(policy.GetProperty("userPromotionApproval").GetBoolean());
        Assert.Equal(0, manifest.GetProperty("providerCalls").GetInt32());
        Assert.False(manifest.GetProperty("holdoutTouched").GetBoolean());
        Assert.Equal("NOT_RUN", manifest.GetProperty("baseline").GetString());
    }

    private static JsonDocument Load(string relativePath) => JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), relativePath)));

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DocxHeaderExtractor.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
