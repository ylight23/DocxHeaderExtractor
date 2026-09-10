using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

public sealed class SemanticTextGeneralizationGateTests
{
    [Fact]
    public void Dynamic_cohort_and_gold_firewall_are_persisted()
    {
        using var manifest = Load("eval/a99-closed-loop/semantic-text-generalization/manifest.v1.json");
        using var summary = Load("eval/a99-closed-loop/semantic-text-generalization/summary.v1.json");
        var cohort = manifest.RootElement.GetProperty("selectedCohort").EnumerateArray().Select(x => x.GetProperty("documentId").GetString()).ToArray();

        Assert.Equal(new[] { "DOC-0001", "DOC-0205", "DOC-0252", "DOC-0256", "DOC-0258" }, cohort);
        Assert.Equal("canonical strict-gold metadata-only evaluator", manifest.RootElement.GetProperty("eligibilityPolicy").GetString());
        Assert.False(manifest.RootElement.GetProperty("goldExactRowsReadBeforeFreeze").GetBoolean());
        Assert.Equal(153, summary.RootElement.GetProperty("totalGoldOccurrences").GetInt32());
        Assert.Equal(15, summary.RootElement.GetProperty("modelCalls").GetInt32());
        Assert.Equal("PASS", summary.RootElement.GetProperty("goldFirewall").GetString());
        Assert.False(summary.RootElement.GetProperty("a99DevMarginMet").GetBoolean());
        Assert.Equal("SEMANTIC_TEXT_CONTRACT_DOES_NOT_GENERALIZE", summary.RootElement.GetProperty("generalizationClassification").GetString());
    }

    [Fact]
    public void Micro_and_per_document_scores_are_complete_for_all_repeats()
    {
        using var artifact = Load("eval/a99-closed-loop/semantic-text-generalization/repeat-summary.v1.json");
        var root = artifact.RootElement;
        Assert.Equal(5, root.GetProperty("cohortSize").GetInt32());
        Assert.Equal(3, root.GetProperty("repeatCount").GetInt32());
        Assert.Equal(5, root.GetProperty("documents").GetArrayLength());
        Assert.Equal(3, root.GetProperty("cohortMicroByRepeat").GetArrayLength());

        var micro = root.GetProperty("cohortMicroByRepeat").EnumerateArray().ToDictionary(x => x.GetProperty("repeat").GetString()!);
        Assert.Equal((143, 7, 10), (micro["r1"].GetProperty("tp").GetInt32(), micro["r1"].GetProperty("fp").GetInt32(), micro["r1"].GetProperty("fn").GetInt32()));
        Assert.Equal((142, 8, 11), (micro["r2"].GetProperty("tp").GetInt32(), micro["r2"].GetProperty("fp").GetInt32(), micro["r2"].GetProperty("fn").GetInt32()));
        Assert.Equal((143, 17, 10), (micro["r3"].GetProperty("tp").GetInt32(), micro["r3"].GetProperty("fp").GetInt32(), micro["r3"].GetProperty("fn").GetInt32()));
        Assert.Equal(0.9137380191693291, micro["r3"].GetProperty("f1").GetDouble(), 12);

        foreach (var document in root.GetProperty("documents").EnumerateArray())
            Assert.Equal(3, document.GetProperty("repeats").GetArrayLength());
    }

    [Fact]
    public void First_loss_forensics_identify_the_next_generic_escalation_targets()
    {
        using var artifact = Load("eval/a99-closed-loop/semantic-text-generalization/persistent-errors.v1.json");
        var root = artifact.RootElement;
        var persistent = root.GetProperty("persistentErrorCounts");
        Assert.Equal(7, persistent.GetProperty("MODEL_OMISSION").GetInt32());
        Assert.Equal(2, persistent.GetProperty("AMBIGUOUS_DUPLICATE_TEXT").GetInt32());
        Assert.Equal(1, persistent.GetProperty("MODEL_WRONG_SPAN").GetInt32());
        Assert.Equal(1, root.GetProperty("intermittentErrorCounts").GetProperty("MODEL_WRONG_SPAN").GetInt32());
        Assert.Equal(11, root.GetProperty("intermittentFalsePositiveCount").GetInt32());
    }

    [Fact]
    public void Every_live_cell_is_frozen_before_gold_with_verified_hashes()
    {
        var root = Root();
        var cells = Directory.GetDirectories(Path.Combine(root, "eval", "a99-closed-loop", "semantic-text-generalization"), "r*", SearchOption.AllDirectories)
            .Where(path => File.Exists(Path.Combine(path, "freeze.v1.json")))
            .Select(path => JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "freeze.v1.json"))).RootElement.Clone())
            .ToArray();

        Assert.Equal(15, cells.Length);
        Assert.All(cells, cell =>
        {
            Assert.False(cell.GetProperty("goldReadBeforeFreeze").GetBoolean());
            Assert.Equal("stop", cell.GetProperty("finishReason").GetString());
            Assert.True(cell.GetProperty("reasoningConfiguration").GetProperty("enabled").GetBoolean());
        });
    }

    private static JsonDocument Load(string relativePath) => JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), relativePath)));

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DocxHeaderExtractor.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
