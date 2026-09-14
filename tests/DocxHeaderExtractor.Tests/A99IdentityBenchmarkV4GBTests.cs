using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

public sealed class A99IdentityBenchmarkV4GBTests
{
    private const string ArtifactRoot = "artifacts/identity-benchmark/v4/target-grounding-challenger/execution";
    private static string Root() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
    private static JsonDocument Load(string file) => JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), ArtifactRoot.Replace('/', Path.DirectorySeparatorChar), file)));

    [Fact]
    public void Provider_preflight_is_frozen_before_transport()
    {
        using var preflight = Load("provider-preflight.json");
        var p = preflight.RootElement;
        Assert.Equal("PREFLIGHT_PASS_FROZEN_CAPABILITY", p.GetProperty("status").GetString());
        Assert.Equal(128, p.GetProperty("requestCount").GetInt32());
        Assert.True(p.GetProperty("explicitExecutionFlag").GetBoolean());
        Assert.Equal(0, p.GetProperty("modelCalls").GetInt32());
        Assert.Equal(0, p.GetProperty("providerCalls").GetInt32());
        Assert.Equal(0, p.GetProperty("goldReadCount").GetInt32());
    }

    [Fact]
    public void Historical_baseline_is_reconstructed_before_v2_execution()
    {
        using var baseline = Load("historical-baseline.json");
        var b = baseline.RootElement;
        Assert.Equal("FROZEN_V4F_E_EXECUTION_ARTIFACTS", b.GetProperty("source").GetString());
        Assert.False(b.GetProperty("temporalProviderDriftControlled").GetBoolean());
        var old = b.GetProperty("arms").EnumerateArray().Single(x => x.GetProperty("arm").GetString() == "OLD");
        var projected = b.GetProperty("arms").EnumerateArray().Single(x => x.GetProperty("arm").GetString() == "PROJECTED_V1");
        Assert.Equal(2, old.GetProperty("targetMismatch").GetInt32());
        Assert.Equal(109, old.GetProperty("valid").GetInt32());
        Assert.Equal(16, old.GetProperty("providerError").GetInt32());
        Assert.Equal(1, old.GetProperty("rawPersistenceFailure").GetInt32());
        Assert.Equal(45, projected.GetProperty("targetMismatch").GetInt32());
        Assert.Equal(63, projected.GetProperty("valid").GetInt32());
        Assert.Equal(19, projected.GetProperty("providerError").GetInt32());
        Assert.Equal(1, projected.GetProperty("rawPersistenceFailure").GetInt32());
    }

    [Fact]
    public void Execution_completed_all_128_attempts_without_retry()
    {
        using var manifest = Load("execution-manifest.json");
        var m = manifest.RootElement;
        Assert.Equal(128, m.GetProperty("scheduledCalls").GetInt32());
        Assert.Equal(128, m.GetProperty("actualProviderCalls").GetInt32());
        using var attempts = Load("attempt-manifest.json");
        Assert.Equal(128, attempts.RootElement.GetProperty("expectedAttemptCount").GetInt32());
        Assert.Equal(128, attempts.RootElement.GetProperty("actualAttemptCount").GetInt32());
    }

    [Fact]
    public void Firewall_keeps_gold_and_historical_labels_closed()
    {
        using var firewall = Load("firewall.json");
        var f = firewall.RootElement;
        Assert.Equal(0, f.GetProperty("goldReadCount").GetInt32());
        Assert.Equal(0, f.GetProperty("ir018ToIr022ReadCount").GetInt32());
        Assert.Equal(0, f.GetProperty("v4ebEvaluationReadCount").GetInt32());
        Assert.False(f.GetProperty("candidateUniverseChanged").GetBoolean());
        Assert.False(f.GetProperty("evidenceChanged").GetBoolean());
        Assert.True(f.GetProperty("targetGroundingRepresentationChanged").GetBoolean());
        Assert.Equal(128, f.GetProperty("providerCalls").GetInt32());
        Assert.True(f.GetProperty("all128Scheduled").GetBoolean());
        Assert.True(f.GetProperty("rawFrozenBeforeParse").GetBoolean());
    }

    [Fact]
    public void Response_freeze_has_zero_target_mismatch_and_gold_reads()
    {
        using var results = Load("target-grounding-results.json");
        var r = results.RootElement;
        Assert.Equal("FROZEN_BEFORE_GOLD", r.GetProperty("status").GetString());
        Assert.Equal(128, r.GetProperty("scheduled").GetInt32());
        Assert.Equal(95, r.GetProperty("evaluable").GetInt32());
        Assert.Equal(0, r.GetProperty("targetMismatch").GetInt32());
        Assert.Equal(0, r.GetProperty("goldReadCount").GetInt32());
        Assert.False(r.GetProperty("semanticAccuracyMeasured").GetBoolean());

        using var paired = Load("historical-paired-analysis.json");
        var p = paired.RootElement;
        Assert.Equal("COMPLETE_AFTER_RESPONSE_FREEZE", p.GetProperty("status").GetString());
        Assert.Equal(45, p.GetProperty("v1TargetMismatch").GetInt32());
        Assert.Equal(0, p.GetProperty("v2TargetMismatch").GetInt32());
        Assert.Equal(28, p.GetProperty("repairedTargetGrounding").GetInt32());
        Assert.Equal(0, p.GetProperty("goldReadCount").GetInt32());
    }
}
