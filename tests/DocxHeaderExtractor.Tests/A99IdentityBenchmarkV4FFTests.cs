using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

public sealed class A99IdentityBenchmarkV4FFTests
{
    private const string RootRelative = "artifacts/identity-benchmark/v4/projected-verifier-experiment/forensics";
    private static JsonDocument Load(string file) => JsonDocument.Parse(File.ReadAllText(Path.Combine(TestRepository.Root(), RootRelative.Replace('/', Path.DirectorySeparatorChar), file)));

    [Fact]
    public void V4F_F_freezes_zero_call_firewall_and_single_diagnosis()
    {
        using var manifest = Load("manifest.json");
        var root = manifest.RootElement;
        Assert.Equal("IDENTITY_BENCHMARK_V4F_F", root.GetProperty("experiment").GetString());
        Assert.Equal("V4F-E@5ae41b6", root.GetProperty("parent").GetString());
        Assert.Equal(0, root.GetProperty("providerCalls").GetInt32());
        Assert.Equal(0, root.GetProperty("modelCalls").GetInt32());
        Assert.Equal(0, root.GetProperty("goldReadCount").GetInt32());
        Assert.False(root.GetProperty("executionArtifactsMutated").GetBoolean());
        Assert.False(root.GetProperty("promptChanged").GetBoolean());
        Assert.False(root.GetProperty("projectionChanged").GetBoolean());
        Assert.False(root.GetProperty("parserChanged").GetBoolean());
        Assert.False(root.GetProperty("goldDerivedInput").GetBoolean());
        Assert.Equal("PROJECTED_TARGET_GROUNDING_DEGRADATION", root.GetProperty("primaryDiagnosis").GetString());
    }

    [Fact]
    public void V4F_F_reconciles_scheduled_arms_and_agreement_denominators()
    {
        using var manifest = Load("manifest.json");
        var m = manifest.RootElement;
        Assert.Equal(128, m.GetProperty("scheduledPairs").GetInt32());
        Assert.Equal(256, m.GetProperty("scheduledAttempts").GetInt32());
        Assert.Equal(58, m.GetProperty("bothParseValidPairs").GetInt32());
        Assert.Equal(58, m.GetProperty("agreementDenominator").GetInt32());
        Assert.Equal(23, m.GetProperty("disagreementCount").GetInt32());

        using var outcomes = Load("arm-outcome-breakdown.json");
        var arms = outcomes.RootElement.GetProperty("arms").EnumerateArray().ToDictionary(x => x.GetProperty("arm").GetString()!);
        Assert.Equal(128, arms["ARM_A"].GetProperty("scheduled").GetInt32());
        Assert.Equal(128, arms["ARM_B"].GetProperty("scheduled").GetInt32());
        Assert.Equal(109, arms["ARM_A"].GetProperty("httpSuccessParseValid").GetInt32());
        Assert.Equal(63, arms["ARM_B"].GetProperty("httpSuccessParseValid").GetInt32());
        Assert.Equal(2, arms["ARM_A"].GetProperty("httpSuccessValidatorRejected").GetInt32());
        Assert.Equal(45, arms["ARM_B"].GetProperty("httpSuccessValidatorRejected").GetInt32());
        Assert.Equal(16, arms["ARM_A"].GetProperty("providerFailure").GetInt32());
        Assert.Equal(19, arms["ARM_B"].GetProperty("providerFailure").GetInt32());
    }

    [Fact]
    public void V4F_F_limits_behavioral_agreement_to_both_valid_pairs()
    {
        using var matrix = Load("disagreement-matrix.json");
        var root = matrix.RootElement;
        Assert.Equal(58, root.GetProperty("bothParseValidPairs").GetInt32());
        Assert.Equal(35, root.GetProperty("agreementCount").GetInt32());
        Assert.Equal(23, root.GetProperty("disagreementCount").GetInt32());
        Assert.Equal(23, root.GetProperty("disagreements").GetArrayLength());
        Assert.False(root.GetProperty("rationaleAvailable").GetBoolean());
        Assert.Equal("not measured; no Gold read", root.GetProperty("semanticAccuracy").GetString());
    }

    [Fact]
    public void V4F_F_attributes_projected_invalidity_to_target_grounding_without_gold()
    {
        using var taxonomy = Load("invalid-response-taxonomy.json");
        var rows = taxonomy.RootElement.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(47, rows.Length);
        Assert.Equal(45, rows.Count(x => x.GetProperty("arm").GetString() == "ARM_B"));
        Assert.All(rows, x => Assert.Equal("TARGET_ID_MISMATCH", x.GetProperty("category").GetString()));

        using var grounding = Load("target-grounding-analysis.json");
        var projected = grounding.RootElement.GetProperty("byArm").EnumerateArray().Single(x => x.GetProperty("arm").GetString() == "ARM_B");
        Assert.Equal(45, projected.GetProperty("targetPairMismatch").GetInt32());
        Assert.Equal(0, projected.GetProperty("relationInvalid").GetInt32());
        Assert.Equal(0, projected.GetProperty("directionInvalid").GetInt32());
    }

    [Fact]
    public void V4F_F_records_provider_failures_and_raw_persistence_gap_separately()
    {
        using var provider = Load("provider-failure-analysis.json");
        var root = provider.RootElement;
        Assert.Equal("OpenRouter", root.GetProperty("provider").GetString());
        Assert.Equal(35, root.GetProperty("failures").GetArrayLength());
        Assert.All(root.GetProperty("failures").EnumerateArray(), x =>
        {
            Assert.Equal("429", x.GetProperty("httpCode").GetString());
            Assert.Equal("insufficient_quota", x.GetProperty("providerErrorCode").GetString());
            Assert.Equal("Alibaba", x.GetProperty("providerName").GetString());
        });

        using var attribution = Load("failure-attribution.json");
        var a = attribution.RootElement;
        Assert.Equal(101, a.GetProperty("bothTransportSuccessPairs").GetInt32());
        Assert.Equal(100, a.GetProperty("bothRawAvailablePairs").GetInt32());
        Assert.Equal(2, a.GetProperty("rawPersistenceIncidents").GetInt32());
        Assert.Equal(0, a.GetProperty("goldReadCount").GetInt32());
    }
}
