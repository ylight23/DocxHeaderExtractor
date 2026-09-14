using System.Text.Json;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Tests;

public sealed class A99IdentityBenchmarkV5BTests
{
    private static string RepositoryRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));

    [Fact]
    public void V5B_preflight_is_frozen_source_only_and_prepares_all_clusters()
    {
        var root = RepositoryRoot();
        var path = Path.Combine(root, "artifacts", "identity-benchmark", "v5", "semantic-node-induction", "preflight", "manifest.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var value = doc.RootElement;
        Assert.Equal("READY_FOR_PROVIDER_EXECUTION", value.GetProperty("status").GetString());
        Assert.Equal(102, value.GetProperty("clusterCount").GetInt32());
        Assert.Equal(102, value.GetProperty("requestCount").GetInt32());
        Assert.Equal(0, value.GetProperty("modelCalls").GetInt32());
        Assert.Equal(0, value.GetProperty("providerCalls").GetInt32());
        Assert.Equal(0, value.GetProperty("goldReadCount").GetInt32());
        Assert.False(value.GetProperty("knownPairLabelsIncluded").GetBoolean());
        Assert.False(value.GetProperty("hierarchyRequested").GetBoolean());
        Assert.False(value.GetProperty("predictionsFrozen").GetBoolean());
    }

    [Fact]
    public void V5B_validator_rejects_unknown_or_incomplete_assignments_and_cross_node_continuation()
    {
        var request = new HdsaSemanticClusterInductionRequest(
            HdsaSemanticClusterInductionContract.Version,
            "SC-test",
            "DOC-test",
            "source",
            "V5:DOC-test:SC-test",
            [
                new HdsaSemanticClusterOccurrence("O1", "A", 1, [], [new("O2", "B", 2)]),
                new HdsaSemanticClusterOccurrence("O2", "B", 2, [new("O1", "A", 1)], []),
            ],
            new HdsaSemanticClusterEvidence([], [], [], [], []));
        var response = new HdsaSemanticClusterInductionResponse(
            "SC-test",
            [new("O1", "N1", HdsaSemanticClusterOccurrenceRole.Primary, HdsaSemanticClusterConfidence.High)],
            [new("N1", "A", "scope", "heading")],
            [new("O1", "O2")],
            []);

        var result = HdsaSemanticClusterInductionContract.Validate(request, response);

        Assert.False(result.Accepted);
        Assert.Contains("INCOMPLETE_OCCURRENCE_COVERAGE", result.Errors);
        Assert.Contains("CONTINUATION_TO_UNRESOLVED", result.Errors);
    }

    [Fact]
    public void V5B_validator_accepts_complete_cluster_local_assignments()
    {
        var request = new HdsaSemanticClusterInductionRequest(
            HdsaSemanticClusterInductionContract.Version,
            "SC-test",
            "DOC-test",
            "source",
            "V5:DOC-test:SC-test",
            [
                new HdsaSemanticClusterOccurrence("O1", "A", 1, [], [new("O2", "B", 2)]),
                new HdsaSemanticClusterOccurrence("O2", "B", 2, [new("O1", "A", 1)], []),
            ],
            new HdsaSemanticClusterEvidence([], [], [], [], []));
        var response = new HdsaSemanticClusterInductionResponse(
            "SC-test",
            [
                new("O1", "N1", HdsaSemanticClusterOccurrenceRole.Primary, HdsaSemanticClusterConfidence.High),
                new("O2", "N1", HdsaSemanticClusterOccurrenceRole.Continuation, HdsaSemanticClusterConfidence.Medium),
            ],
            [new("N1", "A/B", "scope", "heading")],
            [new("O1", "O2")],
            []);

        var result = HdsaSemanticClusterInductionContract.Validate(request, response);

        Assert.True(result.Accepted);
        Assert.True(result.Complete);
        Assert.Equal("V5:DOC-test:SC-test:N1", HdsaSemanticClusterInductionContract.Namespace("DOC-test", "SC-test", "N1"));
    }
}
