using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Tests;

public sealed class A99IdentityBenchmarkV4GATests
{
    private const string ArtifactRoot = "artifacts/identity-benchmark/v4/target-grounding-challenger";
    private static string Root() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
    private static JsonDocument Load(string file) => JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), ArtifactRoot.Replace('/', Path.DirectorySeparatorChar), file)));

    [Fact]
    public void Target_grounded_builder_keeps_exact_distinct_ids_when_visible_text_matches()
    {
        using var packet = JsonDocument.Parse("""
        {
          "artifactKind":"packet",
          "candidateId":"DOC-X:P1-2",
          "pairCore":[
            {"sourceOccurrenceId":"DOC-X:P1","rawSurface":"SAME","canonicalComparisonSurface":"SAME","documentOrder":1,"sourceKind":"SOURCE_PACKET","sourceContainer":"body"},
            {"sourceOccurrenceId":"DOC-X:P2","rawSurface":"SAME","canonicalComparisonSurface":"SAME","documentOrder":2,"sourceKind":"SOURCE_PACKET","sourceContainer":"body"}
          ],
          "localContext":{"previousOfLeft":[],"nextOfLeft":[],"previousOfRight":[],"nextOfRight":[],"intervening":[],"interveningTruncated":false,"totalInterveningOccurrences":0},
          "structuralContext":{"sourceContainers":["body"],"structuralPeers":[]},
          "relationalFacts":{},"evidenceAvailability":{},"missingEvidence":[]
        }
        """);

        var built = HdsaCanonicalTargetGroundedProjectedPairVerifierRequestBuilder.Build(packet.RootElement);
        using var request = JsonDocument.Parse(built.Json);
        var target = request.RootElement.GetProperty("targetPair");
        Assert.Equal("DOC-X:P1", target.GetProperty("leftTarget").GetProperty("occurrenceId").GetString());
        Assert.Equal("DOC-X:P2", target.GetProperty("rightTarget").GetProperty("occurrenceId").GetString());
        Assert.Equal("LEFT_TARGET", target.GetProperty("leftTarget").GetProperty("role").GetString());
        Assert.Equal("RIGHT_TARGET", target.GetProperty("rightTarget").GetProperty("role").GetString());
        Assert.Equal("SAME", target.GetProperty("leftTarget").GetProperty("verbatimText").GetString());
        Assert.Equal("SAME", target.GetProperty("rightTarget").GetProperty("verbatimText").GetString());
        Assert.False(request.RootElement.GetProperty("supportingEvidence").TryGetProperty("pairCore", out _));

        var sourceNode = JsonNode.Parse(packet.RootElement.GetRawText())!.AsObject();
        var permutedNode = new JsonObject();
        foreach (var property in sourceNode.Reverse())
            permutedNode.Add(property.Key, property.Value?.DeepClone());
        using var permuted = JsonDocument.Parse(permutedNode.ToJsonString());
        var permutedBuilt = HdsaCanonicalTargetGroundedProjectedPairVerifierRequestBuilder.Build(permuted.RootElement);
        Assert.Equal(built.Sha256, permutedBuilt.Sha256);
    }

    [Fact]
    public void V4G_A_freezes_full_universe_and_reuses_the_exact_sample()
    {
        using var manifest = Load("manifest.json");
        var m = manifest.RootElement;
        Assert.Equal("READY_FOR_V4G_TARGET_GROUNDING_PROVIDER_EXPERIMENT", m.GetProperty("status").GetString());
        Assert.Equal("DEV_EXPOSED_CHALLENGER", m.GetProperty("developmentStatus").GetString());
        Assert.False(m.GetProperty("independentGeneralizationClaim").GetBoolean());
        Assert.Equal(7_702, m.GetProperty("candidateCount").GetInt32());
        Assert.Equal(7_702, m.GetProperty("v4gRequestCount").GetInt32());
        Assert.Equal(128, m.GetProperty("frozenSampleCount").GetInt32());
        Assert.Equal(0, m.GetProperty("newSourceFacts").GetInt32());
        Assert.Equal(0, m.GetProperty("providerCalls").GetInt32());
        Assert.Equal(0, m.GetProperty("modelCalls").GetInt32());
        Assert.Equal(0, m.GetProperty("goldReadCount").GetInt32());
        Assert.Equal(64_990_323, m.GetProperty("v4cProjectedTotalBytes").GetInt64());
        Assert.Equal(72_186_437, m.GetProperty("v4gTotalBytes").GetInt64());
    }

    [Fact]
    public void V4G_A_lineage_and_firewall_pass_without_model_or_gold()
    {
        using var firewall = Load("firewall.json");
        var f = firewall.RootElement;
        Assert.Equal(0, f.GetProperty("goldReadCount").GetInt32());
        Assert.Equal(0, f.GetProperty("v4ebEvaluationReadCount").GetInt32());
        Assert.Equal(0, f.GetProperty("providerCalls").GetInt32());
        Assert.Equal(0, f.GetProperty("modelCalls").GetInt32());
        Assert.False(f.GetProperty("candidateUniverseChanged").GetBoolean());
        Assert.False(f.GetProperty("projectionEvidenceChanged").GetBoolean());
        Assert.False(f.GetProperty("rankingChanged").GetBoolean());
        Assert.False(f.GetProperty("parserChanged").GetBoolean());

        using var audit = Load("evidence-lineage-audit.json");
        var a = audit.RootElement;
        Assert.Equal(7_702, a.GetProperty("candidateCount").GetInt32());
        Assert.Equal(0, a.GetProperty("newSourceFacts").GetInt32());
        Assert.Equal(7_702, a.GetProperty("counts").GetProperty("reordered").GetInt32());
        Assert.Equal(15_404, a.GetProperty("counts").GetProperty("duplicatedTargetFacts").GetInt32());
    }

    [Fact]
    public void V4G_A_manifest_contains_the_same_7702_candidate_ids()
    {
        using var requests = Load("request-manifest.json");
        using var projected = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), "artifacts/identity-benchmark/v4/projected-requests/request-manifest.json")));
        var ids = requests.RootElement.GetProperty("requests").EnumerateArray().Select(x => x.GetProperty("candidateId").GetString()!).ToArray();
        var oldIds = projected.RootElement.GetProperty("requests").EnumerateArray().Select(x => x.GetProperty("candidateId").GetString()!).ToArray();
        Assert.Equal(7_702, ids.Length);
        Assert.Equal(oldIds.Order(StringComparer.Ordinal), ids.Order(StringComparer.Ordinal));
        Assert.All(requests.RootElement.GetProperty("requests").EnumerateArray(), x => Assert.Equal("hdsa-canonical-target-grounded-projected-pair-verifier-request-builder-v1", x.GetProperty("builderVersion").GetString()));
    }
}
