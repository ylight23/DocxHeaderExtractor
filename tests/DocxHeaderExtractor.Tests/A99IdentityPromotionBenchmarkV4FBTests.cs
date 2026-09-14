using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

public sealed class A99IdentityPromotionBenchmarkV4FBTests
{
    private const string ArtifactRoot = "artifacts/identity-benchmark/v4/context-projection";
    private const string V4Root = "artifacts/identity-benchmark/v4/pruning-challenger";

    private static string Root() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
    private static JsonDocument Load(string relative) => JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), relative.Replace('/', Path.DirectorySeparatorChar))));

    [Fact]
    public void V4F_B_preserves_the_frozen_candidate_count_and_freezes_bounds()
    {
        using var manifest = Load(ArtifactRoot + "/manifest.json");
        using var config = Load(ArtifactRoot + "/projection-config.json");
        using var packets = Load(ArtifactRoot + "/packet-manifest.json");
        Assert.Equal("READY_FOR_V4F_PROJECTED_REQUEST_FREEZE", manifest.RootElement.GetProperty("status").GetString());
        Assert.Equal(7_702, manifest.RootElement.GetProperty("candidateCount").GetInt32());
        Assert.Equal(7_702, manifest.RootElement.GetProperty("projectedPacketCount").GetInt32());
        Assert.Equal(7_702, packets.RootElement.GetProperty("packetCount").GetInt32());
        Assert.Equal(2, config.RootElement.GetProperty("previousOccurrenceCount").GetInt32());
        Assert.Equal(2, config.RootElement.GetProperty("nextOccurrenceCount").GetInt32());
        Assert.Equal(8, config.RootElement.GetProperty("maxInterveningOccurrences").GetInt32());
        Assert.Equal(4, config.RootElement.GetProperty("maxStructuralPeers").GetInt32());
        Assert.Equal(26, config.RootElement.GetProperty("maxPacketOccurrences").GetInt32());
    }

    [Fact]
    public void V4F_B_packet_manifest_is_hash_only_and_all_entries_are_bounded()
    {
        using var packets = Load(ArtifactRoot + "/packet-manifest.json");
        var entries = packets.RootElement.GetProperty("packets").EnumerateArray().ToArray();
        Assert.Equal(7_702, entries.Length);
        Assert.Equal(7_702, entries.Select(x => x.GetProperty("candidateId").GetString()).Distinct(StringComparer.Ordinal).Count());
        foreach (var entry in entries)
        {
            Assert.True(entry.GetProperty("packetBytes").GetInt32() > 0);
            Assert.Equal(64, entry.GetProperty("packetSha256").GetString()!.Length);
            Assert.True(entry.GetProperty("sourceOccurrenceIds").GetArrayLength() <= 26);
            Assert.False(entry.GetProperty("reconstruction").GetProperty("packetBodiesPersisted").GetBoolean());
            Assert.Equal(entry.GetProperty("candidateId").GetString(), entry.GetProperty("reconstruction").GetProperty("candidateId").GetString());
        }
    }

    [Fact]
    public void V4F_B_projected_identity_set_matches_the_frozen_shortlist()
    {
        using var shortlist = Load(V4Root + "/shortlist.json");
        using var packets = Load(ArtifactRoot + "/packet-manifest.json");
        var expected = shortlist.RootElement.GetProperty("candidates").EnumerateArray().Select(x => x.GetProperty("pairId").GetString()!).ToHashSet(StringComparer.Ordinal);
        var actual = packets.RootElement.GetProperty("packets").EnumerateArray().Select(x => x.GetProperty("candidateId").GetString()!).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(7_702, expected.Count);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void V4F_B_size_reduction_is_material_and_exactly_accounted()
    {
        using var sizes = Load(ArtifactRoot + "/packet-size-distribution.json");
        var comparison = sizes.RootElement.GetProperty("comparison");
        Assert.Equal(7_070_469_069L, comparison.GetProperty("oldTotalBytes").GetInt64());
        Assert.Equal(53_572_661L, comparison.GetProperty("newTotalBytes").GetInt64());
        Assert.True(comparison.GetProperty("reductionPercentage").GetDouble() > .99);
        Assert.True(comparison.GetProperty("compressionRatio").GetDouble() > 100);
        Assert.Equal(7_702, sizes.RootElement.GetProperty("newPacketBytes").GetProperty("count").GetInt32());
        Assert.Equal(7_702, sizes.RootElement.GetProperty("newEstimatedTokens").GetProperty("count").GetInt32());
    }

    [Fact]
    public void V4F_B_payload_decomposition_has_an_exact_sum()
    {
        using var payload = Load(ArtifactRoot + "/payload-decomposition.json");
        var t = payload.RootElement.GetProperty("totals");
        var sum = t.GetProperty("pairSpecificBytes").GetInt64() + t.GetProperty("localContextBytes").GetInt64() + t.GetProperty("structuralContextBytes").GetInt64() + t.GetProperty("relationalFactsBytes").GetInt64() + t.GetProperty("staticSchemaMetadataBytes").GetInt64() + t.GetProperty("otherBytes").GetInt64();
        Assert.True(t.GetProperty("exactSum").GetBoolean());
        Assert.Equal(t.GetProperty("totalBytes").GetInt64(), sum);
        Assert.Equal(53_572_661L, t.GetProperty("totalBytes").GetInt64());
    }

    [Fact]
    public void V4F_B_availability_is_descriptive_and_source_only()
    {
        using var availability = Load(ArtifactRoot + "/evidence-availability.json");
        using var contract = Load(ArtifactRoot + "/projection-contract.json");
        var a = availability.RootElement;
        Assert.Equal(7_702, a.GetProperty("packetCount").GetInt32());
        Assert.Equal(7_702, a.GetProperty("pairCore").GetProperty("count").GetInt32());
        Assert.Equal(7_702, a.GetProperty("localContext").GetProperty("count").GetInt32());
        Assert.Equal(0, a.GetProperty("numbering").GetProperty("count").GetInt32());
        Assert.Equal(0, a.GetProperty("scope").GetProperty("count").GetInt32());
        Assert.Equal(0, a.GetProperty("visual").GetProperty("count").GetInt32());
        Assert.False(contract.RootElement.GetProperty("semanticConclusions").GetBoolean());
        Assert.False(contract.RootElement.GetProperty("goldAccess").GetBoolean());
    }

    [Fact]
    public void V4F_B_firewall_blocks_gold_v4e_b_and_provider_access()
    {
        using var firewall = Load(ArtifactRoot + "/firewall.json");
        using var manifest = Load(ArtifactRoot + "/manifest.json");
        var f = firewall.RootElement;
        Assert.Equal(0, f.GetProperty("goldReadCount").GetInt32());
        Assert.Equal(0, f.GetProperty("v4ebEvaluationReadCount").GetInt32());
        Assert.Equal(0, f.GetProperty("providerCalls").GetInt32());
        Assert.Equal(0, f.GetProperty("modelCalls").GetInt32());
        Assert.False(f.GetProperty("candidateCountChanged").GetBoolean());
        Assert.False(f.GetProperty("candidateOrderAffectsPacket").GetBoolean());
        Assert.False(f.GetProperty("relationFieldsConsumed").GetBoolean());
        Assert.True(f.GetProperty("noProviderTransport").GetBoolean());
        Assert.Equal(0, f.GetProperty("reconstructionFailures").GetInt32());
        Assert.Equal(0, f.GetProperty("projectionLimitViolations").GetInt32());
        Assert.Equal("READY_FOR_V4F_PROJECTED_REQUEST_FREEZE", manifest.RootElement.GetProperty("nextGate").GetString());
    }
}
