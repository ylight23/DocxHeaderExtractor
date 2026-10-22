using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.DocumentProcessing.Materialization;
using DocxHeaderExtractor.HeadingAgent;
using DocxHeaderExtractor.HeadingAgent.Scan;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// What the program sends to a model, pinned: every prompt by its id, the tool declarations of the PDF, DOCX and scan agents, and the exact
/// result of a fixed series of tool calls on a DOCX and on a scan. A change to any of these changes what the model reads, which is a change of
/// behaviour, not a refactoring: it needs its own version and an evaluation run. The PDF agent's requests and tool results are held to the
/// qualified agent by <see cref="PdfHeadingAgentParityTests"/>.
/// <para>
/// When such a change is intended, record the golden again: set DHX_RECORD_WIRE_GOLDEN=1 and run this test, then review the diff of
/// Golden/agent-wire.json with the change it belongs to.
/// </para>
/// </summary>
public sealed class AgentWireGoldenTests
{
    private const string GoldenPath = "tests/DocxHeaderExtractor.Tests/Golden/agent-wire.json";
    private const string Docx = "todo10_8/generated-docx/01_phap_quy/001_Bo_luat_Dan_su_91-2015-QH13.docx";

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static JsonObject Prompts() => new()
    {
        ["pdf.system"] = Sha(AgentProtocol.SystemPrompt),
        ["pdf.instruction"] = Sha(AgentProtocol.Instruction),
        ["pdf.instructionD412"] = Sha(AgentProtocol.InstructionD412),
        ["pdf.instructionStructuralV1"] = Sha(AgentProtocol.InstructionStructuralV1),
        ["pdf.instructionFormattingEvidenceV2"] = Sha(AgentProtocol.InstructionFormattingEvidenceV2),
        ["docx.system"] = Sha(DocxAgentProtocol.SystemPrompt),
        ["docx.instruction"] = Sha(DocxAgentProtocol.Instruction),
        ["scan.system"] = Sha(ScanAgentProtocol.SystemPrompt),
        ["scan.instruction"] = Sha(ScanAgentProtocol.Instruction),
        ["scan.placementNote"] = Sha(ScanAgentProtocol.PlacementNote),
        ["grouping.shared"] = Sha(GroupingPrompts.Shared),
        ["grouping.docx"] = Sha(GroupingPrompts.Docx),
        ["grouping.scan"] = Sha(GroupingPrompts.Scan),
        ["verification"] = Sha(AgentGroupVerification.Prompt),
        ["siblingReview"] = Sha(AgentSiblingReview.Prompt),
        ["selectionAudit"] = Sha(SelectionAudit.Prompt),
        ["scan.transcription"] = Sha(ScanTranscription.SystemPrompt),
        ["scan.vision"] = Sha(ScanVisionPreflight.SystemPrompt),
        ["placement.source"] = Sha(EvidenceHeadingPlacement.SourcePrompt),
        ["placement.raw"] = Sha(HeadingParentResolver.RawPlacementPrompt),
        // The repair messages a refused answer receives: every code with its own wording, and the forms that name targets or a detail.
        ["repair.TOOL_CROP_REQUIRED_NOT_REQUESTED"] = Sha(AgentProtocol.RepairMessage("PROTOCOL:TOOL_CROP_REQUIRED_NOT_REQUESTED")),
        ["repair.TOOL_FACTS_REQUIRED_NOT_REQUESTED"] = Sha(AgentProtocol.RepairMessage("PROTOCOL:TOOL_FACTS_REQUIRED_NOT_REQUESTED")),
        ["repair.TOOL_CALL_AFTER_TOOL_CAP"] = Sha(AgentProtocol.RepairMessage("PROTOCOL:TOOL_CALL_AFTER_TOOL_CAP")),
        ["repair.EVIDENCE_NOT_SHOWN"] = Sha(AgentProtocol.RepairMessage("PROTOCOL:EVIDENCE_NOT_SHOWN")),
        ["repair.COVERAGE_INCOMPLETE"] = Sha(AgentProtocol.RepairMessage("PROTOCOL:COVERAGE_INCOMPLETE")),
        ["repair.ANALYSIS_INVALID"] = Sha(AgentProtocol.RepairMessage("PROTOCOL:ANALYSIS_INVALID")),
        ["repair.REPLY_OVER_CAP"] = Sha(AgentProtocol.RepairMessage("PROTOCOL:REPLY_OVER_CAP")),
        ["repair.REASON_INVALID"] = Sha(AgentProtocol.RepairMessage("PROTOCOL:REASON_INVALID")),
        ["repair.REPLY_NOT_JSON"] = Sha(AgentProtocol.RepairMessage("PROTOCOL:REPLY_NOT_JSON")),
        ["repair.other"] = Sha(AgentProtocol.RepairMessage("PROTOCOL:SOMETHING_ELSE")),
        ["repair.READING_VIEW_UNKNOWN"] = Sha(AgentProtocol.RepairMessage("PROTOCOL:READING_VIEW_UNKNOWN", null)),
        ["repair.HEADING_EXTENT"] = Sha(AgentProtocol.RepairMessage("PROTOCOL:HEADING_EXTENT_OUTSIDE", null, "the extent ends after the text")),
        ["repair.KEY_REPEATED"] = Sha(AgentProtocol.RepairMessage("PROTOCOL:KEY_REPEATED", null)),
        ["repair.TOOL_FACTS_INCOMPLETE.named"] = Sha(AgentProtocol.RepairMessage("PROTOCOL:TOOL_FACTS_INCOMPLETE",
            Enumerable.Range(1, 45).Select(i => $"L{i:0000}:S0").ToArray())),
        ["repair.COVERAGE_INCOMPLETE.named"] = Sha(AgentProtocol.RepairMessage("PROTOCOL:COVERAGE_INCOMPLETE", ["L0001:S0", "L0002:S0"])),
    };

    private static JsonObject ToolDefinitions() => new()
    {
        ["pdf"] = Sha(AgentTools.Definitions().ToJsonString()),
        ["docx"] = Sha(DocxAgentTools.Definitions().ToJsonString()),
        ["scan"] = Sha(ScanAgentTools.Definitions().ToJsonString()),
    };

    /// <summary>Runs the calls in order on one tool set (so a repeat meets the cache) and records each result's status and exact bytes.</summary>
    private static JsonArray Calls(AgentToolHost tools, (string? Name, string Args)[] calls)
    {
        var results = new JsonArray();
        for (int i = 0; i < calls.Length; i++)
        {
            var r = tools.Execute($"E1.{i + 1:00}", calls[i].Name, calls[i].Args);
            results.Add(new JsonObject { ["call"] = $"{calls[i].Name} {calls[i].Args}", ["status"] = r.Status, ["sha256"] = r.ContentSha256 });
        }
        foreach (var code in AgentToolHost.RejectionCodes)
        {
            var r = tools.Reject("E2.01", "get_line_facts", "{}", code);
            results.Add(new JsonObject { ["call"] = "reject " + code, ["status"] = r.Status, ["sha256"] = r.ContentSha256 });
        }
        results.Add(new JsonObject { ["call"] = "rejectionBytes", ["status"] = "SIZE", ["sha256"] = tools.RejectionBytes("E3.08", "search_document_text").ToString() });
        return results;
    }

    private static JsonArray DocxCalls()
    {
        var source = DocxAgentSource.Load(TestRepository.Path(Docx), null, out var failure) ?? throw new InvalidOperationException(failure);
        string A(int i) => source.Lines[Math.Min(i, source.Lines.Count - 1)].Alias;
        return Calls(new DocxAgentTools(source),
        [
            ("get_line_facts", $"{{\"first\":\"{A(0)}\",\"last\":\"{A(10)}\",\"offset\":0}}"),
            ("get_line_facts", $"{{\"first\":\"{A(0)}\",\"last\":\"{A(10)}\",\"offset\":5}}"),
            ("get_line_facts", $"{{\"first\":\"{A(0)}\",\"last\":\"{A(10)}\",\"offset\":0}}"),   // a repeat: the cache note
            ("get_line_facts", $"{{\"first\":\"{A(10)}\",\"last\":\"{A(0)}\",\"offset\":0}}"),   // range invalid
            ("get_source_context", $"{{\"alias\":\"{A(20)}\",\"before\":3,\"after\":3}}"),
            ("get_source_span", $"{{\"aliases\":[\"{A(0)}\",\"{A(1)}\",\"{A(0)}\"]}}"),
            ("search_document_text", "{\"query\":\"Điều\",\"mode\":\"literal\",\"offset\":0,\"limit\":10}"),
            ("search_document_text", "{\"query\":\"dieu 1\",\"mode\":\"normalized\",\"offset\":0,\"limit\":5}"),
            ("get_repeated_occurrences", $"{{\"alias\":\"{A(5)}\",\"maxResults\":10}}"),
            ("compare_source_locations", $"{{\"first\":\"{A(10)}\",\"second\":\"{A(50)}\",\"window\":2}}"),
            ("get_shape_sequence", $"{{\"alias\":\"{A(30)}\"}}"),
            ("get_source_context", "{\"alias\":\"NOT-AN-ALIAS\",\"before\":1,\"after\":1}"),
            ("made_up_tool", "{}"),
            (null, "{}"),
            ("get_source_span", "not json"),
        ]);
    }

    private static JsonArray ScanCalls()
    {
        var source = ProductionGroundedRegroupTests.Source();
        string A(int i) => source.Lines[Math.Min(i, source.Lines.Count - 1)].Alias;
        return Calls(new ScanAgentTools(source),
        [
            ("get_line_facts", $"{{\"first\":\"{A(0)}\",\"last\":\"{A(5)}\",\"offset\":0}}"),
            ("get_line_facts", $"{{\"first\":\"{A(0)}\",\"last\":\"{A(5)}\",\"offset\":0}}"),   // a repeat: the cache note
            ("get_source_context", $"{{\"alias\":\"{A(3)}\",\"before\":2,\"after\":2}}"),
            ("search_document_text", "{\"query\":\"original\",\"mode\":\"literal\",\"offset\":0,\"limit\":5}"),
            ("search_document_text", "{\"query\":\"ORIGINAL 1\",\"mode\":\"normalized\",\"offset\":0,\"limit\":5}"),
            ("get_text_views", $"{{\"aliases\":[\"{A(0)}\",\"{A(1)}\"]}}"),
            ("get_source_integrity", "{}"),
            ("get_scan_crop", $"{{\"aliases\":[\"{A(0)}\"]}}"),
            ("get_source_context", "{\"alias\":\"NOT-AN-ALIAS\",\"before\":1,\"after\":1}"),
            ("made_up_tool", "{}"),
        ]);
    }

    private static JsonObject Current() => new()
    {
        ["prompts"] = Prompts(),
        ["toolDefinitions"] = ToolDefinitions(),
        ["toolCalls"] = new JsonObject { ["docx"] = DocxCalls(), ["scan"] = ScanCalls() },
    };

    [Fact]
    public void What_the_agents_send_to_a_model_is_the_recorded_bytes()
    {
        var current = Current();
        var options = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        if (Environment.GetEnvironmentVariable("DHX_RECORD_WIRE_GOLDEN") == "1")
        {
            File.WriteAllText(System.IO.Path.Combine(TestRepository.Root(), GoldenPath.Replace('/', System.IO.Path.DirectorySeparatorChar)),
                current.ToJsonString(options) + "\n", new UTF8Encoding(false));
            return;
        }
        var golden = JsonNode.Parse(File.ReadAllText(TestRepository.Path(GoldenPath)))!.AsObject();
        var differences = new List<string>();
        foreach (var section in new[] { "prompts", "toolDefinitions" })
            foreach (var (id, value) in golden[section]!.AsObject())
                if (current[section]![id]?.GetValue<string>() != value!.GetValue<string>())
                    differences.Add($"{section}.{id}");
        foreach (var flavor in new[] { "docx", "scan" })
        {
            var want = golden["toolCalls"]![flavor]!.AsArray();
            var got = current["toolCalls"]![flavor]!.AsArray();
            for (int i = 0; i < Math.Max(want.Count, got.Count); i++)
                if (want.ElementAtOrDefault(i)?.ToJsonString() != got.ElementAtOrDefault(i)?.ToJsonString())
                    differences.Add($"toolCalls.{flavor}[{i}] {want.ElementAtOrDefault(i)?["call"]}");
        }
        Assert.True(current["prompts"]!.AsObject().Count == golden["prompts"]!.AsObject().Count, "a prompt was added or removed: record the golden again");
        Assert.True(differences.Count == 0, "What a model reads changed: " + string.Join("; ", differences));
    }
}
