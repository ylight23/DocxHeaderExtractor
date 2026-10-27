using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.HeadingAgent.Formats;
using DocxHeaderExtractor.HeadingAgent.Tools;

namespace DocxHeaderExtractor.HeadingAgent.Runtime;

/// <summary>Executes source reads before the first classification request. These are program reads, never fabricated model tool calls.</summary>
internal static class PdfSelectionEvidence
{
    internal const string Version = "PDF_SELECTION_EVIDENCE_PREFLIGHT_V1";
    const int Neighbours = 6;

    internal static string Attach(string payload, IAgentSource source, IAgentTools tools, IReadOnlyList<string> targets)
    {
        if (!source.Flavor.PreloadPageLayoutEvidence) return payload;
        var results = new JsonArray();
        var windows = new JsonArray();
        foreach (var group in targets.Select(a => source.Lines[tools.PositionOf(a)]).GroupBy(l => l.Page))
        {
            if (group.Key is not { } page) throw new InvalidOperationException("PDF_EVIDENCE_PAGE_MISSING");
            var pageLines = source.Lines.Where(l => l.Page == page).ToArray();
            var positions = pageLines.Select((l, i) => (l.Alias, i)).ToDictionary(x => x.Alias, x => x.i);
            var first = Math.Max(0, group.Min(l => positions[l.Alias]) - Neighbours);
            var end = Math.Min(pageLines.Length, group.Max(l => positions[l.Alias]) + Neighbours + 1);
            windows.Add(new JsonObject { ["page"] = page, ["firstAlias"] = pageLines[first].Alias,
                ["lastAlias"] = pageLines[end - 1].Alias, ["offset"] = first, ["endOffsetExclusive"] = end });
            for (var offset = first; offset < end;)
            {
                var args = JsonSerializer.Serialize(new { page, offset, limit = Math.Min(AgentTools.MaxPageLines, end - offset) });
                var output = tools.Execute("P0." + (results.Count + 1).ToString("00"), AgentTools.PageLayout, args);
                if (output.Status != "OK") throw new InvalidOperationException("PDF_EVIDENCE_READ_FAILED:" + output.ErrorCode);
                var result = JsonNode.Parse(Encoding.UTF8.GetString(output.Content))!.AsObject();
                var count = result["result"]!["lines"]!.AsArray().Count;
                if (count == 0) throw new InvalidOperationException("PDF_EVIDENCE_READ_NO_PROGRESS");
                results.Add(result);
                offset += count;
            }
        }
        var covered = targets.Where(tools.PageLayoutAliasesReturned.Contains).ToArray();
        if (covered.Length != targets.Count) throw new InvalidOperationException("PDF_EVIDENCE_TARGET_COVERAGE_INCOMPLETE");
        var packet = JsonNode.Parse(payload)!.AsObject();
        packet["sourceEvidence"] = new JsonObject
        {
            ["version"] = Version, ["origin"] = "program-executed-read-only-tools",
            ["complete"] = true, ["semanticLabels"] = "NONE", ["toolCalls"] = results.Count,
            ["coveredTargetAliases"] = new JsonArray(covered.Select(a => (JsonNode)JsonValue.Create(a)!).ToArray()),
            ["windows"] = windows, ["results"] = results
        };
        return packet.ToJsonString(AgentUnit.ReviewJson);
    }
}
