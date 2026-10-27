using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Agents;
using DocxHeaderExtractor.HeadingAgent.Formats;
using DocxHeaderExtractor.HeadingAgent.Protocol;
using DocxHeaderExtractor.HeadingAgent.Runtime;
using DocxHeaderExtractor.HeadingAgent.Selectors;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfBoundaryInstructionTests
{
    [Theory]
    [InlineData("072_ICP_TAG_Minutes_Mar_2025.pdf", 0, 1)]
    [InlineData("076_ICP_IACG08_Minutes_2023.pdf", 12, 2)]
    public void Source_preflight_completes_pagination_before_first_classification_without_fabricated_model_calls(string file, int segment, int minimumReads)
    {
        var path = TestRepository.Path("todo10_8/heading_corpus_100/05_bien_ban_hop/" + file);
        var source = AgentSource.Load(path, out _, instruction: PdfMembershipInstructionV9.Instruction,
            sourceBound: true, preloadPageLayoutEvidence: true)!;
        var previous = AgentSource.Load(path, out _, instruction: PdfMembershipInstructionV8.Instruction,
            sourceBound: true, requirePageLayoutEvidence: true)!;
        Assert.Equal(previous.Scope(segment).Decide, source.Scope(segment).Decide);
        var targets = source.Scope(segment).Decide;
        var measured = AgentUnit.PrepareFirstRequest(source, segment, PdfMembershipInstructionV9.Instruction);
        var calls = 0;
        ModelTurn Model(string _, int turn, PreparedRequest request)
        {
            calls++;
            Assert.Equal(1, turn);
            Assert.Equal(measured.Sha256, request.Sha256);
            var body = JsonNode.Parse(request.Body)!;
            Assert.Equal(2, body["messages"]!.AsArray().Count);
            var packet = JsonNode.Parse(body["messages"]![1]!["content"]![0]!["text"]!.GetValue<string>())!;
            var evidence = packet["sourceEvidence"]!;
            Assert.Equal("program-executed-read-only-tools", evidence["origin"]!.GetValue<string>());
            Assert.True(evidence["complete"]!.GetValue<bool>());
            Assert.True(evidence["toolCalls"]!.GetValue<int>() >= minimumReads);
            Assert.Equal(targets, evidence["coveredTargetAliases"]!.AsArray().Select(a => a!.GetValue<string>()));
            var observed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var result in evidence["results"]!.AsArray())
            {
                Assert.Equal("OK", result!["status"]!.GetValue<string>());
                Assert.Equal(source.SourceSha256, result["provenance"]!["sourceSha256"]!.GetValue<string>());
                foreach (var line in result["result"]!["lines"]!.AsArray()) observed.Add(line!["alias"]!.GetValue<string>());
            }
            Assert.All(targets, alias => Assert.Contains(alias, observed));
            return new(JsonSerializer.Serialize(new { protocolVersion = SourceBoundSelection.Version, status = "COMPLETE",
                decisions = targets.Select(alias => new { alias, decision = "NOT_SELECT" }) }), [], "stop");
        }
        var outcome = AgentUnit.Run(source, segment, Model, AgentSelector.BudgetFor(source, PdfMembershipInstructionV9.Instruction),
            retryDelayMs: 0, instruction: PdfMembershipInstructionV9.Instruction);
        Assert.Equal("COMPLETE", outcome.Status);
        Assert.Equal(1, calls);
        Assert.Equal(0, outcome.ToolCallsEmitted);
        Assert.Equal(0, outcome.RepairsUsed);
    }

    [Fact]
    public void Tool_evidence_candidate_requires_successful_page_layout_for_every_target_before_accepting_selection()
    {
        var pdfPath = TestRepository.Path("todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf");
        var source = AgentSource.Load(pdfPath, out _, instruction: PdfMembershipInstructionV8.Instruction,
            sourceBound: true, requirePageLayoutEvidence: true)!;
        var v7 = AgentSource.Load(pdfPath, out _, instruction: PdfMembershipInstructionV7.Instruction, sourceBound: true)!;
        Assert.Equal(v7.Cores, source.Cores);
        for (var i = 0; i < source.Cores.Count; i++) Assert.Equal(v7.Scope(i).Decide, source.Scope(i).Decide);
        var targets = source.Scope(0).Decide;
        var orderedTools = source.Flavor.CreateTools(source);
        var ordered = targets.OrderBy(orderedTools.PositionOf).ToArray();
        var calls = 0;
        string? finalRequest = null;

        ModelTurn Model(string _, int turn, PreparedRequest request)
        {
            calls++;
            if (turn == 1)
                return new(Answer(ordered), [], "stop"); // The runtime must reject an answer that skipped the required evidence call.

            var seen = new HashSet<string>(StringComparer.Ordinal);
            using (var body = JsonDocument.Parse(request.Body))
            foreach (var message in body.RootElement.GetProperty("messages").EnumerateArray())
            {
                if (!message.TryGetProperty("role", out var role) || role.GetString() != "tool" ||
                    !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String) continue;
                using var result = JsonDocument.Parse(content.GetString()!);
                var root = result.RootElement;
                if (!root.TryGetProperty("tool", out var tool) || tool.GetString() != "get_page_layout" ||
                    !root.TryGetProperty("status", out var status) || status.GetString() != "OK" ||
                    !root.TryGetProperty("result", out var page) || !page.TryGetProperty("lines", out var pageLines)) continue;
                foreach (var line in pageLines.EnumerateArray()) seen.Add(line.GetProperty("alias").GetString()!);
            }

            var missing = ordered.Where(a => !seen.Contains(a)).ToArray();
            if (missing.Length == 0)
            {
                finalRequest = request.Body;
                return new(Answer(ordered), [], "stop");
            }

            var chunks = missing.Select(alias =>
            {
                var line = source.Lines.Single(l => l.Alias == alias);
                var pageLines = source.Lines.Where(l => l.Page == line.Page).ToArray();
                var position = Array.FindIndex(pageLines, l => l.Alias == alias);
                return (Page: line.Page!.Value, Offset: position / 40 * 40);
            }).Distinct().Take(8).ToArray();
            var toolCalls = chunks.Select((chunk, i) => new ToolCall("layout-" + turn + "-" + i, "get_page_layout",
                JsonSerializer.Serialize(new { page = chunk.Page, offset = chunk.Offset, limit = 40 }))).ToArray();
            return new(null, toolCalls, "tool_calls");
        }

        string Answer(IReadOnlyList<string> aliases) => JsonSerializer.Serialize(new
        {
            protocolVersion = SourceBoundSelection.Version,
            status = "COMPLETE",
            decisions = aliases.Select(alias => new { alias, decision = "NOT_SELECT" })
        });

        var outcome = AgentUnit.Run(source, 0, Model, AgentSelector.BudgetFor(source, PdfMembershipInstructionV8.Instruction),
            retryDelayMs: 0, instruction: PdfMembershipInstructionV8.Instruction);

        Assert.Equal("COMPLETE", outcome.Status);
        Assert.Equal("PROTOCOL:PAGE_LAYOUT_EVIDENCE_REQUIRED", outcome.RepairedFrom);
        Assert.True(outcome.ToolCallsSucceeded > 0);
        Assert.True(calls >= 3);
        Assert.NotNull(finalRequest);
        Assert.Contains("successful get_page_layout result showing every target alias is required", source.Flavor.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("get_page_layout", PdfMembershipInstructionV8.Instruction, StringComparison.Ordinal);
        using var final = JsonDocument.Parse(finalRequest!);
        var observed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in final.RootElement.GetProperty("messages").EnumerateArray())
        {
            if (!message.TryGetProperty("role", out var role) || role.GetString() != "tool" ||
                !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String) continue;
            using var result = JsonDocument.Parse(content.GetString()!);
            var root = result.RootElement;
            if (root.GetProperty("tool").GetString() != "get_page_layout" || root.GetProperty("status").GetString() != "OK") continue;
            foreach (var line in root.GetProperty("result").GetProperty("lines").EnumerateArray())
                observed.Add(line.GetProperty("alias").GetString()!);
        }
        Assert.All(ordered, alias => Assert.Contains(alias, observed));
    }

    [Fact]
    public void General_membership_changes_only_instruction_and_keeps_source_evidence_and_wire_contract()
    {
        var source = AgentSource.Load(TestRepository.Path(
            "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf"), out _,
            instruction: PdfMembershipInstructionV7.Instruction, sourceBound: true)!;
        var previous = JsonNode.Parse(AgentUnit.PrepareFirstRequest(source, 0, PdfMembershipInstructionV6.Instruction).Body)!;
        var general = JsonNode.Parse(AgentUnit.PrepareFirstRequest(source, 0, PdfMembershipInstructionV7.Instruction).Body)!;
        JsonObject Payload(JsonNode request) => JsonNode.Parse(request["messages"]![1]!["content"]![0]!["text"]!.GetValue<string>())!.AsObject();
        var a = Payload(previous); var b = Payload(general);
        Assert.Equal(PdfMembershipInstructionV7.Instruction, b["instruction"]!.GetValue<string>());
        Assert.NotEqual(a["instructionSha256"]!.GetValue<string>(), b["instructionSha256"]!.GetValue<string>());
        a.Remove("instruction"); a.Remove("instructionSha256");
        b.Remove("instruction"); b.Remove("instructionSha256");
        Assert.True(JsonNode.DeepEquals(a, b));
        previous["messages"]![1]!["content"]![0]!["text"] = "<instruction comparison>";
        general["messages"]![1]!["content"]![0]!["text"] = "<instruction comparison>";
        Assert.True(JsonNode.DeepEquals(previous, general));
    }

    [Fact]
    public void Separate_grouping_instruction_preserves_selection_bytes_membership_and_group_coverage()
    {
        var source = AgentSource.Load(TestRepository.Path(
            "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf"), out _,
            instruction: PdfMembershipInstructionV5.Instruction, sourceBound: true)!;
        var chosen = source.Cores.SelectMany((_, i) => source.Scope(i).Decide.Take(2)).ToHashSet();
        var snapshots = new List<Dictionary<string, string>>();
        foreach (var groupingInstruction in new[] { (string?)null, PdfBoundaryInstruction.Instruction })
        {
            var requests = new ConcurrentDictionary<string, string>();
            ModelTurn Model(string unit, int turn, PreparedRequest request)
            {
                var packet = JsonNode.Parse(JsonNode.Parse(request.Body)!["messages"]![1]!["content"]![0]!["text"]!.GetValue<string>())!;
                if (packet["stage"]!.GetValue<string>() == "GROUP")
                {
                    Assert.Equal(groupingInstruction ?? PdfMembershipInstructionV5.Instruction, packet["instruction"]!.GetValue<string>());
                    return new(JsonSerializer.Serialize(new { protocolVersion = "PDF_HEADING_GROUPS_V1",
                        runs = packet["runs"]!.AsArray().Select(r => new { run = r!["run"]!.GetValue<string>(),
                            headings = r["lines"]!.AsArray().Select(l => new[] { l!["alias"]!.GetValue<string>() }) }) }), [], "stop");
                }
                Assert.Equal(PdfMembershipInstructionV5.Instruction, packet["instruction"]!.GetValue<string>());
                Assert.True(requests.TryAdd(unit, request.Sha256));
                return new(JsonSerializer.Serialize(new { protocolVersion = SourceBoundSelection.Version, status = "COMPLETE",
                    decisions = packet["targets"]!.AsArray().Select(t => chosen.Contains(t!["alias"]!.GetValue<string>())
                        ? (object)new { alias = t["alias"]!.GetValue<string>(), decision = "SELECT", sourceText = t["text"]!.GetValue<string>(), evidenceRefs = new[] { t["alias"]!.GetValue<string>() } }
                        : new { alias = t["alias"]!.GetValue<string>(), decision = "NOT_SELECT" }) }), [], "stop");
            }
            var result = AgentSelector.Select(source, Model, AgentSelector.BudgetFor(source, PdfMembershipInstructionV5.Instruction),
                6, CancellationToken.None, 0, () => 0, PdfMembershipInstructionV5.Instruction, groupingInstruction: groupingInstruction);
            Assert.Empty(result.UnitFailures);
            Assert.True(chosen.SetEquals(result.SelectedAliases));
            Assert.NotNull(result.Groups);
            Assert.Equal(chosen.Count, result.Groups.Count);
            Assert.True(chosen.SetEquals(result.Groups.SelectMany(g => g)));
            snapshots.Add(requests.ToDictionary());
        }
        Assert.Equal(snapshots[0].OrderBy(x => x.Key), snapshots[1].OrderBy(x => x.Key));
    }

    [Fact]
    public void Boundary_candidate_is_opt_in_and_does_not_replace_custom_user_instruction()
    {
        Assert.Null(PdfSelector.ResolveGroupingInstruction(null, false));
        Assert.Null(PdfSelector.ResolveGroupingInstruction("User rule", true));
        Assert.Equal(PdfBoundaryInstruction.Instruction, PdfSelector.ResolveGroupingInstruction(null, true));
        Assert.Equal(PdfGeneralBoundaryInstruction.Instruction, PdfSelector.ResolveGroupingInstruction(null, true, general: true));
        Assert.Equal(new PdfSelector(instruction: "User rule").SelectorId,
            new PdfSelector(instruction: "User rule", separateGroupingInstruction: true).SelectorId);
        Assert.NotEqual(new PdfSelector().SelectorId, new PdfSelector(separateGroupingInstruction: true).SelectorId);
    }
}
