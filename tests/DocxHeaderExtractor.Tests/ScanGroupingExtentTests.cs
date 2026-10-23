using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Contracts.Models;
using DocxHeaderExtractor.Contracts.Scan;
using DocxHeaderExtractor.HeadingAgent.Scan;
using DocxHeaderExtractor.HeadingAgent.Grouping;
using DocxHeaderExtractor.HeadingAgent.Runtime;
using DocxHeaderExtractor.HeadingAgent.Selectors;
using DocxHeaderExtractor.Agents;
using DocxHeaderExtractor.Agents.Budget;

namespace DocxHeaderExtractor.Tests;

public sealed class ScanGroupingExtentTests
{
    internal static ScanAgentSource Source()
    {
        OcrView View(string id, string first) => new(new(id, "none", 1, "fixture", "1", "en", 1), EvidenceOrigin.OcrDerived,
            [new(1, new string('a', 64), 500, 600, new string('b', 64),
                [new($"P001-{id}-L001", first, new(20, 30, 300, 20), 95, 95, false, []),
                 new($"P001-{id}-L002", "Second heading", new(20, 60, 300, 20), 95, 95, false, [])])]);
        return new(new(ScanOcrEvidence.CurrentVersion, new string('c', 64), "ScannedPdf", 1, new string('d', 64),
            [View("ocr-original", "OCR"), View("ocr-upscaled", "prefix 😀 Heading tail")], []), []);
    }

    static string Reply(JsonNode payload) => JsonSerializer.Serialize(new { protocolVersion = AgentGrouping.GroupVersion,
        runs = payload["runs"]!.AsArray().Select(r => new { run = r!["run"]!.GetValue<string>(),
            headings = r["lines"]!.AsArray().Select(l => new[] { l!["alias"]!.GetValue<string>() }) }) });

    [Fact]
    public void Selection_grouping_and_binding_use_offsets_in_the_same_selected_reading()
    {
        var source = Source();
        const string alias = "P001-L001";
        var reading = source.Readings(alias)["ocr-upscaled"];
        var start = reading.IndexOf("Heading", StringComparison.Ordinal);
        var sawGroup = false;
        AgentUnit.Model model = (unit, turn, request) =>
        {
            var payload = JsonNode.Parse(JsonNode.Parse(request.Body)!["messages"]![1]!["content"]!.Text())!;
            if (payload["stage"]!.GetValue<string>() == "GROUP")
            {
                sawGroup = true;
                var line = payload["runs"]![0]!["lines"]![0]!;
                Assert.Equal("Heading", line["text"]!.GetValue<string>());
                Assert.Equal("Heading", line["selectedText"]!.GetValue<string>());
                Assert.Equal("ocr-upscaled", line["readingViewId"]!.GetValue<string>());
                Assert.Equal(reading, line["sourceText"]!.GetValue<string>());
                Assert.Equal(start, line["headingExtent"]!["start"]!.GetValue<int>());
                Assert.Equal(300, line["layout"]!["width"]!.GetValue<int>());
                return new(Reply(payload), [], "stop");
            }
            return new(JsonSerializer.Serialize(new { protocolVersion = source.Flavor.AnswerVersion, status = "COMPLETE",
                decisions = payload["targets"]!.AsArray().Select(t => new { alias = t!["alias"]!.GetValue<string>(), decision = "SELECT",
                    evidenceRefs = new[] { t["alias"]!.GetValue<string>() }, readingViewId = t["alias"]!.GetValue<string>() == alias ? "ocr-upscaled" : "ocr-original",
                    headingExtent = t["alias"]!.GetValue<string>() == alias ? new { start, end = start + 7, text = "Heading" } : null }) },
                new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }), [], "stop");
        };
        var selection = AgentSelector.Select(source, model, new(100, 100), 1, CancellationToken.None, 0, () => 0);
        Assert.Empty(selection.UnitFailures);
        Assert.True(sawGroup);
        var (headings, rejected) = ScanSelector.Headings(source, selection);
        Assert.Equal(0, rejected);
        Assert.Equal("Heading", headings[0].Text);
        Assert.Equal(start, headings[0].Parts[0].ExtentStart);
        Assert.Equal("ocr-upscaled", headings[0].Parts[0].ViewId);
        Assert.Equal(reading, ScanSelector.PrepareSelection(source, selection).Lines[0].Text);
    }

    [Fact]
    public void Invalid_reading_or_extent_never_falls_back_to_the_default_reading()
    {
        var source = Source();
        Assert.Throws<InvalidOperationException>(() => source.Flavor.GroupLineItem(source, 0, null, "unknown"));
        Assert.Throws<InvalidOperationException>(() => source.Flavor.GroupLineItem(source, 0, new(0, 999), "ocr-upscaled"));
        var whole = JsonSerializer.SerializeToNode(source.Flavor.GroupLineItem(source, 0, null, "ocr-upscaled"))!;
        Assert.Equal(source.Readings("P001-L001")["ocr-upscaled"], whole["text"]!.GetValue<string>());
        Assert.Equal("OCR", source.Lines[0].Text);
    }

    [Fact]
    public void Cache_key_changes_with_the_reading_even_when_offsets_do_not_change()
    {
        var source = Source();
        var runs = new[] { new AgentGrouping.Run("R1", [0, 1]) };
        var extents = new Dictionary<string, TextOffsetSpan> { ["P001-L001"] = new(0, 2) };
        var first = AgentGrouping.BatchKey(source, runs, 1, "instruction", extents, new Dictionary<string, string> { ["P001-L001"] = "ocr-original" });
        var second = AgentGrouping.BatchKey(source, runs, 1, "instruction", extents, new Dictionary<string, string> { ["P001-L001"] = "ocr-upscaled" });
        Assert.NotEqual(first, second);
    }
}
