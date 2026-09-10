using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

public sealed class ResidualErrorAttributionTests
{
    [Fact]
    public void Occurrence_matrix_preserves_three_repeat_attribution()
    {
        using var artifact = Load("eval/a99-closed-loop/residual-error-attribution/occurrence-matrix.v1.json");
        var root = artifact.RootElement;
        Assert.True(root.GetProperty("hashVerified").GetBoolean());
        Assert.Equal(15, root.GetProperty("frozenCellCount").GetInt32());
        Assert.False(root.GetProperty("goldReadBeforeFreeze").GetBoolean());
        Assert.Equal(153, root.GetProperty("rows").GetArrayLength());

        var stability = root.GetProperty("stability");
        Assert.Equal(1, stability.GetProperty("persistent3of3").GetInt32());
        Assert.Equal(1, stability.GetProperty("stochastic2of3").GetInt32());
        Assert.Equal(0, stability.GetProperty("stochastic1of3").GetInt32());
        Assert.Equal(139, stability.GetProperty("alwaysFound").GetInt32());
        Assert.Equal(7, stability.GetProperty("systemAffected").GetInt32());
        Assert.Equal(5, stability.GetProperty("unresolved").GetInt32());
        Assert.Equal(5, stability.GetProperty("resolvedModelWrongSpan").GetInt32());

        var row = root.GetProperty("rows").EnumerateArray().First();
        var repeat = row.GetProperty("repeats").EnumerateArray().First();
        foreach (var property in new[] { "modelRawExact", "modelRawNear", "bound", "validated", "projected", "final", "exactFinalKey" })
            Assert.True(repeat.TryGetProperty(property, out _), property);
    }

    [Fact]
    public void First_loss_and_unresolved_buckets_are_explicit()
    {
        using var artifact = Load("eval/a99-closed-loop/residual-error-attribution/first-loss.v1.json");
        var root = artifact.RootElement;
        Assert.Equal(7, root.GetProperty("systemFirstLossCounts").GetProperty("BINDER_LOSS").GetInt32());
        Assert.Equal(5, root.GetProperty("unresolvedResolutionCounts").GetProperty("MODEL_WRONG_SPAN").GetInt32());
        Assert.Equal(12, root.GetProperty("rows").GetArrayLength());
        Assert.All(root.GetProperty("rows").EnumerateArray(), row => Assert.True(row.GetProperty("perRepeat").GetArrayLength() == 3));
    }

    [Fact]
    public void Oracle_residuals_keep_bucket_and_support_diagnostics_offline()
    {
        using var artifact = Load("eval/a99-closed-loop/residual-error-attribution/oracle-residual.v1.json");
        var root = artifact.RootElement;
        Assert.True(root.GetProperty("diagnosticOnly").GetBoolean());
        Assert.Equal(144, root.GetProperty("union").GetProperty("tp").GetInt32());
        Assert.Equal(10, root.GetProperty("union").GetProperty("fp").GetInt32());
        Assert.Equal(9, root.GetProperty("union").GetProperty("fn").GetInt32());

        var fn = root.GetProperty("falseNegativeBuckets");
        Assert.Equal(7, fn.GetProperty("SYSTEM_LOSS").GetInt32());
        Assert.Equal(1, fn.GetProperty("PERSISTENT_MODEL_OMISSION").GetInt32());
        Assert.Equal(1, fn.GetProperty("MODEL_WRONG_SPAN").GetInt32());

        var fp = root.GetProperty("falsePositiveBuckets");
        Assert.Equal(8, fp.GetProperty("TRUE_EXTRA").GetInt32());
        Assert.Equal(1, fp.GetProperty("WRONG_SOURCE_DUPLICATE_TEXT").GetInt32());
        Assert.Equal(1, fp.GetProperty("SUPERSET_GOLD_HEADING").GetInt32());
        Assert.Equal(1, root.GetProperty("falsePositiveStability").GetProperty("PRESENT_1_OF_3").GetInt32());
        Assert.Equal(6, root.GetProperty("falsePositiveStability").GetProperty("PRESENT_2_OF_3").GetInt32());
        Assert.Equal(3, root.GetProperty("falsePositiveStability").GetProperty("PRESENT_3_OF_3").GetInt32());

        foreach (var row in root.GetProperty("falsePositiveRows").EnumerateArray())
            Assert.Equal(row.GetProperty("supportingRepeats").GetArrayLength(), row.GetProperty("supportCount").GetInt32());
    }

    [Fact]
    public void Reverted_intervention_and_gold_firewall_remain_closed()
    {
        using var summary = Load("eval/a99-closed-loop/residual-error-attribution/summary.v1.json");
        var root = summary.RootElement;
        Assert.Equal("NONE", root.GetProperty("intervention").GetString());
        Assert.Equal("RESIDUAL_MIXED_CAUSES", root.GetProperty("finalClassification").GetString());
        Assert.Equal("NEXT_MODEL_ACTION_NONE_YET", root.GetProperty("nextModelAction").GetString());
        Assert.False(root.GetProperty("goldReadBeforeFreeze").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("pairedReplay").ValueKind);

        using var reverted = Load("eval/a99-closed-loop/model-omission-stability/summary.v1.json");
        Assert.Equal("INTERVENTION_REVERTED", reverted.RootElement.GetProperty("finalClassification").GetString());
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
