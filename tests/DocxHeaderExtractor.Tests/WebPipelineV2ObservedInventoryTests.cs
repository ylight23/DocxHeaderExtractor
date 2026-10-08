using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

public sealed class WebPipelineV2ObservedInventoryTests
{
    [Fact]
    public void Existing_run_inventory_freezes_physical_overlap_without_claiming_raw_replay_or_Gold_accuracy()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(
            "artifacts/web-pdf-semantic-diagnostic/run-1c32edb4.inventory.v1.json")));
        var root = document.RootElement;
        Assert.Equal("OBSERVED_OUTPUT_INVENTORY_NOT_RAW_REPLAY", root.GetProperty("comparisonType").GetString());
        Assert.False(root.GetProperty("rawStageCapturesAvailable").GetBoolean());
        Assert.Equal("NOT_EVALUABLE", root.GetProperty("rawStageAttribution").GetString());
        Assert.Equal("NOT_RECORDED", root.GetProperty("placementExecutionStatus").GetString());
        Assert.Null(root.GetProperty("sourceFileSha256").GetString());
        Assert.Equal(0, root.GetProperty("providerCallsDuringInventory").GetInt32());
        Assert.Equal("NONE", root.GetProperty("goldMutation").GetString());
        Assert.Equal("NOT_SCORED", root.GetProperty("giayMoiFalseNegativeGoldScore").GetString());
        var identities = root.GetProperty("identities").EnumerateArray().ToDictionary(
            value => value.GetProperty("ordinal").GetInt32(), value => value.GetProperty("sourceIdSha256").GetString()!);
        Assert.All(identities.Values, value => Assert.Matches("^[a-f0-9]{64}$", value));
        Assert.Equal(identities.Count, identities.Values.Distinct().Count());
        var headings = root.GetProperty("headings").EnumerateArray().ToDictionary(
            value => value.GetProperty("diagnosticKey").GetString()!, value => value.GetProperty("parts").EnumerateArray()
                .Select(part => (identities[part.GetProperty("ordinal").GetInt32()],
                    part.GetProperty("start").GetInt32(), part.GetProperty("end").GetInt32())).ToArray());
        Assert.Equal(root.GetProperty("acceptedHeadingCount").GetInt32(), headings.Count);
        Assert.Equal(3, headings["H3"].Length);
        Assert.Contains(Assert.Single(headings["H4"]), headings["H3"]);
        Assert.Contains(Assert.Single(headings["H5"]), headings["H3"]);
        Assert.Equal(2, root.GetProperty("exactSourceSelectionOverlaps").GetArrayLength());
    }
}
