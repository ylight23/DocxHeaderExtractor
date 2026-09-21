using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

public sealed class A99IdentityBenchmarkV4GCTests
{
    private const string ArtifactRoot = "artifacts/identity-benchmark/v4/target-grounding-challenger/semantic-audit";
    private static JsonDocument Load(string file) => JsonDocument.Parse(File.ReadAllText(Path.Combine(TestRepository.Root(), ArtifactRoot.Replace('/', Path.DirectorySeparatorChar), file)));

    [Fact]
    public void Integrity_and_firewall_are_closed_before_gold_join()
    {
        using var manifest = Load("manifest.json");
        var m = manifest.RootElement;
        Assert.Equal("INTEGRITY_VERIFIED", m.GetProperty("status").GetString());
        Assert.Equal(128, m.GetProperty("scheduled").GetInt32());
        Assert.Equal(128, m.GetProperty("attempts").GetInt32());
        Assert.Equal(128, m.GetProperty("providerCalls").GetInt32());
        Assert.Equal(0, m.GetProperty("modelCalls").GetInt32());
        Assert.True(m.GetProperty("noExecutionArtifactsMutated").GetBoolean());
    }

    [Fact]
    public void Provider_failures_and_target_mismatches_are_not_semantic_errors()
    {
        using var population = Load("population-denominators.json");
        var p = population.RootElement;
        Assert.Equal(128, p.GetProperty("scheduledV2").GetInt32());
        Assert.Equal(95, p.GetProperty("transportOrEvaluatorV2").GetInt32());
        Assert.Equal(77, p.GetProperty("v1V2JointlyEvaluable").GetInt32());
        Assert.Equal(49, p.GetProperty("jointlyValidRelationOutputs").GetInt32());
        Assert.Equal(45, p.GetProperty("v1TargetMismatch").GetInt32());
    }

    [Fact]
    public void Clean_relation_comparison_is_behavioral_not_gold_accuracy()
    {
        using var clean = Load("clean-cohort-analysis.json");
        var c = clean.RootElement;
        Assert.Equal(49, c.GetProperty("jointlyValid").GetInt32());
        Assert.Equal(47, c.GetProperty("relationAgreement").GetInt32());
        Assert.Equal(2, c.GetProperty("relationChanges").GetInt32());
        Assert.Equal("V1 valid and target-grounded, V2 valid and target-grounded; V1/V2 outputs only, not Gold", c.GetProperty("definition").GetString());
    }

    [Fact]
    public void Gold_join_is_after_freeze_and_does_not_invent_sample_cases()
    {
        using var gold = Load("dev-gold-diagnostic.json");
        var g = gold.RootElement;
        Assert.True(g.GetProperty("goldReadAfterBehaviorFreeze").GetBoolean());
        Assert.False(g.GetProperty("independentGeneralizationClaim").GetBoolean());
        Assert.Equal(0, g.GetProperty("evaluable").GetInt32());
        Assert.Equal(0, g.GetProperty("correct").GetInt32());
        Assert.Equal(0, g.GetProperty("incorrect").GetInt32());
    }
}
