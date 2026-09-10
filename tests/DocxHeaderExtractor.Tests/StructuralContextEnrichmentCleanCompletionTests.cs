using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

public sealed class StructuralContextEnrichmentCleanCompletionTests
{
    [Fact]
    public void Cell_matrix_reuses_successes_and_isolates_provider_failures()
    {
        using var matrix = Load("eval/a99-closed-loop/semantic-text-stable-error-repair/campaign-cell-matrix.v1.json");
        var cells = matrix.RootElement.GetProperty("cells").EnumerateArray().ToArray();
        Assert.Equal(15, cells.Length);
        Assert.Equal(13, cells.Count(x => x.GetProperty("state").GetString() == "FROZEN_SUCCESS"));
        Assert.Equal(2, cells.Count(x => x.GetProperty("state").GetString() == "FROZEN_PROVIDER_FAILURE"));
        Assert.Equal(0, matrix.RootElement.GetProperty("providerCallsCurrentRun").GetInt32());
    }

    [Fact]
    public void System_loss_trace_proves_navigation_role_exclusion_not_binder_loss()
    {
        using var audit = Load("eval/a99-closed-loop/semantic-text-stable-error-repair/system-loss-audit.v1.json");
        var root = audit.RootElement;
        Assert.Equal(4, root.GetProperty("counts").GetProperty("systemProjection").GetInt32());
        Assert.Equal(0, root.GetProperty("counts").GetProperty("traceAccounting").GetInt32());
        Assert.False(root.GetProperty("genericPostModelDefectProven").GetBoolean());
        Assert.All(root.GetProperty("losses").EnumerateArray(), x => Assert.Equal("MODEL_ROLE_CONTRACT_EXCLUSION", x.GetProperty("classification").GetString()));
    }

    [Fact]
    public void Enrichment_packet_audit_has_parser_facts_without_gold_or_candidate_decisions()
    {
        using var overhead = Load("eval/a99-closed-loop/semantic-text-stable-error-repair/packet-overhead.v1.json");
        var root = overhead.RootElement;
        Assert.True(root.GetProperty("factsAreParserDerived").GetBoolean());
        Assert.False(root.GetProperty("candidateGating").GetBoolean());
        Assert.False(root.GetProperty("goldInPacket").GetBoolean());
        Assert.All(root.GetProperty("rows").EnumerateArray(), x => Assert.True(x.GetProperty("enrichmentPacketCharacters").GetInt32() >= x.GetProperty("baselinePacketCharacters").GetInt32()));
    }

    [Fact]
    public void Final_decision_does_not_score_provider_failure_as_semantic_fn()
    {
        using var summary = Load("eval/a99-closed-loop/semantic-text-stable-error-repair/clean-completion-summary.v1.json");
        var root = summary.RootElement;
        Assert.Equal("ENRICHMENT_V1_NOT_MEASURED_PROVIDER_BLOCKED", root.GetProperty("decision").GetString());
        Assert.Equal(0, root.GetProperty("providerCallsCurrentRun").GetInt32());
        Assert.True(root.GetProperty("goldFirewall").GetString() == "PASS");
    }

    private static JsonDocument Load(string relativePath) => JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), relativePath.Replace('/', Path.DirectorySeparatorChar))));
    private static string Root() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
}
