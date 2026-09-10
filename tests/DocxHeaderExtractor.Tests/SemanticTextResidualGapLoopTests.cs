using System.Text.Json;
using DocxHeaderExtractor.Eval.ReasoningRetention;

namespace DocxHeaderExtractor.Tests;

public sealed class SemanticTextResidualGapLoopTests
{
    private const string Root = "eval/a99-closed-loop/semantic-text-residual-loop";

    [Fact]
    public void Residual_loop_inventory_is_complete_and_offline()
    {
        using var summary = Load("summary.v1.json");
        using var residuals = Load("residuals.v1.json");
        var root = summary.RootElement;
        var inventory = residuals.RootElement;

        Assert.Equal("TERMINAL_OFFLINE_GENERICITY_GATE", root.GetProperty("status").GetString());
        Assert.Equal((425, 22, 34), (
            root.GetProperty("baseline").GetProperty("tp").GetInt32(),
            root.GetProperty("baseline").GetProperty("fp").GetInt32(),
            root.GetProperty("baseline").GetProperty("fn").GetInt32()));
        Assert.Equal(0.9381898454746137, root.GetProperty("baseline").GetProperty("f1").GetDouble(), 12);
        Assert.Equal(56, inventory.GetProperty("counts").GetProperty("allResidualRows").GetInt32());
        Assert.Equal(34, inventory.GetProperty("counts").GetProperty("falseNegatives").GetInt32());
        Assert.Equal(22, inventory.GetProperty("counts").GetProperty("falsePositives").GetInt32());
        Assert.Equal(0, root.GetProperty("providerCalls").GetInt32());
        Assert.Equal(0, root.GetProperty("modelCalls").GetInt32());
        Assert.Equal("PASS", root.GetProperty("goldFirewall").GetString());
        Assert.False(root.GetProperty("runtimeGoldLeakage").GetBoolean());

        var initial = root.GetProperty("initialResiduals");
        Assert.Equal(16, initial.GetProperty("MODEL_OMISSION").GetInt32());
        Assert.Equal(5, initial.GetProperty("MODEL_WRONG_TEXT_BOUNDARY").GetInt32());
        Assert.Equal(3, initial.GetProperty("MODEL_WRONG_TEXT").GetInt32());
        Assert.Equal(10, initial.GetProperty("SYSTEM_AMBIGUOUS_DUPLICATE_TEXT").GetInt32());
        Assert.Equal(22, initial.GetProperty("MODEL_TRUE_EXTRA").GetInt32());
    }

    [Fact]
    public void Every_candidate_is_explicitly_rejected_before_provider_inference()
    {
        using var summary = Load("summary.v1.json");
        var experiments = summary.RootElement.GetProperty("experiments").EnumerateArray().ToArray();
        Assert.Equal(4, experiments.Length);
        foreach (var experiment in experiments)
        {
            Assert.Equal("NO_GENERIC_SAFE_INTERVENTION", experiment.GetProperty("decision").GetString());
            Assert.False(experiment.GetProperty("modelVisibleChange").GetBoolean());
            Assert.False(experiment.GetProperty("freshInferenceRequired").GetBoolean());
            Assert.Equal(0, experiment.GetProperty("systemLoss").GetInt32());
        }
        Assert.Equal("MODEL_OMISSION", summary.RootElement.GetProperty("nextLargestProvenLoss").GetString());
        Assert.Equal("A99_NOT_MEASURED_DEV_MARGIN_BELOW_0.995", summary.RootElement.GetProperty("a99Status").GetString());
    }

    [Fact]
    public void Residual_rows_preserve_source_context_and_repeat_masks()
    {
        using var residuals = Load("residuals.v1.json");
        var rows = residuals.RootElement.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Contains(rows, x => x.GetProperty("kind").GetString() == "FN" && x.GetProperty("context").GetString()!.Length > 0);
        Assert.Contains(rows, x => x.GetProperty("kind").GetString() == "FP" && x.GetProperty("modelTexts").GetArrayLength() > 0);
        Assert.All(rows, x => Assert.Matches("^[01]{3}$", x.GetProperty("repeatMask").GetString()!));
        Assert.All(rows, x => Assert.False(x.GetProperty("finalPrediction").GetBoolean() && x.GetProperty("kind").GetString() == "FN"));
    }

    [Fact]
    public void Duplicate_exact_text_requires_a_valid_discriminator_and_never_guesses()
    {
        var aliases = new[] { new SemanticTextSourceAlias("S1", "body/p1", 1, "Africa Africa") };

        var ambiguous = SemanticTextExactBinder.Bind([new("S1", "Africa", "SECTION")], aliases, out var ambiguousAudit);
        Assert.Empty(ambiguous);
        Assert.Equal(SemanticTextBindingStatus.AMBIGUOUS_EXACT_TEXT, Assert.Single(ambiguousAudit).Status);

        var selected = SemanticTextExactBinder.Bind([new("S1", "Africa", "SECTION", 2)], aliases, out var selectedAudit);
        Assert.Equal(7, Assert.Single(selected).Start);
        Assert.Equal(SemanticTextBindingStatus.BOUND, Assert.Single(selectedAudit).Status);

        var invalid = SemanticTextExactBinder.Bind([new("S1", "Africa", "SECTION", 3)], aliases, out var invalidAudit);
        Assert.Empty(invalid);
        Assert.Equal(SemanticTextBindingStatus.AMBIGUOUS_EXACT_TEXT, Assert.Single(invalidAudit).Status);
    }

    [Fact]
    public void Unique_text_and_utf16_span_behavior_remain_exact_only()
    {
        var aliases = new[] { new SemanticTextSourceAlias("S1", "body/p1", 1, "😀\nHeading") };
        var bound = SemanticTextExactBinder.Bind([new("S1", "Heading", "SECTION")], aliases, out _);
        var item = Assert.Single(bound);
        Assert.Equal(3, item.Start);
        Assert.Equal(10, item.End);

        var fuzzy = SemanticTextExactBinder.Bind([new("S1", "Headinx", "SECTION")], aliases, out var audit);
        Assert.Empty(fuzzy);
        Assert.Equal(SemanticTextBindingStatus.TEXT_NOT_FOUND, Assert.Single(audit).Status);
    }

    [Fact]
    public void Gold_firewall_reasoning_and_keep_revert_contracts_are_persisted()
    {
        using var baselineFreeze = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "eval/a99-closed-loop/semantic-text-generalization/DOC-0001/r1/freeze.v1.json")));
        Assert.False(baselineFreeze.RootElement.GetProperty("goldReadBeforeFreeze").GetBoolean());
        Assert.True(baselineFreeze.RootElement.GetProperty("reasoningConfiguration").GetProperty("enabled").GetBoolean());
        Assert.DoesNotContain("Western Asia", SemanticTextExactBindingContract.System, StringComparison.OrdinalIgnoreCase);

        using var summary = Load("summary.v1.json");
        Assert.All(summary.RootElement.GetProperty("experiments").EnumerateArray(), experiment =>
        {
            Assert.Equal(0, experiment.GetProperty("systemLoss").GetInt32());
            Assert.False(experiment.GetProperty("modelVisibleChange").GetBoolean());
        });
    }

    private static JsonDocument Load(string name) => JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), Root, name)));
    private static string RepoRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
}
