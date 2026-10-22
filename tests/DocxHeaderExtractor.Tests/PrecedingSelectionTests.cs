using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Contracts.Scan;
using DocxHeaderExtractor.Sources.Scan;
using DocxHeaderExtractor.HeadingAgent;
using DocxHeaderExtractor.HeadingAgent.Scan;
using static DocxHeaderExtractor.HeadingAgent.AgentProtocol;

namespace DocxHeaderExtractor.Tests;

/// <summary>A unit whose shown context holds a line the first answers selected is asked again with that line named as precedingSelected; a unit
/// without one, and every call without the option, are unchanged.</summary>
public sealed class PrecedingSelectionTests
{
    // Sixty lines on one page make two units: lines 1-48 and lines 49-60. The first unit selects line 21, which lies in the second unit's context.
    const int LineCount = 60, SelectedIndex = 20, SecondUnitFirstIndex = 48;
    static string A(int index) => $"P023-L{index + 1:000}";

    static ScanAgentSource Source()
    {
        var lines = Enumerable.Range(0, LineCount).Select(i => new OcrLine($"P023-ocr-original-L{i + 1:000}", "Dòng " + (i + 1),
            new PixelBox(100, 100 + i * 50, 1000, 40), 95, 95, false, [])).ToArray();
        var evidence = new ScanOcrEvidence(ScanOcrEvidence.CurrentVersion, new string('a', 64), "ScannedPdf", 1, new string('b', 64),
            [new OcrView(new OcrViewSpec("ocr-original", "none", 1, "test", "1", "vie", 1), EvidenceOrigin.OcrDerived,
                [new OcrPage(23, new string('c', 64), 1600, 2200, new string('d', 64), lines)])], []);
        return new ScanAgentSource(evidence);
    }

    static JsonObject Packet(PreparedRequest request)
    {
        var content = JsonNode.Parse(request.Body)!["messages"]![1]!["content"]!;
        return JsonNode.Parse(content is JsonArray ? content[0]!["text"]!.GetValue<string>() : content.GetValue<string>())!.AsObject();
    }

    /// <summary>Selects only the line at <see cref="SelectedIndex"/>, divides every grouping run into single lines, and keeps every selection request.</summary>
    static (AgentUnit.Model Model, List<JsonObject> Selections) Scripted()
    {
        var selections = new List<JsonObject>();
        AgentUnit.Model model = (_, _, request) =>
        {
            var packet = Packet(request);
            if (packet["runs"] is { } runs)
            {
                var divided = runs.AsArray().Select(r => new
                {
                    run = r!["run"]!.GetValue<string>(),
                    headings = r["lines"]!.AsArray().Select(l => new[] { l!["alias"]!.GetValue<string>() }).ToArray(),
                }).ToArray();
                return new ModelTurn(JsonSerializer.Serialize(new { protocolVersion = AgentGrouping.GroupVersion, runs = divided }), [], "stop");
            }
            selections.Add(packet);
            var decisions = packet["targets"]!.AsArray().Select(t =>
            {
                var alias = t!["alias"]!.GetValue<string>();
                return new { alias, decision = alias == A(SelectedIndex) ? "SELECT" : "NOT_SELECT", evidenceRefs = new[] { alias }, reason = "Scripted decision." };
            }).ToArray();
            return new ModelTurn(JsonSerializer.Serialize(new { protocolVersion = SelectScanVersion, status = "COMPLETE", decisions }), [], "stop");
        };
        return (model, selections);
    }

    static bool AsksSecondUnit(JsonObject packet) => packet["targets"]!.AsArray().Any(t => t!["alias"]!.GetValue<string>() == A(SecondUnitFirstIndex));

    [Fact]
    public void Without_the_option_no_request_names_a_preceding_line()
    {
        var (model, selections) = Scripted();

        var result = HeadingAgentSelector.Select(Source(), model, new AgentBudget(100, 100), 1, CancellationToken.None, 0, () => 0);

        Assert.Equal(2, selections.Count);
        Assert.All(selections, p => Assert.Null(p["precedingSelected"]));
        Assert.Contains(A(SelectedIndex), result.SelectedAliases);
    }

    [Fact]
    public void With_the_option_the_unit_after_a_selected_line_is_asked_again_with_that_line_named()
    {
        var (model, selections) = Scripted();

        var result = HeadingAgentSelector.Select(Source(), model, new AgentBudget(100, 100), 1, CancellationToken.None, 0, () => 0,
            precedingSelection: true);

        var second = selections.Where(AsksSecondUnit).ToArray();
        Assert.Equal(2, second.Length);
        Assert.Null(second[0]["precedingSelected"]);
        var named = second[1]["precedingSelected"]!.AsObject();
        Assert.Equal(A(SelectedIndex), named["alias"]!.GetValue<string>());
        Assert.Equal(23, named["page"]!.GetValue<int>());
        Assert.Equal("Dòng " + (SelectedIndex + 1), named["text"]!.GetValue<string>());
        Assert.Contains(A(SelectedIndex), result.SelectedAliases);
    }

    [Fact]
    public void The_first_unit_has_no_context_before_it_and_is_not_asked_again()
    {
        var (model, selections) = Scripted();

        HeadingAgentSelector.Select(Source(), model, new AgentBudget(100, 100), 1, CancellationToken.None, 0, () => 0, precedingSelection: true);

        Assert.Equal(3, selections.Count);
        Assert.Equal(1, selections.Count(p => p["targets"]!.AsArray().Any(t => t!["alias"]!.GetValue<string>() == A(0))));
    }
}
