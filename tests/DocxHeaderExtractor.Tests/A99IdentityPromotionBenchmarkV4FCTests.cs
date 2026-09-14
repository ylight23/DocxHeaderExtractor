using System.Security.Cryptography;
using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

public sealed class A99IdentityPromotionBenchmarkV4FCTests
{
    private const string ArtifactRoot = "artifacts/identity-benchmark/v4/projected-requests";
    private const string V4Root = "artifacts/identity-benchmark/v4/pruning-challenger";

    private static string Root() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
    private static JsonDocument Load(string relative) => JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), relative.Replace('/', Path.DirectorySeparatorChar))));
    private static string Sha256(string relative) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(Root(), relative.Replace('/', Path.DirectorySeparatorChar))))).ToLowerInvariant();

    [Fact]
    public void V4F_C_freezes_the_same_candidates_with_zero_execution()
    {
        using var manifest = Load(ArtifactRoot + "/manifest.json");
        var m = manifest.RootElement;
        Assert.Equal("READY_FOR_PROJECTED_VERIFIER_EXPERIMENT_DESIGN", m.GetProperty("status").GetString());
        Assert.Equal(7_702, m.GetProperty("candidateCount").GetInt32());
        Assert.Equal(7_702, m.GetProperty("projectedRequestCount").GetInt32());
        Assert.False(m.GetProperty("candidateUniverseChanged").GetBoolean());
        Assert.False(m.GetProperty("rankingChanged").GetBoolean());
        Assert.False(m.GetProperty("providerExecuted").GetBoolean());
        Assert.Equal(0, m.GetProperty("goldReadCount").GetInt32());
        Assert.Equal(0, m.GetProperty("v4ebEvaluationReadCount").GetInt32());
        Assert.Equal(0, m.GetProperty("providerCalls").GetInt32());
        Assert.Equal(0, m.GetProperty("modelCalls").GetInt32());
    }

    [Fact]
    public void V4F_C_request_manifest_is_hash_only_and_reconstructible()
    {
        using var manifest = Load(ArtifactRoot + "/request-manifest.json");
        var m = manifest.RootElement;
        Assert.Equal(7_702, m.GetProperty("requestCount").GetInt32());
        Assert.Equal(Sha256("artifacts/identity-benchmark/v4/context-projection/packet-manifest.json"), m.GetProperty("packetManifestSha256").GetString());
        Assert.True(m.GetProperty("packetManifestConsumedAsFrozen").GetBoolean());
        Assert.False(m.GetProperty("packetBodiesPersisted").GetBoolean());
        Assert.False(m.GetProperty("exactBytesPersisted").GetBoolean());
        Assert.True(m.GetProperty("exactBytesDeterministicallyReconstructible").GetBoolean());
        Assert.Equal("candidateId ASC", m.GetProperty("canonicalOrdering").GetString());
        var entries = m.GetProperty("requests").EnumerateArray().ToArray();
        Assert.Equal(7_702, entries.Length);
        Assert.Equal(7_702, entries.Select(x => x.GetProperty("candidateId").GetString()).Distinct(StringComparer.Ordinal).Count());
        Assert.All(entries, x =>
        {
            Assert.Equal(64, x.GetProperty("packetSha256").GetString()!.Length);
            Assert.Equal(64, x.GetProperty("requestSha256").GetString()!.Length);
            Assert.True(x.GetProperty("exactByteLength").GetInt32() > 0);
            Assert.Equal("hdsa-canonical-projected-pair-verifier-request-builder-v1", x.GetProperty("builderVersion").GetString());
        });
    }

    [Fact]
    public void V4F_C_preserves_the_frozen_candidate_identity_set()
    {
        using var shortlist = Load(V4Root + "/shortlist.json");
        using var requests = Load(ArtifactRoot + "/request-manifest.json");
        var expected = shortlist.RootElement.GetProperty("candidates").EnumerateArray().Select(x => x.GetProperty("pairId").GetString()!).ToHashSet(StringComparer.Ordinal);
        var actual = requests.RootElement.GetProperty("requests").EnumerateArray().Select(x => x.GetProperty("candidateId").GetString()!).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void V4F_C_materially_reduces_context_without_adding_evidence()
    {
        using var sizes = Load(ArtifactRoot + "/size-distribution.json");
        using var payload = Load(ArtifactRoot + "/payload-decomposition.json");
        var comparison = sizes.RootElement.GetProperty("comparison");
        Assert.Equal(7_070_469_069L, comparison.GetProperty("oldTotalBytes").GetInt64());
        Assert.Equal(64_990_323L, comparison.GetProperty("projectedTotalBytes").GetInt64());
        Assert.True(comparison.GetProperty("reductionPercentage").GetDouble() > .99);
        Assert.True(comparison.GetProperty("compressionRatio").GetDouble() > 100);
        var totals = payload.RootElement.GetProperty("totals");
        Assert.True(totals.GetProperty("exactSum").GetBoolean());
        Assert.Equal(64_990_323L, totals.GetProperty("totalBytes").GetInt64());
        Assert.Equal(0L, totals.GetProperty("fullDocumentContextBytes").GetInt64());
    }

    [Fact]
    public void V4F_C_requests_are_within_configured_limit_but_not_provider_verified()
    {
        using var context = Load(ArtifactRoot + "/context-limit-analysis.json");
        var c = context.RootElement;
        Assert.Equal(1_000_000, c.GetProperty("configuredContextLimitTokens").GetInt32());
        Assert.False(c.GetProperty("providerVerified").GetBoolean());
        Assert.Equal(7_702, c.GetProperty("classification").GetProperty("safe").GetProperty("count").GetInt32());
        Assert.Equal(0, c.GetProperty("classification").GetProperty("near").GetProperty("count").GetInt32());
        Assert.Equal(0, c.GetProperty("classification").GetProperty("exceeds").GetProperty("count").GetInt32());
    }

    [Fact]
    public void V4F_C_keeps_the_existing_decision_contract_and_changes_only_evidence_representation()
    {
        using var contract = Load(ArtifactRoot + "/request-contract.json");
        using var firewall = Load(ArtifactRoot + "/firewall.json");
        var c = contract.RootElement;
        Assert.Equal("hdsa-global-identity-retrieve-verify-v1", c.GetProperty("decisionContract").GetString());
        Assert.Equal(new[] { "CONTINUATION_OF", "SAME_SEMANTIC_REPEAT", "DISTINCT", "UNRESOLVED" }, c.GetProperty("decisionOptions").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.False(c.GetProperty("fullDocumentContext").GetBoolean());
        Assert.False(c.GetProperty("goldAccess").GetBoolean());
        Assert.False(c.GetProperty("providerExecution").GetBoolean());
        Assert.True(firewall.RootElement.GetProperty("evidenceRepresentationChanged").GetBoolean());
        Assert.False(firewall.RootElement.GetProperty("requestPromptChanged").GetBoolean());
        Assert.False(firewall.RootElement.GetProperty("evidenceAdded").GetBoolean());
        Assert.True(firewall.RootElement.GetProperty("tamperedPacketFailsClosed").GetBoolean());
        Assert.True(firewall.RootElement.GetProperty("tamperedManifestFailsClosed").GetBoolean());
    }

    [Fact]
    public void V4F_C_reconstruction_report_is_clean()
    {
        using var report = Load(ArtifactRoot + "/reconstruction-report.json");
        var r = report.RootElement;
        Assert.Equal("PASS", r.GetProperty("packetReconstruction").GetString());
        Assert.Equal("PASS", r.GetProperty("projectedRequestReconstruction").GetString());
        Assert.Equal(0, r.GetProperty("packetShaMismatches").GetInt32());
        Assert.Equal(0, r.GetProperty("requestShaMismatches").GetInt32());
        Assert.Equal(7_702, r.GetProperty("accepted").GetInt32());
        Assert.Equal(7_702, r.GetProperty("expected").GetInt32());
        Assert.Equal(0, r.GetProperty("dryRunTransportCalls").GetInt32());
    }
}
