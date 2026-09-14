using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

public sealed class A99IdentityPromotionBenchmarkV4EATests
{
    private const string ArtifactRoot = "artifacts/identity-benchmark/v4/pruning-challenger";

    private static string Root() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));

    private static JsonDocument Load(string fileName)
        => JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), ArtifactRoot.Replace('/', Path.DirectorySeparatorChar), fileName)));

    [Fact]
    public void V4E_A_reproduces_the_frozen_V3_shortlist_before_running_the_V4_challenger()
    {
        using var manifest = Load("manifest.json");
        var root = manifest.RootElement;

        Assert.Equal("READY_FOR_V4E_PRUNING_GOLD_EVALUATION", root.GetProperty("status").GetString());
        Assert.Equal("DEV_EXPOSED_CHALLENGER", root.GetProperty("developmentStatus").GetString());
        Assert.False(root.GetProperty("independentGeneralizationClaim").GetBoolean());
        Assert.Equal(95_999, root.GetProperty("v3BroadCandidateCount").GetInt32());
        Assert.Equal(7_659, root.GetProperty("v3BaselineShortlistCount").GetInt32());
        Assert.True(root.GetProperty("v3BaselineShortlistByteIdentical").GetBoolean());
        Assert.Equal(96_069, root.GetProperty("v4BroadCandidateCount").GetInt32());
        Assert.Equal(7_702, root.GetProperty("afterDominanceCount").GetInt32());
        Assert.Equal(7_702, root.GetProperty("shortlistedCount").GetInt32());
        Assert.Equal(7_702, root.GetProperty("requestCount").GetInt32());
    }

    [Fact]
    public void V4E_A_is_source_only_and_fail_closed_against_Gold_and_provider_execution()
    {
        using var disclosure = Load("development-disclosure.json");
        using var firewall = Load("firewall.json");
        using var ranking = Load("ranking-contract.json");
        using var shortlist = Load("shortlist-manifest.json");

        var disclosureRoot = disclosure.RootElement;
        var firewallRoot = firewall.RootElement;
        var rankingRoot = ranking.RootElement;
        var shortlistRoot = shortlist.RootElement;

        Assert.Equal(0, disclosureRoot.GetProperty("goldReadCount").GetInt32());
        Assert.Equal(0, disclosureRoot.GetProperty("providerCalls").GetInt32());
        Assert.Equal(0, disclosureRoot.GetProperty("v4cEvaluationReadCount").GetInt32());
        Assert.False(disclosureRoot.GetProperty("independentGeneralizationClaim").GetBoolean());

        Assert.Equal(0, firewallRoot.GetProperty("goldReadCount").GetInt32());
        Assert.Equal(0, firewallRoot.GetProperty("modelCalls").GetInt32());
        Assert.Equal(0, firewallRoot.GetProperty("providerCalls").GetInt32());
        Assert.Equal(0, firewallRoot.GetProperty("v4cEvaluationReadCount").GetInt32());
        Assert.True(firewallRoot.GetProperty("everyBroadCandidateHasDisposition").GetBoolean());
        Assert.True(firewallRoot.GetProperty("requestShaCheckedBeforeNetwork").GetBoolean());
        Assert.False(firewallRoot.GetProperty("exactBytesPersisted").GetBoolean());
        Assert.True(firewallRoot.GetProperty("exactBytesDeterministicallyReconstructible").GetBoolean());

        Assert.False(rankingRoot.GetProperty("goldUsed").GetBoolean());
        Assert.False(rankingRoot.GetProperty("modelUsed").GetBoolean());
        Assert.False(rankingRoot.GetProperty("reasonLabelsAreScores").GetBoolean());
        Assert.Equal(8, rankingRoot.GetProperty("budgets").GetProperty("maxCandidatesPerOccurrence").GetInt32());
        Assert.Equal(12_000, rankingRoot.GetProperty("budgets").GetProperty("maxCandidatesPerDocument").GetInt32());
        Assert.Equal(18_000, rankingRoot.GetProperty("budgets").GetProperty("maxCandidatesTotal").GetInt32());

        Assert.Equal("READY_FOR_V4E_PRUNING_GOLD_EVALUATION", shortlistRoot.GetProperty("status").GetString());
        Assert.False(shortlistRoot.GetProperty("goldUsed").GetBoolean());
        Assert.Equal(0, shortlistRoot.GetProperty("providerCalls").GetInt32());
    }

    [Fact]
    public void V4E_A_accounts_for_every_V4_only_candidate_without_budget_overflow()
    {
        using var scalability = Load("scalability-report.json");
        using var decisions = Load("pruning-decisions.json");
        var report = scalability.RootElement;
        var v4Only = report.GetProperty("v4Only");

        var total = v4Only.GetProperty("total").GetInt32();
        var shortlisted = v4Only.GetProperty("shortlisted").GetInt32();
        var dominated = v4Only.GetProperty("dominated").GetInt32();
        var budgetPruned = v4Only.GetProperty("budgetPruned").GetInt32();

        Assert.Equal(70, total);
        Assert.Equal(total, shortlisted + dominated + budgetPruned);
        Assert.Equal(43, shortlisted);
        Assert.Equal(27, dominated);
        Assert.Equal(0, budgetPruned);
        Assert.False(report.GetProperty("goldUsed").GetBoolean());
        Assert.Equal(0, report.GetProperty("providerCalls").GetInt32());

        var dispositionCounts = decisions.RootElement.GetProperty("decisions").EnumerateArray()
            .GroupBy(item => item.GetProperty("decision").GetString())
            .ToDictionary(group => group.Key!, group => group.Count(), StringComparer.Ordinal);
        Assert.Equal(96_069, dispositionCounts.Values.Sum());
        Assert.Equal(7_702, dispositionCounts["SHORTLISTED"]);
        Assert.Equal(88_367, dispositionCounts["PRUNED_DOMINATED"]);
        Assert.Equal(0, dispositionCounts.GetValueOrDefault("PRUNED_BUDGET"));
    }

    [Fact]
    public void V4E_A_freezes_deterministic_request_manifest_without_network_calls()
    {
        using var request = Load("request-manifest.json");
        using var manifest = Load("manifest.json");
        var root = request.RootElement;

        Assert.Equal("hdsa-canonical-pair-verifier-request-builder-v1", root.GetProperty("canonicalBuilderVersion").GetString());
        Assert.Equal("hdsa-global-identity-retrieve-verify-v1", root.GetProperty("requestContract").GetString());
        Assert.False(root.GetProperty("goldDerivedInput").GetBoolean());
        Assert.False(root.GetProperty("exactBytesPersisted").GetBoolean());
        Assert.Equal(7_702, root.GetProperty("requestCount").GetInt32());
        Assert.Equal(7_702, root.GetProperty("requests").GetArrayLength());
        Assert.Equal(root.GetProperty("shortlistSha256").GetString(), manifest.RootElement.GetProperty("shortlistSha256").GetString());

        foreach (var item in root.GetProperty("requests").EnumerateArray())
        {
            Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("requestSha256").GetString()));
            Assert.True(item.GetProperty("requestBytes").GetInt32() > 0);
            Assert.False(item.GetProperty("reconstruction").GetProperty("goldDerivedInput").GetBoolean());
            Assert.False(item.GetProperty("reconstruction").GetProperty("exactBytesPersisted").GetBoolean());
        }
    }
}
