using System.Text.Json;
using System.Text.Json.Nodes;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfGoldOccurrenceAuthorityTests
{
    private const string Pack = "eval/a99-closed-loop/pdf-gold-doc0252";
    private const string Authority = Pack + "/review-decisions.v1.json";
    private const string SourceUniverse = Pack + "/source-universe.v1.json";
    private const string OccurrenceArtifact =
        "eval/a99-closed-loop/canonical-semantic-gold-vnext/occurrence/DOC-0252.occurrence-gold.v1.json";

    [Fact]
    public void Approved_authority_converts_without_using_the_41_total()
    {
        var loaded = LoadApproved();
        var converted = PdfGoldOccurrenceMaterializer.Convert(loaded);

        Assert.Equal(1013, loaded.Reviews.Count);
        Assert.Equal(38, loaded.Reviews.Count(row => row.HumanDecision == PdfGoldReview.Heading));
        Assert.Equal(975, loaded.Reviews.Count(row => row.HumanDecision == PdfGoldReview.NotHeading));
        Assert.Equal(0, loaded.Reviews.Count(row => row.HumanDecision == PdfGoldReview.NeedsReview));
        Assert.Equal(41, converted.Headings.Count);
        Assert.Null(converted.SemanticHeadingTotal);
    }

    [Fact]
    public void Frozen_occurrence_gold_is_bound_and_evaluable()
    {
        var loaded = LoadApproved();
        var gold = PdfGoldOccurrenceMaterializer.Freeze(loaded);

        Assert.Equal("DOC-0252", gold.DocumentId);
        Assert.Equal(PdfGoldOccurrenceAuthorityLoader.SourceSha256, gold.SourceSha256);
        Assert.Equal(41, gold.SemanticHeadingTotal);
        Assert.True(gold.Capabilities.OccurrenceEvaluable);
        Assert.Equal(PdfGoldOccurrenceAuthorityLoader.SourceUniverseSha256,
            gold.OccurrenceAuthority?.SourceUniverseSha256);
        Assert.Equal(0, gold.ProviderCalls);
        Assert.All(gold.Headings, heading => Assert.False(string.IsNullOrWhiteSpace(heading.SemanticRole)));
        Assert.All(gold.Headings, heading => Assert.Null(heading.ParentSourceAlias));

        FreezeArtifact.AssertJson(Pack.Replace("pdf-gold-doc0252", "canonical-semantic-gold-vnext/occurrence"),
            "DOC-0252.occurrence-gold.v1.json", ArtifactJson(gold, loaded.SourceUniverseSha256));
    }

    [Fact]
    public void Fused_and_continuation_invariants_survive_conversion()
    {
        var loaded = LoadApproved();
        var converted = PdfGoldOccurrenceMaterializer.Convert(loaded);
        var byAlias = loaded.Reviews.ToDictionary(row => row.SourceAlias, StringComparer.Ordinal);
        Assert.Equal(2, byAlias["S0043"].HeadingClaims.Count);
        Assert.Equal(2, byAlias["S0460"].HeadingClaims.Count);
        Assert.Equal(2, byAlias["S0573"].HeadingClaims.Count);
        Assert.Equal(PdfGoldReview.Heading, byAlias["S0616"].HumanDecision);
        Assert.Equal(PdfGoldReview.NotHeading, byAlias["S0618"].HumanDecision);
        Assert.All(loaded.Reviews.SelectMany(row => row.HeadingClaims),
            claim => Assert.Null(claim.ParentSourceAlias));

        var reviewedClaims = loaded.Reviews
            .Where(row => row.HumanDecision == PdfGoldReview.Heading)
            .SelectMany(row => row.HeadingClaims)
            .ToArray();
        Assert.Equal(reviewedClaims.Length, converted.Headings.Count);
        Assert.Equal(
            reviewedClaims.Select(claim => claim.SemanticRole),
            converted.Headings.Select(heading => heading.SemanticRole));
    }

    [Theory]
    [InlineData("duplicate", "authority-duplicate-source-alias")]
    [InlineData("missing", "authority-row-count-mismatch")]
    [InlineData("unknown", "authority-unknown-source-alias")]
    [InlineData("source-sha", "authority-source-sha-mismatch")]
    [InlineData("universe-sha", "authority-source-universe-sha-mismatch")]
    [InlineData("needs-review", "authority-needs-review-occurrence")]
    public void Authority_integrity_failures_are_rejected(string mutation, string expectedCode)
    {
        var authority = JsonNode.Parse(Read(Authority))!.AsObject();
        var decisions = authority["decisions"]!.AsArray();
        switch (mutation)
        {
            case "duplicate":
                decisions[1]! ["sourceAlias"] = "S0001";
                break;
            case "missing":
                decisions.RemoveAt(decisions.Count - 1);
                break;
            case "unknown":
                decisions[0]! ["sourceAlias"] = "S9999";
                break;
            case "source-sha":
                authority["sourceSha256"] = "bad";
                break;
            case "universe-sha":
                authority["sourceUniverseSha256"] = "bad";
                break;
            case "needs-review":
                decisions[0]! ["humanDecision"] = PdfGoldReview.NeedsReview;
                break;
        }

        var error = Assert.Throws<InvalidDataException>(() =>
            PdfGoldOccurrenceAuthorityLoader.Load(
                authority.ToJsonString(), Read(SourceUniverse)));
        Assert.Contains(expectedCode, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reconciliation_is_downstream_and_fails_without_repair()
    {
        var loaded = LoadApproved();
        var converted = PdfGoldOccurrenceMaterializer.Convert(loaded);
        Assert.Equal(41, converted.Headings.Count);
        Assert.Null(converted.SemanticHeadingTotal);
        Assert.Throws<InvalidDataException>(() =>
            Reconcile(converted.Headings.Count, 40));
    }

    [Fact]
    public void Frozen_artifact_is_deterministic()
    {
        var first = PdfGoldOccurrenceMaterializer.Freeze(LoadApproved());
        var second = PdfGoldOccurrenceMaterializer.Freeze(LoadApproved());
        Assert.Equal(JsonSerializer.Serialize(ArtifactJson(first,
                PdfGoldOccurrenceAuthorityLoader.SourceUniverseSha256), FreezeArtifact.Json),
            JsonSerializer.Serialize(ArtifactJson(second,
                PdfGoldOccurrenceAuthorityLoader.SourceUniverseSha256), FreezeArtifact.Json));
    }

    private static PdfGoldOccurrenceAuthorityDocument LoadApproved() =>
        PdfGoldOccurrenceAuthorityLoader.LoadFromFiles(
            Path(Authority), Path(SourceUniverse));

    private static object ArtifactJson(PdfGoldDocument gold, string sourceUniverseSha256) => new
    {
        artifactKind = gold.ArtifactKind,
        schemaVersion = gold.SchemaVersion,
        documentId = gold.DocumentId,
        sourceSha256 = gold.SourceSha256,
        sourceUniverseSha256,
        semanticHeadingTotal = gold.SemanticHeadingTotal,
        semanticHeadingTotalAuthority = gold.SemanticHeadingTotalAuthority,
        occurrenceAuthority = gold.OccurrenceAuthority,
        finalAuthority = gold.FinalAuthority,
        capabilities = gold.Capabilities,
        providerCalls = gold.ProviderCalls,
        headings = gold.Headings,
    };

    private static void Reconcile(int actual, int expected)
    {
        if (actual != expected)
            throw new InvalidDataException($"occurrence-gold-reconciliation-mismatch:{actual}:{expected}");
    }

    private static string Read(string relativePath) => File.ReadAllText(Path(relativePath));

    private static string Path(string relativePath) =>
        System.IO.Path.Combine(TestRepository.Root(), relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));

}
