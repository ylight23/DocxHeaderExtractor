using System.Security.Cryptography;
using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

public sealed class A99IdentityBenchmarkV4FDTests
{
    private const string ArtifactRoot = "artifacts/identity-benchmark/v4/projected-verifier-experiment";
    private const string V4Root = "artifacts/identity-benchmark/v4/pruning-challenger";
    private static string Root() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
    private static JsonDocument Load(string relative) => JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), relative.Replace('/', Path.DirectorySeparatorChar))));
    private static string Sha256(string relative) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(Root(), relative.Replace('/', Path.DirectorySeparatorChar))))).ToLowerInvariant();

    [Fact]
    public void V4F_D_freezes_a_source_only_paired_sample()
    {
        using var m = Load(ArtifactRoot + "/manifest.json");
        var root = m.RootElement;
        Assert.Equal("READY_FOR_V4F_PAIRED_PROVIDER_EXECUTION", root.GetProperty("status").GetString());
        Assert.Equal("V4F-C@7874911", root.GetProperty("parent").GetString());
        Assert.Equal(7_702, root.GetProperty("populationCount").GetInt32());
        Assert.Equal(128, root.GetProperty("sampleCount").GetInt32());
        Assert.Equal(256, root.GetProperty("totalFutureCalls").GetInt32());
        Assert.Equal(new[] { "DOC-0123", "DOC-0133", "DOC-0252" }, root.GetProperty("documents").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.Equal(new[] { "LOCAL_ONLY", "STRUCTURALLY_PARTIAL" }, root.GetProperty("packetClasses").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.False(root.GetProperty("knownIrUsedForSelection").GetBoolean());
        Assert.Equal(0, root.GetProperty("goldReadCountForSampling").GetInt32());
        Assert.Equal(0, root.GetProperty("v4ebEvaluationReadCountForSampling").GetInt32());
        Assert.Equal(0, root.GetProperty("providerCalls").GetInt32());
        Assert.Equal(0, root.GetProperty("modelCalls").GetInt32());
    }

    [Fact]
    public void V4F_D_selection_contract_is_deterministic_and_gold_blind()
    {
        using var c = Load(ArtifactRoot + "/sampling-contract.json");
        var root = c.RootElement;
        Assert.Equal("a99-v4f-d-source-only-stratified-sha256-v1", root.GetProperty("selectionSeed").GetString());
        Assert.True(root.GetProperty("deterministic").GetBoolean());
        Assert.True(root.GetProperty("sourceOnly").GetBoolean());
        Assert.False(root.GetProperty("goldUsedForSelection").GetBoolean());
        Assert.False(root.GetProperty("knownIrUsedForSelection").GetBoolean());
        var forbidden = root.GetProperty("forbiddenInputs").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Contains("GOLD_RELATION", forbidden);
        Assert.Contains("MODEL_OUTPUT", forbidden);
        Assert.Contains("V4E_B_OUTCOME", forbidden);
    }

    [Fact]
    public void V4F_D_population_strata_account_for_every_population_and_sampled_candidate()
    {
        using var strata = Load(ArtifactRoot + "/population-strata.json");
        var rows = strata.RootElement.GetProperty("strata").EnumerateArray().ToArray();
        Assert.Equal(7_702, rows.Sum(x => x.GetProperty("populationCount").GetInt32()));
        Assert.Equal(128, rows.Sum(x => x.GetProperty("sampledCount").GetInt32()));
        Assert.All(rows, x => Assert.True(x.GetProperty("populationCount").GetInt32() >= x.GetProperty("sampledCount").GetInt32()));
    }

    [Fact]
    public void V4F_D_has_request_identity_and_configuration_parity_for_both_arms()
    {
        using var paired = Load(ArtifactRoot + "/paired-request-manifest.json");
        var root = paired.RootElement;
        Assert.Equal(128, root.GetProperty("sampleCount").GetInt32());
        Assert.Equal(256, root.GetProperty("totalFutureCalls").GetInt32());
        Assert.True(root.GetProperty("candidateIdentityParity").GetBoolean());
        Assert.True(root.GetProperty("parserSchemaIdentical").GetBoolean());
        Assert.Equal("EVIDENCE_REPRESENTATION", root.GetProperty("onlyChangedVariable").GetString());
        var arms = root.GetProperty("arms").EnumerateArray().ToArray();
        Assert.Equal(128, arms.Length);
        Assert.All(arms, pair =>
        {
            var id = pair.GetProperty("candidateId").GetString();
            var oldArm = pair.GetProperty("oldRequest");
            var newArm = pair.GetProperty("projectedRequest");
            Assert.Equal(id, oldArm.GetProperty("requestId").GetString());
            Assert.Equal(id, newArm.GetProperty("requestId").GetString());
            Assert.Equal("qwen/qwen3.7-flash", oldArm.GetProperty("model").GetString());
            Assert.Equal(oldArm.GetProperty("model").GetString(), newArm.GetProperty("model").GetString());
            Assert.Equal("OpenRouter", oldArm.GetProperty("provider").GetString());
            Assert.Equal(oldArm.GetProperty("provider").GetString(), newArm.GetProperty("provider").GetString());
            Assert.Equal(oldArm.GetProperty("configurationHash").GetString(), newArm.GetProperty("configurationHash").GetString());
            Assert.Equal(64, oldArm.GetProperty("requestSha256").GetString()!.Length);
            Assert.Equal(64, newArm.GetProperty("requestSha256").GetString()!.Length);
            Assert.True(oldArm.GetProperty("requestBytes").GetInt64() > newArm.GetProperty("requestBytes").GetInt64());
        });
    }

    [Fact]
    public void V4F_D_freezes_balanced_interleaved_execution_order()
    {
        using var order = Load(ArtifactRoot + "/execution-order.json");
        var entries = order.RootElement.GetProperty("entries").EnumerateArray().ToArray();
        Assert.Equal(256, entries.Length);
        Assert.Equal(new[] { "ARM_A", "ARM_B" }, entries.GroupBy(x => x.GetProperty("armId").GetString()).OrderBy(x => x.Key).Select(x => x.Key).ToArray());
        Assert.Equal(128, entries.Count(x => x.GetProperty("armId").GetString() == "ARM_A"));
        Assert.Equal(128, entries.Count(x => x.GetProperty("armId").GetString() == "ARM_B"));
        Assert.Equal(Enumerable.Range(1, 256), entries.Select(x => x.GetProperty("sequence").GetInt32()));
        for (var i = 0; i < entries.Length; i += 2)
        {
            Assert.Equal(entries[i].GetProperty("candidateId").GetString(), entries[i + 1].GetProperty("candidateId").GetString());
            Assert.NotEqual(entries[i].GetProperty("armId").GetString(), entries[i + 1].GetProperty("armId").GetString());
        }
    }

    [Fact]
    public void V4F_D_freezes_metrics_as_behavioral_agreement_not_accuracy()
    {
        using var metrics = Load(ArtifactRoot + "/metrics-contract.json");
        var root = metrics.RootElement;
        Assert.False(root.GetProperty("agreementIsAccuracy").GetBoolean());
        Assert.True(root.GetProperty("freezeBeforeExecution").GetBoolean());
        Assert.Equal("SEPARATE; requires independent labels; ARM_A is not oracle", root.GetProperty("semanticAccuracy").GetString());
        Assert.Contains("PairwiseRelationAgreement", root.GetProperty("behavioralPreservation").EnumerateArray().Select(x => x.GetString()));
        Assert.Contains("ProjectedChangeRate", root.GetProperty("behavioralPreservation").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public void V4F_D_freezes_zero_call_firewall_and_hash_only_request_storage()
    {
        using var firewall = Load(ArtifactRoot + "/firewall.json");
        using var sample = Load(ArtifactRoot + "/sample.json");
        var f = firewall.RootElement;
        Assert.Equal(0, f.GetProperty("providerCalls").GetInt32());
        Assert.Equal(0, f.GetProperty("modelCalls").GetInt32());
        Assert.Equal(0, f.GetProperty("goldReadCountForSampling").GetInt32());
        Assert.Equal(0, f.GetProperty("v4ebEvaluationReadCountForSampling").GetInt32());
        Assert.False(f.GetProperty("knownIrUsedForSelection").GetBoolean());
        Assert.False(f.GetProperty("candidateUniverseChanged").GetBoolean());
        Assert.False(f.GetProperty("rankingChanged").GetBoolean());
        Assert.False(f.GetProperty("projectionChanged").GetBoolean());
        Assert.True(f.GetProperty("requestHashesOnly").GetBoolean());
        Assert.True(f.GetProperty("noProviderTransport").GetBoolean());
        Assert.Equal(128, sample.RootElement.GetProperty("candidates").GetArrayLength());
    }
}
