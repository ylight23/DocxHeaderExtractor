using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

public sealed class A99IdentityPromotionBenchmarkV4FATests
{
    private const string ArtifactRoot = "artifacts/identity-benchmark/v4/provider-scale";
    private const string V4Root = "artifacts/identity-benchmark/v4/pruning-challenger";

    private static string Root() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
    private static JsonDocument Load(string relative) => JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), relative.Replace('/', Path.DirectorySeparatorChar))));

    [Fact]
    public void V4F_A_freezes_the_exact_7702_request_universe_without_execution()
    {
        using var manifest = Load(ArtifactRoot + "/manifest.json");
        var r = manifest.RootElement;
        Assert.Equal("OFFLINE_PROVIDER_SCALE_AUDIT_COMPLETE", r.GetProperty("status").GetString());
        Assert.Equal(7_702, r.GetProperty("requestCount").GetInt32());
        Assert.Equal("PASS", r.GetProperty("exactReconstruction").GetString());
        Assert.Equal("hdsa-canonical-pair-verifier-request-builder-v1", r.GetProperty("canonicalBuilderVersion").GetString());
        Assert.Equal("qwen/qwen3.7-flash", r.GetProperty("contextValidation").GetProperty("model").GetString());
        Assert.Equal("OpenRouter", r.GetProperty("contextValidation").GetProperty("provider").GetString());
        Assert.Equal(1_000_000, r.GetProperty("configuredContextLimitTokens").GetInt32());
        Assert.False(r.GetProperty("providerContextLimitVerified").GetBoolean());
        Assert.Equal("REQUEST_CONTEXT_DOMINANT", r.GetProperty("primaryDiagnosis").GetString());
    }

    [Fact]
    public void V4F_A_has_a_complete_firewall_and_does_not_consume_V4E_B()
    {
        using var firewall = Load(ArtifactRoot + "/firewall.json");
        var r = firewall.RootElement;
        Assert.Equal(0, r.GetProperty("goldReadCount").GetInt32());
        Assert.Equal(0, r.GetProperty("v4ebEvaluationReadCount").GetInt32());
        Assert.Equal(0, r.GetProperty("modelCalls").GetInt32());
        Assert.Equal(0, r.GetProperty("providerCalls").GetInt32());
        Assert.False(r.GetProperty("v4eBConsumed").GetBoolean());
        Assert.False(r.GetProperty("requestBodiesMutated").GetBoolean());
        Assert.False(r.GetProperty("requestBodiesArchived").GetBoolean());
        Assert.True(r.GetProperty("noProviderTransport").GetBoolean());
        Assert.Equal(7_702, r.GetProperty("frozenShortlistCount").GetInt32());
        Assert.Equal(7_702, r.GetProperty("frozenRequestCount").GetInt32());
    }

    [Fact]
    public void V4F_A_size_and_token_distributions_are_deterministic_and_complete()
    {
        using var sizes = Load(ArtifactRoot + "/request-size-distribution.json");
        var r = sizes.RootElement;
        Assert.Equal(7_702, r.GetProperty("requestCount").GetInt32());
        Assert.Equal(7_702, r.GetProperty("requestBytes").GetProperty("count").GetInt32());
        Assert.Equal(7_070_469_069L, r.GetProperty("requestBytes").GetProperty("total").GetInt64());
        Assert.True(r.GetProperty("requestBytes").GetProperty("min").GetInt64() > 0);
        Assert.True(r.GetProperty("requestBytes").GetProperty("max").GetInt64() >= r.GetProperty("requestBytes").GetProperty("p99").GetInt64());
        Assert.Equal(7_702, r.GetProperty("estimatedInputTokens").GetProperty("count").GetInt32());
        Assert.Equal("UTF8_BYTES_DIV_4_CEILING_V1", r.GetProperty("estimator").GetProperty("name").GetString());
        Assert.True(r.GetProperty("estimator").GetProperty("valuesAreEstimated").GetBoolean());
    }

    [Fact]
    public void V4F_A_payload_decomposition_sums_exactly_and_exposes_shared_context_redundancy()
    {
        using var payload = Load(ArtifactRoot + "/payload-decomposition.json");
        var r = payload.RootElement;
        var totals = r.GetProperty("totals");
        Assert.True(totals.GetProperty("exactSum").GetBoolean());
        Assert.Equal(7_070_469_069L, totals.GetProperty("totalBytes").GetInt64());
        Assert.Equal(totals.GetProperty("totalBytes").GetInt64(), totals.GetProperty("pairSpecificBytes").GetInt64() + totals.GetProperty("documentSharedContextBytes").GetInt64() + totals.GetProperty("otherBytes").GetInt64());
        var percentages = r.GetProperty("percentages");
        Assert.True(percentages.GetProperty("documentSharedContext").GetDouble() > .99);
        var unique = r.GetProperty("logicalVsUnique");
        Assert.Equal(3, unique.GetProperty("uniqueDocumentCount").GetInt32());
        Assert.True(unique.GetProperty("redundancyFactor").GetDouble() > 1.0);
        Assert.True(unique.GetProperty("repeatedContextBytes").GetInt64() > 0);
    }

    [Fact]
    public void V4F_A_call_count_and_document_counts_cover_the_frozen_shortlist()
    {
        using var calls = Load(ArtifactRoot + "/call-count-distribution.json");
        var r = calls.RootElement;
        Assert.Equal(7_702, r.GetProperty("totalCalls").GetInt32());
        Assert.Equal(7_702, r.GetProperty("callsPerDocument").EnumerateArray().Sum(x => x.GetProperty("calls").GetInt32()));
        Assert.Equal(3, r.GetProperty("callsPerDocument").GetArrayLength());
        Assert.True(r.GetProperty("candidateEndpointDegree").GetProperty("allDocuments").GetProperty("max").GetInt64() > 0);
        Assert.True(r.GetProperty("evidenceConfigurations").GetArrayLength() > 0);
        Assert.True(r.GetProperty("sourceOnly").GetBoolean());
    }

    [Fact]
    public void V4F_A_execution_cost_and_follow_on_gate_are_explicit()
    {
        using var execution = Load(ArtifactRoot + "/execution-scenarios.json");
        Assert.Equal(6, execution.RootElement.GetProperty("rates").GetArrayLength());
        Assert.True(execution.RootElement.GetProperty("noConcurrencyAssumption").GetBoolean());
        using var cost = Load(ArtifactRoot + "/cost-estimate.json");
        Assert.Equal("COST_UNKNOWN", cost.RootElement.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, cost.RootElement.GetProperty("pricing").ValueKind);
        using var manifest = Load(ArtifactRoot + "/manifest.json");
        Assert.Equal("READY_FOR_V4F_CONTEXT_PROJECTION_EXPERIMENT", manifest.RootElement.GetProperty("recommendedNextExperiment").GetString());
    }

    [Fact]
    public void V4F_A_does_not_change_the_frozen_V4E_input_hashes()
    {
        using var current = Load(ArtifactRoot + "/manifest.json");
        using var v4e = Load(V4Root + "/manifest.json");
        Assert.Equal(v4e.RootElement.GetProperty("shortlistSha256").GetString(), current.RootElement.GetProperty("shortlistSha256").GetString());
        Assert.Equal(v4e.RootElement.GetProperty("requestManifestSha256").GetString(), current.RootElement.GetProperty("requestManifestSha256").GetString());
        Assert.Equal(v4e.RootElement.GetProperty("sourceCatalogSha256").GetString(), current.RootElement.GetProperty("sourceCatalogSha256").GetString());
    }
}
