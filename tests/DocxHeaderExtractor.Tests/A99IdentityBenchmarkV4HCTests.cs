using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

public sealed class A99IdentityBenchmarkV4HCTests
{
    private static string RepositoryRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));

    [Fact]
    public void V4H_C_artifacts_are_complete_and_exactly_joined()
    {
        var root = RepositoryRoot();
        var manifestPath = Path.Combine(root, "artifacts", "identity-benchmark", "v4", "semantic-adjudication", "evaluation", "manifest.json");
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var value = manifest.RootElement;
        Assert.Equal("V4H_C_COMPLETE", value.GetProperty("status").GetString());
        Assert.Equal(128, value.GetProperty("itemCount").GetInt32());
        Assert.True(value.GetProperty("exactJoin").GetBoolean());
        Assert.Equal(0, value.GetProperty("modelCalls").GetInt32());
        Assert.Equal(0, value.GetProperty("providerCalls").GetInt32());
    }

    [Fact]
    public void V4H_C_keeps_invalid_provider_cells_out_of_relation_labels()
    {
        var root = RepositoryRoot();
        var summaryPath = Path.Combine(root, "artifacts", "identity-benchmark", "v4", "semantic-adjudication", "evaluation", "summary.json");
        using var summary = JsonDocument.Parse(File.ReadAllText(summaryPath));
        var prediction = summary.RootElement.GetProperty("prediction");
        var overall = summary.RootElement.GetProperty("overall");
        Assert.Equal(95, prediction.GetProperty("validCount").GetInt32());
        Assert.Equal(33, prediction.GetProperty("invalidCount").GetInt32());
        Assert.Equal(71, overall.GetProperty("correct").GetInt32());
        Assert.Equal(128, overall.GetProperty("total").GetInt32());
    }

    [Fact]
    public void V4H_C_sanity_cases_preserve_frozen_user_final_relations()
    {
        var root = RepositoryRoot();
        var path = Path.Combine(root, "artifacts", "identity-benchmark", "v4", "semantic-adjudication", "evaluation", "sanity-cases.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var byId = doc.RootElement.EnumerateArray().ToDictionary(x => x.GetProperty("reviewId").GetString()!);
        Assert.Equal("DISTINCT", byId["SA-0067"].GetProperty("goldRelation").GetString());
        Assert.Equal("DISTINCT", byId["SA-0108"].GetProperty("goldRelation").GetString());
        Assert.Equal("CONTINUATION_OF", byId["SA-0040"].GetProperty("goldRelation").GetString());
    }
}
