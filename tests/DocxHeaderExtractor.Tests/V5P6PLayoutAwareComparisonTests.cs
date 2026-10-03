using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>Offline, Gold-read-after-hash-freeze comparison of P6P text context with P6P-L layout facts.</summary>
public sealed class V5P6PLayoutAwareComparisonTests
{
    private const string LayoutRoot = "artifacts/v5-p6pl-layout-aware-pdf";
    private const string TextScore = "artifacts/v5-p6p-document-aware-pdf/gold-score-after-pack-rerun.v1.json";

    [Fact]
    public void Layout_aware_full31_is_a_clean_same_wire_observation_and_does_not_reduce_SRC095_false_positives()
    {
        using var text = Read(TextScore);
        using var layout = Read($"{LayoutRoot}/gold-score.v1.json");
        using var manifest = Read($"{LayoutRoot}/execution-manifest.v1.json");
        using var result = Read($"{LayoutRoot}/result.v1.json");
        using var freeze = Read($"{LayoutRoot}/response-hash-freeze.v1.json");

        Assert.Equal(31, result.RootElement.GetProperty("logicalProviderCalls").GetInt32());
        Assert.Equal(31, result.RootElement.GetProperty("rows").GetArrayLength());
        Assert.False(result.RootElement.GetProperty("goldRead").GetBoolean());
        Assert.True(freeze.RootElement.GetProperty("verifiedBeforeGoldRead").GetBoolean());
        Assert.True(manifest.RootElement.GetProperty("treatment").GetProperty("context").GetProperty("perOccurrenceNeutralLayoutFacts").GetBoolean());
        Assert.All(result.RootElement.GetProperty("rows").EnumerateArray(), row =>
        {
            Assert.Equal("stop", row.GetProperty("finishReason").GetString());
            Assert.True(row.GetProperty("transportAccepted").GetBoolean());
            Assert.True(row.GetProperty("parserAccepted").GetBoolean());
            Assert.True(row.GetProperty("contractValid").GetBoolean());
            Assert.Equal(0, row.GetProperty("quarantinedOccurrences").GetInt32());
        });

        var textMetric = text.RootElement.GetProperty("metric");
        var layoutMetric = layout.RootElement.GetProperty("metric");
        var text095 = Document(text.RootElement, "SRC-095").GetProperty("exact");
        var layout095 = Document(layout.RootElement, "SRC-095").GetProperty("exact");
        var textStructural = text.RootElement.GetProperty("goldAxisRecovery").GetProperty("STRUCTURE|SECTION|TITLE");
        var layoutStructural = layout.RootElement.GetProperty("goldAxisRecovery").GetProperty("STRUCTURE|SECTION|TITLE");

        Assert.Equal(101, I(text095, "falsePositives"));
        Assert.Equal(108, I(layout095, "falsePositives"));
        Assert.Equal(117, I(textStructural, "p6pTp"));
        Assert.Equal(103, I(layoutStructural, "p6pTp"));

        FreezeArtifact.AssertJson(LayoutRoot, "p6p-text-versus-layout-comparison.v1.json", new
        {
            schemaVersion = "v5-p6pl-text-versus-neutral-layout-full31-comparison-v1",
            treatment = new
            {
                same = new[] { "model", "provider pin", "temperature", "reasoning enabled with effort omitted", "P05 pack partition", "document context", "system prompt", "sourceParts locator contract", "parser/binder", "response cap" },
                onlyAddedToP6PL = new[] { "per-occurrence parser-observed page", "relative vertical position", "horizontal geometry and width", "line count", "bold ratio", "font-size/body ratio", "same-normalized-text recurrence" },
                semanticLabelsAdded = false,
                goldMutated = false,
            },
            execution = new
            {
                p6plPrimaryCalls = result.RootElement.GetProperty("logicalProviderCalls").GetInt32(),
                p6plAllTransportStop = result.RootElement.GetProperty("rows").EnumerateArray().All(row => row.GetProperty("finishReason").GetString() == "stop"),
                p6plAllContractValid = result.RootElement.GetProperty("rows").EnumerateArray().All(row => row.GetProperty("contractValid").GetBoolean()),
                p6plHashVerifiedBeforeGold = freeze.RootElement.GetProperty("verifiedBeforeGoldRead").GetBoolean(),
                providerCallsDuringComparison = 0,
            },
            exact = new
            {
                p6pText = Metric(textMetric),
                p6plLayout = Metric(layoutMetric),
                deltaLayoutMinusText = Delta(textMetric, layoutMetric),
            },
            src095 = new
            {
                p6pText = Metric(text095),
                p6plLayout = Metric(layout095),
                deltaLayoutMinusText = Delta(text095, layout095),
                falsePositiveReductionObserved = I(layout095, "falsePositives") < I(text095, "falsePositives"),
            },
            structuralSectionRecovery = new
            {
                gold = I(textStructural, "gold"), p6pTextTp = I(textStructural, "p6pTp"), p6plLayoutTp = I(layoutStructural, "p6pTp"),
                deltaTp = I(layoutStructural, "p6pTp") - I(textStructural, "p6pTp"),
                retainedAtLeast92Of93 = I(layoutStructural, "p6pTp") >= 92,
            },
            verdict = "P6PL_NEUTRAL_LAYOUT_FACTS_DID_NOT_REDUCE_SRC095_FALSE_POSITIVES_AND_REDUCED_STRUCTURAL_SECTION_RECOVERY; NOT_A_PROMOTION_CANDIDATE",
        });
    }

    private static JsonDocument Read(string path) => JsonDocument.Parse(File.ReadAllText(TestRepository.Path(path)));
    private static JsonElement Document(JsonElement root, string documentId) => root.GetProperty("documents").EnumerateArray().Single(row => row.GetProperty("documentId").GetString() == documentId);
    private static int I(JsonElement value, string name) => value.GetProperty(name).GetInt32();
    private static double D(JsonElement value, string name) => value.GetProperty(name).GetDouble();
    private static object Metric(JsonElement value) => new { tp = I(value, "truePositives"), fp = I(value, "falsePositives"), fn = I(value, "falseNegatives"), precision = D(value, "truePrecision"), recall = D(value, "trueRecall"), f1 = D(value, "f1") };
    private static object Delta(JsonElement baseline, JsonElement candidate) => new { tp = I(candidate, "truePositives") - I(baseline, "truePositives"), fp = I(candidate, "falsePositives") - I(baseline, "falsePositives"), fn = I(candidate, "falseNegatives") - I(baseline, "falseNegatives"), precision = Math.Round(D(candidate, "truePrecision") - D(baseline, "truePrecision"), 4), recall = Math.Round(D(candidate, "trueRecall") - D(baseline, "trueRecall"), 4), f1 = Math.Round(D(candidate, "f1") - D(baseline, "f1"), 4) };
}
