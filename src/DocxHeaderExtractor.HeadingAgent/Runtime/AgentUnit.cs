using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Agents;
using DocxHeaderExtractor.Agents.Budget;
using DocxHeaderExtractor.Conversation;
using DocxHeaderExtractor.HeadingAgent.Formats;
using static DocxHeaderExtractor.HeadingAgent.Protocol.AgentProtocol;

namespace DocxHeaderExtractor.HeadingAgent.Runtime;

/// <summary>
/// One unit of the D4.12 method (ported from the qualified V6 RunUnit, arm TOOLFACTS_ANALYSIS_V3, with json_object on every turn, the
/// single-fence unwrap, two prompt-cache marks, 3 transport retries and repair turns with no limit of their own):
///   turns 1..3: tools declared, tool_choice auto; a reply with tool calls is answered with the tool results and the conversation continues;
///   turn 4: tool_choice none; the model answers from what it has.
/// The turns, retries, budget and tool rounds are the shared <see cref="ToolConversation"/>; what belongs to heading selection is
/// <see cref="SelectionTask"/>.
/// </summary>
internal static class AgentUnit
{
    internal static readonly JsonSerializerOptions ReviewJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public const int TransportRetries = 3;
    public static readonly string[] RetriableTransport = ["TRANSPORT:STREAM_ERROR", "TRANSPORT:STREAM_INCOMPLETE", "TRANSPORT:RATE_LIMITED_NO_RETRY", "TRANSPORT:SERVER_ERROR_NO_RETRY", "TRANSPORT:TRANSPORT_ERROR"];

    public delegate ModelTurn Model(string unitId, int turn, PreparedRequest request);

    /// <summary>Scan only: a reply without a facts-tool call is refused and repaired (the gate reads tool calls, not facts in the request).</summary>
    internal const bool ScanRequiresFactsTool = false;
    /// <summary>Scan only: the context lines' facts in the request (contextFacts). Off: the request then stays the size the PDF and DOCX routes send.</summary>
    internal const bool ScanContextFacts = false;

    /// <summary>The SHA-256 of the first request <see cref="Run"/> sends for a segment (no history, tool_choice auto): it carries the model,
    /// the system prompt, the tools, the instruction and the payload with its facts, so it names everything the unit's answer depends on.</summary>
    public static string FirstRequestSha256(IAgentSource source, int segment, string instruction = Instruction, string? precedingAlias = null)
    {
        var (decide, shownIdx) = source.Scope(segment);
        var flavor = source.Flavor;
        var tools = flavor.CreateTools(source);
        var ordered = decide.OrderBy(tools.PositionOf).ToList();
        var payload = Payload(source.SourceSha256, AgentSource.SegmentId(segment), ordered, shownIdx.Select(i => source.Lines[i]).ToArray(),
            tools.DocumentStyles(), instruction, flavor, flavor.PayloadFacts(source, ordered));
        payload = WithContextFacts(payload, source, shownIdx, ordered);
        payload = WithPrecedingSelected(payload, source, precedingAlias);
        return Build(payload, new JsonArray(), "auto", flavor).Sha256;
    }

    /// <summary>Scan only: the context lines' facts, as contextFacts, so a target is compared with the lines next to it from the request itself.</summary>
    internal static string WithContextFacts(string payload, IAgentSource source, IReadOnlyList<int> shown, IReadOnlyList<string> decide)
    {
        if (!ScanContextFacts || source is not DocxHeaderExtractor.HeadingAgent.Scan.ScanAgentSource scan) return payload;
        var targets = decide.ToHashSet(StringComparer.Ordinal);
        var items = shown.Select(i => scan.Lines[i].Alias).Where(a => !targets.Contains(a))
            .Select(a => (JsonNode)new JsonObject { ["alias"] = a, ["facts"] = scan.Facts[a].DeepClone() }).ToArray();
        var node = JsonNode.Parse(payload)!.AsObject();
        node["contextFacts"] = new JsonArray(items);
        return node.ToJsonString(ReviewJson);
    }

    /// <summary>The request of a unit that follows the earlier answers: precedingSelected names the nearest line before the unit's targets that those
    /// answers selected, with its page and text. Without an alias the payload is returned as it is, so no other request changes.</summary>
    internal static string WithPrecedingSelected(string payload, IAgentSource source, string? alias)
    {
        if (alias is null) return payload;
        var line = source.Lines.Single(l => l.Alias == alias);
        var node = JsonNode.Parse(payload)!.AsObject();
        node["precedingSelected"] = new JsonObject { ["alias"] = alias, ["page"] = JsonValue.Create(line.Page), ["text"] = line.Text };
        return node.ToJsonString(ReviewJson);
    }

    public static AgentUnitOutcome Run(IAgentSource source, int segment, Model model, AgentBudget budget, int retryDelayMs = 5000, string instruction = Instruction,
        JsonObject? selectionReview = null, int maxPosts = int.MaxValue, string? precedingAlias = null) =>
        ToolConversation.Run(new SelectionTask(source, segment, instruction, selectionReview, precedingAlias),
            (unitId, turn, request) => model(unitId, turn, request), budget, retryDelayMs, maxPosts);
}
