using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Tests;

public sealed class ProviderSemanticExecutionFingerprintTests
{
    private static RemoteInferenceOptions Options(string apiKey = "secret-a") => new()
    {
        ApiKey = apiKey,
        Model = "qwen/qwen3.7-flash",
        OpenRouterProviderRoute = "Alibaba",
        RequireZeroDataRetention = false,
    };

    [Fact]
    public void Same_semantic_execution_config_has_same_hash()
    {
        var left = ProviderSemanticExecutionFingerprint.ForOpenRouterBoundary(Options(), 15456, "schema-sha");
        var right = ProviderSemanticExecutionFingerprint.ForOpenRouterBoundary(Options(), 15456, "schema-sha");

        Assert.Equal(left.Sha256, right.Sha256);
        Assert.Equal(left.CanonicalJson, right.CanonicalJson);
    }

    [Fact]
    public void Reasoning_mode_change_changes_hash()
    {
        var disabled = ProviderSemanticExecutionFingerprint.ForOpenRouterBoundary(Options(), 15456);
        var enabled = disabled with { ReasoningValue = "high" };

        Assert.NotEqual(disabled.Sha256, enabled.Sha256);
    }

    [Fact]
    public void Api_key_change_does_not_change_hash()
    {
        var left = ProviderSemanticExecutionFingerprint.ForOpenRouterBoundary(Options("secret-a"), 15456);
        var right = ProviderSemanticExecutionFingerprint.ForOpenRouterBoundary(Options("secret-b"), 15456);

        Assert.Equal(left.Sha256, right.Sha256);
        Assert.DoesNotContain("secret-a", left.CanonicalJson, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-b", right.CanonicalJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Response_schema_and_route_are_behavioral_inputs()
    {
        var baseline = ProviderSemanticExecutionFingerprint.ForOpenRouterBoundary(Options(), 15456, "schema-a");
        var schemaChanged = baseline with { ResponseSchemaSha256 = "schema-b" };
        var routeChanged = baseline with { ProviderRouting = "zdr=false;data_collection=deny;require_parameters=true;allow_fallbacks=false;route=OTHER" };

        Assert.NotEqual(baseline.Sha256, schemaChanged.Sha256);
        Assert.NotEqual(baseline.Sha256, routeChanged.Sha256);
    }
}
