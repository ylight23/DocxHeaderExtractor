using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Tests;

public sealed class HdsaLiveRunManifestGuardTests
{
    [Fact]
    public void Identical_prepared_manifest_verifies_and_allows_one_call()
    {
        var bodies = Bodies();
        var manifest = HdsaLiveRunManifestGuard.Prepare(
            "run-1", "source", "catalog", "resolver-v4", "contract-v1", bodies);

        var verification = HdsaLiveRunManifestGuard.Verify(
            manifest, "source", "catalog", "resolver-v4", "contract-v1", bodies);

        Assert.True(verification.Accepted);
        Assert.Equal(1, verification.ProviderCallsAllowed);
        Assert.Null(verification.RejectionReason);
        Assert.Equal(
            HdsaLiveRunManifestGuard.Serialize(manifest),
            HdsaLiveRunManifestGuard.Serialize(manifest));
    }

    [Theory]
    [InlineData("source-x", "catalog", "resolver-v4", "contract-v1")]
    [InlineData("source", "catalog-x", "resolver-v4", "contract-v1")]
    [InlineData("source", "catalog", "resolver-x", "contract-v1")]
    [InlineData("source", "catalog", "resolver-v4", "contract-x")]
    public void Identity_mismatch_fails_before_a_provider_call(
        string source, string catalog, string resolver, string contract)
    {
        var manifest = HdsaLiveRunManifestGuard.Prepare(
            "run-1", "source", "catalog", "resolver-v4", "contract-v1", Bodies());
        var verification = HdsaLiveRunManifestGuard.Verify(
            manifest, source, catalog, resolver, contract, Bodies());

        var providerCalls = verification.Accepted ? 1 : 0;
        Assert.False(verification.Accepted);
        Assert.Equal(0, verification.ProviderCallsAllowed);
        Assert.Equal(0, providerCalls);
        Assert.NotNull(verification.RejectionReason);
    }

    [Fact]
    public void Request_mutation_fails_before_a_provider_call()
    {
        var manifest = HdsaLiveRunManifestGuard.Prepare(
            "run-1", "source", "catalog", "resolver-v4", "contract-v1", Bodies());
        var mutated = new Dictionary<string, string>(Bodies()) { ["parent-1"] = "{\"x\":2}" };

        var verification = HdsaLiveRunManifestGuard.Verify(
            manifest, "source", "catalog", "resolver-v4", "contract-v1", mutated);

        Assert.False(verification.Accepted);
        Assert.Equal(0, verification.ProviderCallsAllowed);
        Assert.Equal("REQUEST_FINGERPRINT_MISMATCH", verification.RejectionReason);
    }

    [Fact]
    public void Unsafe_manifest_cannot_be_serialized_or_executed()
    {
        var unsafeManifest = new HdsaLiveRunManifest(
            "a99-live-run-manifest-v1", "run-1", "source", "catalog", "resolver-v4", "contract-v1",
            new Dictionary<string, string>(), true, false, false);

        Assert.False(unsafeManifest.IsProductionSafe);
        Assert.Throws<InvalidDataException>(() => HdsaLiveRunManifestGuard.Serialize(unsafeManifest));
        var verification = HdsaLiveRunManifestGuard.Verify(
            unsafeManifest, "source", "catalog", "resolver-v4", "contract-v1", new Dictionary<string, string>());
        Assert.False(verification.Accepted);
        Assert.Equal(0, verification.ProviderCallsAllowed);
    }

    [Fact]
    public void Serialized_manifest_contains_only_firewall_safe_state()
    {
        var manifest = HdsaLiveRunManifestGuard.Prepare(
            "run-1", "source", "catalog", "resolver-v4", "contract-v1", Bodies());
        var json = HdsaLiveRunManifestGuard.Serialize(manifest);

        Assert.Contains("\"goldReadBeforeFreeze\":false", json, StringComparison.Ordinal);
        Assert.Contains("\"goldDerivedInput\":false", json, StringComparison.Ordinal);
        Assert.Contains("\"legacyHierarchyConsumed\":false", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorization", json, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, string> Bodies() =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["parent-1"] = "{\"x\":1}",
            ["parent-2"] = "{\"x\":2}",
        };
}
