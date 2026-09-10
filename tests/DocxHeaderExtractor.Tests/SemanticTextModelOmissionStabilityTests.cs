using System.Text.Json;
using DocxHeaderExtractor.Eval.ReasoningRetention;

namespace DocxHeaderExtractor.Tests;

public sealed class SemanticTextModelOmissionStabilityTests
{
    [Theory]
    [InlineData("EXACT_TP,EXACT_TP,EXACT_TP", "ALWAYS_FOUND")]
    [InlineData("MODEL_OMISSION,MODEL_OMISSION,MODEL_OMISSION", "PERSISTENT_3_OF_3_MISS")]
    [InlineData("MODEL_OMISSION,MODEL_OMISSION,EXACT_TP", "STOCHASTIC_2_OF_3_MISS")]
    [InlineData("MODEL_OMISSION,EXACT_TP,EXACT_TP", "STOCHASTIC_1_OF_3_MISS")]
    [InlineData("MODEL_OMISSION,MODEL_WRONG_SPAN,EXACT_TP", "MIXED_SPAN_OR_OMISSION")]
    [InlineData("SYSTEM_LOSS,EXACT_TP,EXACT_TP", "SYSTEM_AFFECTED")]
    public void Stability_classification_is_deterministic(string statusText, string expected)
    {
        Assert.Equal(expected, SemanticTextStabilityDiagnostics.Classify(statusText.Split(',')));
    }

    [Fact]
    public void Union_and_consensus_scores_are_exact_set_math()
    {
        IReadOnlySet<string>[] repeats =
        [
            new HashSet<string>(["a", "b", "x"]),
            new HashSet<string>(["a", "c", "x"]),
            new HashSet<string>(["a", "b", "y"]),
        ];
        var gold = new HashSet<string>(["a", "b", "c", "d"]);
        var union = SemanticTextStabilityDiagnostics.Score(repeats, gold, 1);
        var consensus2 = SemanticTextStabilityDiagnostics.Score(repeats, gold, 2);
        var consensus3 = SemanticTextStabilityDiagnostics.Score(repeats, gold, 3);
        Assert.Equal((3, 2, 1), (union.Tp, union.Fp, union.Fn));
        Assert.Equal((2, 1, 2), (consensus2.Tp, consensus2.Fp, consensus2.Fn));
        Assert.Equal((1, 0, 3), (consensus3.Tp, consensus3.Fp, consensus3.Fn));
    }

    [Fact]
    public void Runtime_verifier_contract_has_no_gold_or_expected_count_input()
    {
        Assert.DoesNotContain("strict-gold-occurrence", SemanticTextSelfConsistencyContract.BuildUser("SOURCE", "PROPOSALS", "route"), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expectedCount", SemanticTextSelfConsistencyContract.System, StringComparison.OrdinalIgnoreCase);
        var parsed = SemanticTextSelfConsistencyContract.Parse("{\"decisions\":[{\"candidateId\":0,\"action\":\"KEEP\"},{\"candidateId\":1,\"action\":\"REJECT\"}]}");
        Assert.Equal(2, parsed.Count);
        Assert.Equal("KEEP", parsed[0].Action);
    }

    [Fact]
    public void Existing_exact_binder_remains_the_runtime_span_authority()
    {
        var aliases = new[] { new SemanticTextSourceAlias("S1", "body/p1", 1, "alpha Heading omega") };
        var bound = SemanticTextExactBinder.Bind([new SemanticTextHeading("S1", "Heading", "SECTION")], aliases, out var observations);
        var item = Assert.Single(bound);
        Assert.Equal((6, 13), (item.Start, item.End));
        Assert.Equal(SemanticTextBindingStatus.BOUND, Assert.Single(observations).Status);
    }

    [Fact]
    public void Frozen_stability_artifacts_prove_union_consensus_firewall_and_revert_gate()
    {
        var root = RepoRoot();
        using var summary = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "eval/a99-closed-loop/model-omission-stability/summary.v1.json")));
        using var oracle = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "eval/a99-closed-loop/model-omission-stability/oracle-union-diagnostic.v1.json")));
        Assert.Equal("INTERVENTION_REVERTED", summary.RootElement.GetProperty("finalClassification").GetString());
        Assert.Equal(144, oracle.RootElement.GetProperty("union").GetProperty("tp").GetInt32());
        Assert.Equal(9, oracle.RootElement.GetProperty("union").GetProperty("fn").GetInt32());
        Assert.Equal(142, oracle.RootElement.GetProperty("consensus2Of3").GetProperty("tp").GetInt32());
        Assert.Equal(139, oracle.RootElement.GetProperty("consensus3Of3").GetProperty("tp").GetInt32());
        Assert.Equal(0, summary.RootElement.GetProperty("baseline").GetProperty("systemLoss").GetInt32());
        foreach (var freeze in Directory.EnumerateFiles(Path.Combine(root, "eval/a99-closed-loop/model-omission-stability/intervention"), "freeze.v1.json", SearchOption.AllDirectories))
        {
            using var cell = JsonDocument.Parse(File.ReadAllText(freeze));
            Assert.False(cell.RootElement.GetProperty("goldReadBeforeFreeze").GetBoolean());
        }
    }

    private static string RepoRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
}
