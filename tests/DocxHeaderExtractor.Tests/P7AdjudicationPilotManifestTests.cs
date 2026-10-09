using System.Security.Cryptography;
using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

public sealed class P7AdjudicationPilotManifestTests
{
    private static byte[] Receipt() => File.ReadAllBytes(TestRepository.Path("artifacts/web-pdf-semantic-diagnostic/p7.d2.3.adjudication-pilot.v1.json"));
    [Fact] public void Limited_source_selection_receipt_is_immutable()
    {
        Assert.Equal("9ce761f0b631b58cc0c591649048e24c55b426e3b843565c5a3a5ecc39bc6ad4", Convert.ToHexStringLower(SHA256.HashData(Receipt())));
    }
    [Fact] public void Whole_page_pilot_has_explicit_source_coverage_not_model_candidate_filter()
    {
        using var doc = JsonDocument.Parse(Receipt()); var root = doc.RootElement;
        var pages = root.GetProperty("documents").EnumerateArray().SelectMany(d => d.GetProperty("pages").EnumerateArray()).ToArray();
        Assert.Equal(5, root.GetProperty("sourceDocuments").GetInt32()); Assert.Equal(6, pages.Length);
        Assert.Equal(213, pages.Sum(p => p.GetProperty("occurrences").GetInt32()));
        Assert.All(pages, p => { Assert.Equal(64, p.GetProperty("pageSourceSha256").GetString()!.Length); Assert.Equal(64, p.GetProperty("imageSha256").GetString()!.Length); });
        Assert.False(root.GetProperty("claimsHeldOut").GetBoolean()); Assert.False(root.GetProperty("gapsProvenAbsent").GetBoolean());
    }
    [Fact] public void Source_selection_and_synthetic_scorer_do_not_grant_gold_or_provider_authority()
    {
        using var doc = JsonDocument.Parse(Receipt()); var root = doc.RootElement;
        Assert.Equal(0, root.GetProperty("providerCalls").GetInt32()); Assert.Equal(0, root.GetProperty("reviewedMembershipUnits").GetInt32());
        Assert.Equal("NOT_FROZEN", root.GetProperty("goldManifest").GetString());
        Assert.Equal("NOT_FROZEN", root.GetProperty("treatmentRequestManifest").GetString());
        Assert.Equal("LOCKED", root.GetProperty("providerExecution").GetString());
        Assert.Equal("LOCKED", root.GetProperty("productionPromotion").GetString());
        Assert.False(root.GetProperty("modelResponsesRead").GetBoolean()); Assert.False(root.GetProperty("productionChanged").GetBoolean());
    }
}
