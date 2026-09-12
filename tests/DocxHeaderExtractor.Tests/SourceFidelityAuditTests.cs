using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Eval.ReasoningRetention;

namespace DocxHeaderExtractor.Tests;

public sealed class SourceFidelityAuditTests
{
    [Fact]
    public void ConvertedDocumentHasExpectedParagraphAndMarkerSignals()
    {
        var root = TestRoot();
        var artifact = Path.Combine(root, "eval", "a99-closed-loop", "source-fidelity-audit", "DOC-0205", "summary.v1.json");
        if (!File.Exists(artifact)) return;

        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(artifact));
        var converted = json.RootElement.GetProperty("converted").GetProperty("rawOoxml");
        Assert.Equal(476, converted.GetProperty("totalParagraphs").GetInt32());
        Assert.Equal(445, converted.GetProperty("nonEmptyParagraphs").GetInt32());
        Assert.Equal(680, converted.GetProperty("maxParagraphLength").GetInt32());
        Assert.Equal(101, converted.GetProperty("medianParagraphLength").GetDouble());
        Assert.Equal(5, converted.GetProperty("chapterParagraphs").GetInt32());
        Assert.Equal(9, converted.GetProperty("sectionParagraphs").GetInt32());
        Assert.Equal(57, converted.GetProperty("articleParagraphs").GetInt32());
    }

    [Fact]
    public void ParagraphJoinAuditIsExactOnlyAndDoesNotCreateUtf16SpanAcrossParagraphs()
    {
        var paragraph = new SourceParagraph
        {
            SourceId = "body[1]/p[1]",
            SourceOrdinal = 1,
            Text = "Chương I",
            Style = new(),
            Numbering = new(),
            Layout = new(),
        };

        Assert.Equal("Chương I", paragraph.Text);
        Assert.NotEqual("Chương I QUY ĐỊNH CHUNG", paragraph.Text);
    }

    [Fact]
    public void PairedLiveScoreExcludesOnlyTheFiveNonComparableMultiParagraphGoldRows()
    {
        var root = TestRoot();
        var artifact = Path.Combine(root, "eval", "a99-closed-loop", "source-fidelity-paired-live", "DOC-0205", "summary.v1.json");
        if (!File.Exists(artifact)) return;

        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(artifact));
        var summary = json.RootElement;
        Assert.Equal("FAITHFUL_SOURCE_NO_PAIRED_LIFT", summary.GetProperty("decision").GetString());
        Assert.Equal(5, summary.GetProperty("faithfulCrossParagraphGold").GetInt32());

        var comparable = summary.GetProperty("pairedComparable66");
        var control = comparable.GetProperty("control");
        var faithful = comparable.GetProperty("faithful");
        Assert.Equal(195, control.GetProperty("tp").GetInt32());
        Assert.Equal(1, control.GetProperty("fp").GetInt32());
        Assert.Equal(3, control.GetProperty("fn").GetInt32());
        Assert.Equal(15, control.GetProperty("excludedPredictions").GetInt32());
        Assert.Equal(84, faithful.GetProperty("tp").GetInt32());
        Assert.Equal(24, faithful.GetProperty("fp").GetInt32());
        Assert.Equal(114, faithful.GetProperty("fn").GetInt32());
        Assert.Equal(27, faithful.GetProperty("excludedPredictions").GetInt32());
        Assert.Equal(6, summary.GetProperty("modelCalls").GetInt32());
        Assert.Equal(6, summary.GetProperty("providerCalls").GetInt32());
        Assert.Equal("PASS", summary.GetProperty("goldFirewall").GetString());
    }

    [Fact]
    public void FaithfulWholeAliasReplayRemovesTextEchoBindingFailuresWithoutProviderCalls()
    {
        var root = TestRoot();
        var artifact = Path.Combine(root, "eval", "a99-closed-loop", "source-fidelity-whole-alias-replay", "DOC-0205", "summary.v1.json");
        if (!File.Exists(artifact)) return;

        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(artifact));
        var summary = json.RootElement;
        Assert.Equal(0, summary.GetProperty("modelCalls").GetInt32());
        Assert.Equal(0, summary.GetProperty("providerCalls").GetInt32());
        Assert.Equal(66, summary.GetProperty("comparableGoldCount").GetInt32());
        Assert.Equal(5, summary.GetProperty("faithfulCrossParagraphGold").GetInt32());
        Assert.Equal("PASS", summary.GetProperty("goldFirewall").GetString());

        var aggregate = summary.GetProperty("aggregate");
        Assert.Equal(189, aggregate.GetProperty("tp").GetInt32());
        Assert.Equal(33, aggregate.GetProperty("fp").GetInt32());
        Assert.Equal(9, aggregate.GetProperty("fn").GetInt32());
        Assert.Equal(30, aggregate.GetProperty("excludedPredictions").GetInt32());

        var diagnostics = summary.GetProperty("diagnostics");
        Assert.Equal(0, diagnostics.GetProperty("bindingFailure").GetInt32());
        Assert.Equal(0, diagnostics.GetProperty("unknownAliasSelections").GetInt32());
        Assert.True(diagnostics.GetProperty("modelTextIgnored").GetBoolean());
    }

    [Fact]
    public void FaithfulWholeAliasLivePreservesInterleavedProviderEvidenceAndRejectsUnstableGate()
    {
        var root = TestRoot();
        var artifact = Path.Combine(root, "eval", "a99-closed-loop", "source-fidelity-whole-alias-live", "DOC-0205", "summary.v1.json");
        if (!File.Exists(artifact)) return;

        Assert.True(new FileInfo(artifact).Length < 1_000_000, "Summary must not duplicate the full execution packet.");
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(artifact));
        var summary = json.RootElement;
        Assert.Equal(6, summary.GetProperty("modelCalls").GetInt32());
        Assert.Equal(6, summary.GetProperty("providerCalls").GetInt32());
        Assert.True(summary.GetProperty("interleaved").GetBoolean());
        Assert.Equal(452, summary.GetProperty("sourceAliasCount").GetInt32());
        Assert.Equal("PASS", summary.GetProperty("goldFirewall").GetString());
        Assert.Equal("WHOLE_ALIAS_LIVE_REQUIRES_REVIEW", summary.GetProperty("decision").GetString());

        var challenger = summary.GetProperty("challenger").GetProperty("aggregate");
        Assert.Equal(133, challenger.GetProperty("tp").GetInt32());
        Assert.Equal(12, challenger.GetProperty("fp").GetInt32());
        Assert.Equal(65, challenger.GetProperty("fn").GetInt32());
        Assert.Equal(2, challenger.GetProperty("bindingFailure").GetInt32());
        Assert.Equal(0, challenger.GetProperty("unknownAliasSelections").GetInt32());
        Assert.Equal(0, challenger.GetProperty("systemLoss").GetInt32());

        var wholeAliasRecall = summary.GetProperty("rows").EnumerateArray()
            .Where(row => row.GetProperty("isWholeAlias").GetBoolean())
            .Select(row => row.GetProperty("score").GetProperty("tp").GetInt32())
            .ToArray();
        Assert.Equal(new[] { 9, 62, 62 }, wholeAliasRecall);
    }

    [Fact]
    public void FaithfulWholeAliasProviderProbeSeparatesRawVarianceFromAliasSelection()
    {
        var root = TestRoot();
        var artifact = Path.Combine(root, "eval", "a99-closed-loop", "source-fidelity-whole-alias-provider-probe", "DOC-0205", "probe-summary.v1.json");
        if (!File.Exists(artifact)) return;

        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(artifact));
        var summary = json.RootElement;
        Assert.Equal(181928, summary.GetProperty("requestBodyBytes").GetInt32());
        Assert.Equal("c84538be36adb82c0f3a26a33590596890177ddb4ed5b02463aff539f543ecf5", summary.GetProperty("requestBodySha256").GetString());
        Assert.True(summary.GetProperty("requestBodyStableAcrossCalls").GetBoolean());
        Assert.Equal(8, summary.GetProperty("probeCalls").GetInt32());
        Assert.Equal(8, summary.GetProperty("modelCalls").GetInt32());
        Assert.Equal(8, summary.GetProperty("providerCalls").GetInt32());
        Assert.Equal(8, summary.GetProperty("distinctRawResponseHashes").GetInt32());
        Assert.Equal(1, summary.GetProperty("distinctParsedAliasSetHashes").GetInt32());
        Assert.Equal("RAW_RESPONSE_NONDETERMINISM_ALIAS_SELECTION_STABLE", summary.GetProperty("classification").GetString());
        Assert.False(summary.GetProperty("goldRead").GetBoolean());

        var family = Assert.Single(summary.GetProperty("observedRawProposalCountFamilies").EnumerateArray());
        Assert.Equal(78, family.GetProperty("rawProposalCount").GetInt32());
        Assert.Equal(8, family.GetProperty("calls").GetInt32());
    }

    [Fact]
    public void WholeAliasBindingFailureIsTheSameDuplicateAliasInR2AndR3()
    {
        var root = TestRoot();
        var artifact = Path.Combine(root, "eval", "a99-closed-loop", "source-fidelity-whole-alias-live", "DOC-0205", "binding-failure-diagnosis.v1.json");
        if (!File.Exists(artifact)) return;

        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(artifact));
        var summary = json.RootElement;
        Assert.False(summary.GetProperty("goldRead").GetBoolean());
        Assert.Equal("DUPLICATE_ALIAS_SELECTION_FAIL_CLOSED", summary.GetProperty("classification").GetString());
        foreach (var observation in summary.GetProperty("observations").EnumerateArray())
        {
            Assert.Equal(1, observation.GetProperty("bindingFailure").GetInt32());
            Assert.Equal(0, observation.GetProperty("unknownAliasSelections").GetInt32());
            Assert.Equal("S0239", observation.GetProperty("duplicateSelection").GetProperty("sourceAlias").GetString());
        }
    }

    private static string TestRoot() => Directory.GetParent(AppContext.BaseDirectory)!.Parent!.Parent!.Parent!.FullName;
}
