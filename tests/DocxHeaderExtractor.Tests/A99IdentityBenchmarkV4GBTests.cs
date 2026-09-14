using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

public sealed class A99IdentityBenchmarkV4GBTests
{
    private const string ArtifactRoot = "artifacts/identity-benchmark/v4/target-grounding-challenger/execution";
    private static string Root() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
    private static JsonDocument Load(string file) => JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), ArtifactRoot.Replace('/', Path.DirectorySeparatorChar), file)));

    [Fact]
    public void Preflight_requires_explicit_approval_and_did_not_transport()
    {
        using var preflight = Load("provider-preflight.json");
        var p = preflight.RootElement;
        Assert.Equal("AWAITING_V4G_PROVIDER_EXECUTION_APPROVAL", p.GetProperty("status").GetString());
        Assert.True(p.GetProperty("requiredExplicitApproval").GetBoolean());
        Assert.Equal(128, p.GetProperty("sampleCount").GetInt32());
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
    public void Execution_is_scheduled_for_all_128_not_historical_failures_only()
    {
        using var manifest = Load("execution-manifest.json");
        var m = manifest.RootElement;
        Assert.Equal(128, m.GetProperty("scheduledCalls").GetInt32());
        Assert.Equal(0, m.GetProperty("actualProviderCalls").GetInt32());
        Assert.True(m.GetProperty("targetGroundingRepresentationChanged").GetBoolean());
        using var attempts = Load("attempt-manifest.json");
        Assert.Equal(128, attempts.RootElement.GetProperty("expectedAttemptCount").GetInt32());
        Assert.Equal(0, attempts.RootElement.GetProperty("actualAttemptCount").GetInt32());
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
        Assert.True(f.GetProperty("noProviderTransport").GetBoolean());
    }
}
