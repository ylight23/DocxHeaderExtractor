using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The human-owned occurrence decision authority for DOC-0252.
///
/// The generated review views are disposable readings of the parser source universe; this authority
/// is the approved worksheet a person reviewed. An ordinary test run validates it but never
/// regenerates or overwrites it.
/// </summary>
public sealed class PdfGoldReviewDecisionAuthorityTests
{
    private const string Pack = "eval/a99-closed-loop/pdf-gold-doc0252";
    private const string SourceSha =
        "a005f25e3bb9754cd6c8c7000682d00eb68238fb8937d3475fe807ffbbd94b61";
    private const string SourceUniverseSha =
        "5dd617b27d7c1479f81fb41468076b5f6ba826a7d86cc9a04f6dfa0fa624c3ec";

    private static readonly string[] ExpectedHeadingAliases =
    [
        "S0001", "S0006", "S0043", "S0122", "S0127", "S0141", "S0173", "S0199",
        "S0223", "S0244", "S0268", "S0269", "S0363", "S0460", "S0492", "S0541",
        "S0573", "S0616", "S0649", "S0693", "S0732", "S0818", "S0893", "S0911",
        "S0912", "S0914", "S0917", "S0920", "S0925", "S0930", "S0933", "S0935",
        "S0946", "S0949", "S0950", "S0968", "S0987", "S0994",
    ];

    [Fact]
    public void Approved_decisions_cover_the_exact_source_universe_and_claim_contract()
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
        using var sourceDocument = Open("source-universe.v1.json");
        var source = sourceDocument.RootElement.GetProperty("rows").EnumerateArray().ToArray();

        Assert.Equal(1013, source.Length);
        Assert.Equal(source.Length, decisions.Length);
        Assert.Equal(
            source.Select(row => row.GetProperty("sourceAlias").GetString()),
            decisions.Select(row => row.GetProperty("sourceAlias").GetString()));

        Assert.Equal(ExpectedHeadingAliases,
            decisions.Where(row => row.GetProperty("humanDecision").GetString() == "HEADING")
                .Select(row => row.GetProperty("sourceAlias").GetString()));
        Assert.Equal(38, decisions.Count(row => row.GetProperty("humanDecision").GetString() == "HEADING"));
        Assert.Equal(975,
            decisions.Count(row => row.GetProperty("humanDecision").GetString() == "NOT_HEADING"));
        Assert.Equal(0,
            decisions.Count(row => row.GetProperty("humanDecision").GetString() == "NEEDS_REVIEW" ||
                                   row.GetProperty("humanDecision").ValueKind == JsonValueKind.Null));
        Assert.Equal(41, decisions.Sum(row => row.GetProperty("headingClaims").GetArrayLength()));

        Assert.All(decisions, decision =>
        {
            var properties = decision.EnumerateObject().Select(property => property.Name).ToArray();
            Assert.Equal(["sourceAlias", "humanDecision", "headingClaims"], properties);

            var humanDecision = decision.GetProperty("humanDecision").GetString();
            var claims = decision.GetProperty("headingClaims").EnumerateArray().ToArray();
            if (humanDecision == "HEADING")
                Assert.NotEmpty(claims);
            if (humanDecision == "NOT_HEADING")
                Assert.Empty(claims);

            Assert.All(claims, claim =>
            {
                Assert.Equal(
                    ["selectionMode", "verbatimText", "occurrence", "leftExactContext",
                        "rightExactContext", "semanticRole", "parentSourceAlias", "reviewNote"],
                    claim.EnumerateObject().Select(property => property.Name));
                Assert.NotEqual(JsonValueKind.Null, claim.GetProperty("selectionMode").ValueKind);
                Assert.NotEqual(JsonValueKind.Null, claim.GetProperty("semanticRole").ValueKind);
                Assert.Equal(JsonValueKind.Null, claim.GetProperty("parentSourceAlias").ValueKind);
            });
        });
    }

    [Fact]
    public void Fused_and_continuation_occurrences_remain_distinct_in_the_approved_payload()
    {
        using var authority = Open("review-decisions.v1.json");
        var rows = authority.RootElement.GetProperty("decisions").EnumerateArray()
            .ToDictionary(row => row.GetProperty("sourceAlias").GetString()!, StringComparer.Ordinal);

        Assert.Equal(2, rows["S0043"].GetProperty("headingClaims").GetArrayLength());
        Assert.Equal(2, rows["S0460"].GetProperty("headingClaims").GetArrayLength());
        Assert.Equal(2, rows["S0573"].GetProperty("headingClaims").GetArrayLength());
        Assert.Equal("HEADING", rows["S0616"].GetProperty("humanDecision").GetString());
        Assert.Equal("NOT_HEADING", rows["S0618"].GetProperty("humanDecision").GetString());
        Assert.Equal("HEADING", rows["S0930"].GetProperty("humanDecision").GetString());
        Assert.Equal("HEADING", rows["S0935"].GetProperty("humanDecision").GetString());
    }

    [Fact]
    public void The_authority_is_bound_to_the_committed_source_universe_hash()
    {
        using var source = Open("source-universe.v1.json");
        Assert.Equal(SourceUniverseSha, CanonicalArtifactHash.OfTextFile(SourcePath()));
        Assert.Equal(SourceSha, source.RootElement.GetProperty("sourceSha256").GetString());
    }

    private static JsonDocument Open(string name) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(TestRepository.Root(), Pack, name)));

    private static string SourcePath() =>
        Path.Combine(TestRepository.Root(), Pack, "source-universe.v1.json");

}
