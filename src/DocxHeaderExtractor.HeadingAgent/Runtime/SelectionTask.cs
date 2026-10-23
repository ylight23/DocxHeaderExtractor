using System.Text.Json.Nodes;
using DocxHeaderExtractor.Agents;
using DocxHeaderExtractor.Conversation;
using DocxHeaderExtractor.HeadingAgent.Formats;
using DocxHeaderExtractor.HeadingAgent.Protocol;
using static DocxHeaderExtractor.HeadingAgent.Protocol.AgentProtocol;

namespace DocxHeaderExtractor.HeadingAgent.Runtime;

/// <summary>
/// Heading selection for one segment, as a task of <see cref="ToolConversation"/> (the D4.12 method, arm TOOLFACTS_ANALYSIS_V3). It owns what
/// is particular to the problem: the payload (targets, context, facts, the style table), the facts gate (an answer needs facts supplied in the
/// request or a successful get_line_facts call), the SELECT parser and the core-facts coverage. Where the flavor requires the core facts (DOCX),
/// a COMPLETE answer that leaves some target without them is sent back once with their aliases while the unit still has room for another facts
/// result; if they stay uncovered, or there is no room to read them, the unit ends INCOMPLETE and none of its decisions is used. C# never reads
/// the reasoning, never changes a decision and never adds a call the model did not make; which detail a decision needs is the model's.
/// </summary>
internal sealed class SelectionTask : IConversationTask<AgentUnitOutcome>
{
    readonly IAgentSource source;
    readonly IAgentFormat flavor;
    readonly IAgentTools tools;
    readonly List<string> ordered;
    readonly HashSet<string> shownAliases;
    readonly string payload;
    readonly Dictionary<string, string>? targetTexts;
    readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? readings;
    // Counters for the experiment: replies that called tools, and whether such a reply already carried decisions.
    int toolRounds;
    bool decidedBeforeTools;
    bool fenceUnwrapped;
    bool coverageAsked;

    public SelectionTask(IAgentSource source, int segment, string instruction, JsonObject? selectionReview, string? precedingAlias)
    {
        this.source = source;
        UnitId = (selectionReview is null ? "" : "REVIEW-") + AgentSource.SegmentId(segment);
        var (decide, shownIdx) = source.Scope(segment);
        var lines = source.Lines;
        flavor = source.Flavor;
        tools = flavor.CreateTools(source);
        ordered = decide.OrderBy(tools.PositionOf).ToList();
        shownAliases = shownIdx.Select(i => lines[i].Alias).ToHashSet(StringComparer.Ordinal);
        var styles = tools.DocumentStyles();
        var requestFacts = flavor.PayloadFacts(source, ordered);
        var built = Payload(source.SourceSha256, UnitId, ordered, shownIdx.Select(i => lines[i]).ToArray(), styles, instruction, flavor, requestFacts);
        if (selectionReview is not null)
        {
            var reviewPayload = JsonNode.Parse(built)!.AsObject();
            reviewPayload["selectionReview"] = selectionReview.DeepClone();
            built = reviewPayload.ToJsonString(AgentUnit.ReviewJson);
        }
        built = AgentUnit.WithContextFacts(built, source, shownIdx, ordered);
        payload = AgentUnit.WithPrecedingSelected(built, source, precedingAlias);
        if (requestFacts is not null)
            tools.NoteRequestFacts(ordered, requestFacts.SelectMany(f => flavor.FactReferences(f!)));
        // The style table shows the first lines of each style: they are in the request, so the model may cite them.
        shownAliases.UnionWith(styles["styles"]!.AsArray().SelectMany(s => s!["firstLines"]!.AsArray().Select(l => l!["alias"]!.GetValue<string>())));
        targetTexts = flavor.HeadingExtents ? ordered.ToDictionary(a => a, a => lines[tools.PositionOf(a)].Text, StringComparer.Ordinal) : null;
        var found = ordered.Select(a => (a, r: flavor.Readings(source, a))).Where(x => x.r is not null).ToDictionary(x => x.a, x => x.r!, StringComparer.Ordinal);
        readings = found.Count > 0 ? found : null;
        Tools = new ToolsOf(tools);
        Limits = new ConversationLimits(MaxPostsPerUnit, MaxToolTurns, MaxCallsPerTurn, MaxToolMessageBytesPerUnit,
            AgentUnit.TransportRetries, AgentUnit.RetriableTransport, flavor.RetryOnceCodes);
    }

    public string UnitId { get; }
    public ConversationLimits Limits { get; }
    public IConversationTools Tools { get; }
    public string ImagesNote => "The images get_scan_crop returned in this turn, in call order.";

    public PreparedRequest Build(JsonArray history, string toolChoice) => AgentProtocol.Build(payload, history, toolChoice, flavor);

    public IReadOnlyList<string> ChoicesAfterRepair(string failure) =>
        failure is "PROTOCOL:TOOL_FACTS_REQUIRED_NOT_REQUESTED" or "PROTOCOL:TOOL_CROP_REQUIRED_NOT_REQUESTED" or "PROTOCOL:TOOL_FACTS_INCOMPLETE"
            ? ["auto", "none"] : ["none"];

    public string RepairMessage(string failure, IReadOnlyList<string>? missing, string? detail) => AgentProtocol.RepairMessage(failure, missing, detail);

    public void NoteToolRound(ModelTurn reply)
    {
        toolRounds++;
        if (reply.Content is { Length: > 0 } before && before.Contains("\"decisions\"", StringComparison.Ordinal)) decidedBeforeTools = true;
    }

    public Verdict<AgentUnitOutcome> Judge(ModelTurn reply, ConversationState state)
    {
        // A final answer must come after the facts were read.
        // The scan route requires a facts-tool call: facts in the request are not enough (its model never called the tool otherwise).
        var scanGate = AgentUnit.ScanRequiresFactsTool && source is DocxHeaderExtractor.HeadingAgent.Scan.ScanAgentSource;
        var factsRead = scanGate ? tools.ToolFactsRead : tools.FactsWereRead;
        var gateCode = scanGate ? "PROTOCOL:TOOL_CROP_REQUIRED_NOT_REQUESTED" : "PROTOCOL:TOOL_FACTS_REQUIRED_NOT_REQUESTED";
        if (!factsRead) return new Verdict<AgentUnitOutcome>.Repair(gateCode);
        var observed = shownAliases.Concat(tools.ObservedAliases).Distinct(StringComparer.Ordinal).ToDictionary(a => a, tools.PositionOf, StringComparer.Ordinal);
        var content = reply.Content ?? "";
        if (TryUnwrapFence(content, out var inner)) { content = inner; fenceUnwrapped = true; }
        var parsed = ParseSelect(content, observed, ordered, flavor.AnswerVersion, targetTexts, readings);
        if (!parsed.Ok) return new Verdict<AgentUnitOutcome>.Repair("PROTOCOL:" + parsed.Failure, parsed.Missing, parsed.Detail);
        state.Budget.NoteContract(true);
        if (parsed.Status == "CLARIFICATION_REQUIRED")
            return new Verdict<AgentUnitOutcome>.Finish(Done(state, "CLARIFICATION_REQUIRED", null, analysis: parsed.Analysis));
        // Core facts coverage: a COMPLETE answer needs the core facts of every target to have been returned. One that falls short goes back
        // once, naming the targets, when another facts result still fits the unit's tool bytes (asking for what cannot be delivered would only
        // cost a post). If the targets stay uncovered, or there is no room to read them, the unit is INCOMPLETE: the decisions of an unread
        // target are not taken. Which detail evidence a decision needs is the model's; only the core facts are required.
        var uncovered = ordered.Where(a => !tools.FactsReturnedFor.Contains(a)).ToArray();
        if (uncovered.Length > 0 && parsed.Status == "COMPLETE" && flavor.RequiresCoreFacts)
        {
            if (!coverageAsked && state.MessageBytes + tools.MaxResultBytes <= MaxToolMessageBytesPerUnit)
            {
                coverageAsked = true;
                return new Verdict<AgentUnitOutcome>.Repair("PROTOCOL:TOOL_FACTS_INCOMPLETE", uncovered, StopIfUnchanged: false);
            }
            return new Verdict<AgentUnitOutcome>.Finish(Done(state, "INCOMPLETE", coverageAsked ? "COVERAGE:CORE_FACTS_MISSING" : "BUDGET:CORE_FACTS_UNCOVERED"));
        }
        return new Verdict<AgentUnitOutcome>.Finish(Done(state, parsed.Status!, null, parsed.Values, parsed.Analysis, parsed.Reasons, parsed.Extents, parsed.ReadingChoices));
    }

    public AgentUnitOutcome Stop(ConversationState state, string status, string failure) => Done(state, status, failure);

    AgentUnitOutcome Done(ConversationState state, string status, string? failure, IReadOnlyDictionary<string, string>? decisions = null,
        string? analysis = null, IReadOnlyDictionary<string, string>? reasons = null, IReadOnlyDictionary<string, HeadingExtent>? extents = null,
        IReadOnlyDictionary<string, string>? readingChoices = null) =>
        new(UnitId, status, failure, decisions ?? ordered.ToDictionary(a => a, _ => "UNRESOLVED", StringComparer.Ordinal), state.Posts,
            state.ToolCallsEmitted, state.ToolCallsSucceeded, fenceUnwrapped, analysis, reasons, state.RepairedFrom, state.RepairsUsed,
            state.RetriesUsed, state.RequestSha256s, extents, ordered.Where(a => !tools.FactsReturnedFor.Contains(a)).ToArray(),
            tools.FactsResultsCut, ordered.Where(tools.DetailEvidenceAliases.Contains).ToArray(), readingChoices, toolRounds, decidedBeforeTools);

    /// <summary>The heading tools as the conversation loop runs them.</summary>
    sealed class ToolsOf(IAgentTools tools) : IConversationTools
    {
        public IReadOnlyList<string> ReservedNames => tools.ReservedNames;
        public int MaxResultBytes => tools.MaxResultBytes;
        public int RejectionBytes(string evidenceId, string? name) => tools.RejectionBytes(evidenceId, name);

        public ToolOutput Reject(string evidenceId, string? name, string? rawArguments, string code)
        {
            var result = tools.Reject(evidenceId, name, rawArguments, code);
            return new ToolOutput(result.Status, result.Content);
        }

        public ToolOutput Execute(string evidenceId, string? name, string? rawArguments)
        {
            var result = tools.Execute(evidenceId, name, rawArguments);
            return new ToolOutput(result.Status, result.Content);
        }

        public IReadOnlyList<JsonObject> TakeImages() => tools.TakeImages();
    }
}
