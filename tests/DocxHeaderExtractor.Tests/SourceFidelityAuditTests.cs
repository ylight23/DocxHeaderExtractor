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

    private static string TestRoot() => Directory.GetParent(AppContext.BaseDirectory)!.Parent!.Parent!.Parent!.FullName;
}
