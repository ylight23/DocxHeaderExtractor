using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Agents;
using DocxHeaderExtractor.Agents.Budget;
using DocxHeaderExtractor.Conversation;
using DocxHeaderExtractor.HeadingAgent.Formats;
using DocxHeaderExtractor.HeadingAgent.Protocol;
using DocxHeaderExtractor.HeadingAgent.Tools;

namespace DocxHeaderExtractor.HeadingAgent.Runtime;

/// <summary>V14 candidate conversation: parser layout blocks are decision targets, original aliases remain output identities.</summary>
internal sealed class PdfUnitSelectionTask : IConversationTask<AgentUnitOutcome>
{
    readonly AgentSource source; readonly int segment; readonly (int Start, int End) core; readonly IAgentTools tools; readonly IReadOnlyList<PdfSelectionUnit> units;
    readonly IReadOnlyList<string> owned; readonly HashSet<string> shown; readonly string payload;
    bool fenceUnwrapped;
    int completionTokens;
    int completionRecoveries;
    public string UnitId { get; }
    public ConversationLimits Limits { get; }
    public IConversationTools Tools { get; }
    public string ImagesNote => "";

    PdfUnitSelectionTask(AgentSource source, int segment, string instruction, (int Start, int End)? scope = null)
    {
        this.source = source; this.segment = segment; UnitId = AgentSource.SegmentId(segment); tools = source.Flavor.CreateTools(source);
        core = scope ?? source.Cores[segment]; units = PdfSelectionUnits.ForCore(source, PdfSelectionUnits.Build(source), core);
        owned = units.SelectMany(u => u.Members.Select(i => source.Lines[i].Alias)).ToArray();
        completionTokens = CompletionDemand(source, units);
        var (decide, shownIdx) = AgentUnit.Scope(source, core);
        if (!owned.SequenceEqual(decide, StringComparer.Ordinal)) throw new InvalidOperationException("PDF_UNIT_OWNERSHIP_MISMATCH");
        shown = shownIdx.Select(i => source.Lines[i].Alias).ToHashSet(StringComparer.Ordinal);
        var facts = AgentRequestFacts.Read(source, owned);
        var byAlias = facts.OfType<JsonObject>().ToDictionary(x => x["alias"]!.GetValue<string>(), StringComparer.Ordinal);
        var packet = new JsonObject
        {
            ["instruction"] = instruction, ["instructionSha256"] = AgentProtocol.Sha(instruction),
            ["documentSha256"] = source.SourceSha256, ["stage"] = "UNIT_SEGMENT", ["unitId"] = UnitId,
            ["unitProtocol"] = PdfSelectionUnits.Version,
            ["targetUnits"] = new JsonArray(units.Select(u => (JsonNode)new JsonObject
            {
                ["unitId"] = u.UnitId,
                ["geometryKind"] = u.Kind,
                ["geometry"] = u.Geometry.DeepClone(),
                ["members"] = new JsonArray(u.Members.Select(i => (JsonNode)new JsonObject
                { ["alias"] = source.Lines[i].Alias, ["page"] = source.Lines[i].Page, ["text"] = source.Lines[i].Text,
                    ["facts"] = byAlias[source.Lines[i].Alias].DeepClone() }).ToArray())
            }).ToArray()),
            ["context"] = new JsonArray(shownIdx.Where(i => i < core.Start || i >= core.End).Select(i => (JsonNode)new JsonObject
            { ["alias"] = source.Lines[i].Alias, ["page"] = source.Lines[i].Page, ["text"] = source.Lines[i].Text }).ToArray()),
            ["documentStyles"] = tools.DocumentStyles()
        };
        if (source.VisualCrops.Count > 0) packet["pageImages"] = PageImages(source, core);
        payload = PdfRasterEvidence.Attach(packet.ToJsonString(AgentUnit.ReviewJson), source);
        tools.NoteRequestFacts(owned, []);
        Tools = new ToolHost(tools);
        Limits = new ConversationLimits(AgentProtocol.MaxPostsPerUnit, AgentProtocol.MaxToolTurns, AgentProtocol.MaxCallsPerTurn,
            AgentProtocol.MaxToolMessageBytesPerUnit, AgentUnit.TransportRetries, AgentUnit.RetriableTransport, []);
    }

    /// <summary>The page band each attached image shows, so image positions map back to full-page PDF coordinates.</summary>
    internal static JsonObject PageImages(AgentSource source, (int Start, int End) core) => new()
    {
        ["coordinates"] = "PDF points, bottom-left origin, as in layout.bbox. Each image keeps the full page width and shows the band from topPt down to bottomPt; a page not listed as cropped is shown whole.",
        ["pages"] = new JsonArray(source.Lines.Skip(core.Start).Take(core.End - core.Start).Select(l => l.Page).Where(p => p is not null)
            .Select(p => p!.Value).Distinct().Order().Select(p => (JsonNode)(source.VisualCrops.TryGetValue(p, out var c) && c.Cropped
                ? new JsonObject { ["page"] = p, ["cropped"] = true, ["topPt"] = c.TopPt, ["bottomPt"] = c.BottomPt, ["pageHeightPt"] = Math.Round(c.PageHeightPt, 1) }
                : new JsonObject { ["page"] = p, ["cropped"] = false })).ToArray()),
    };

    public static AgentUnitOutcome Run(AgentSource source, int segment, AgentUnit.Model model, AgentBudget budget, int retryDelayMs, string instruction) =>
        ToolConversation.Run(new PdfUnitSelectionTask(source, segment, instruction),
            (unitId, turn, request) => model(unitId, turn, request), budget, retryDelayMs);

    /// <summary>The exact first V14 wire request, used for budget admission and cache identity.</summary>
    internal static PreparedRequest PrepareFirstRequest(AgentSource source, int segment, string instruction) =>
        new PdfUnitSelectionTask(source, segment, instruction).Build([], "auto");

    internal static PreparedRequest PrepareFirstRequest(AgentSource source, int segment, string instruction, (int Start, int End) scope) =>
        new PdfUnitSelectionTask(source, segment, instruction, scope).Build([], "auto");

    public PreparedRequest Build(JsonArray history, string toolChoice) => AgentProtocol.Build(payload, history, toolChoice, source.Flavor,
        new WireOptions(MaxTokens: completionTokens), source.SelectionImages(core));

    internal static int CompletionDemand(AgentSource source, IReadOnlyList<PdfSelectionUnit> units)
    {
        var legacy = source.ResourcePolicy.SelectionOutputDemand(units.Sum(u => u.Members.Count));
        if (!source.DynamicCompletionBudget) return legacy;
        // Estimate visible output from complete unit decisions, including NOT_SELECT reasons and
        // exact source echoes. This is an allowance, not measured tokenizer usage or a reasoning cap.
        var visible = 512L + units.Sum(u => Math.Max(192L, u.Members.Sum(i =>
            64L + (System.Text.Encoding.UTF8.GetByteCount(source.Lines[i].Text) + 1L) / 2)));
        var estimate = Math.Max(16384L, source.ResourcePolicy.SelectionReasoningAllowance + visible);
        var rounded = (estimate + 1023) / 1024 * 1024;
        return (int)Math.Min(legacy, rounded);
    }

    public bool TryIncreaseCompletionBudget()
    {
        if (!source.DynamicCompletionBudget || completionRecoveries >= 2 || completionTokens >= source.ResourcePolicy.MaxCompletionTokens) return false;
        var legacy = source.ResourcePolicy.SelectionOutputDemand(owned.Count);
        completionTokens = (int)Math.Min(source.ResourcePolicy.MaxCompletionTokens, Math.Max((long)legacy, 2L * completionTokens));
        completionRecoveries++;
        return true;
    }
    public IReadOnlyList<string> ChoicesAfterRepair(string _) => ["none"];
    public string RepairMessage(string failure, IReadOnlyList<string>? _, string? __) =>
        "Your unit-selection answer was refused (" + failure + "). Return only one complete JSON object: " +
        "{\"protocolVersion\":\"" + PdfUnitSelectionProtocol.Version + "\",\"status\":\"COMPLETE\",\"decisions\":[...]} " +
        "where every decisions item has unitId, decision, and selected; SELECT selected entries have alias, sourceText, and evidenceRefs; " +
        "NOT_SELECT has selected:[] and a concise evidence-grounded reason of at most 300 characters (not hidden chain-of-thought).";
    public void NoteToolRound(ModelTurn _) { }
    public AgentUnitOutcome Stop(ConversationState state, string status, string failure) => Done(state, status, failure);

    public Verdict<AgentUnitOutcome> Judge(ModelTurn reply, ConversationState state)
    {
        var content = reply.Content ?? "";
        if (AgentProtocol.TryUnwrapFence(content, out var inner)) { content = inner; fenceUnwrapped = true; }
        try
        {
            using var doc = JsonDocument.Parse(content); var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.GetProperty("protocolVersion").GetString() != PdfUnitSelectionProtocol.Version || root.GetProperty("status").GetString() != "COMPLETE")
                return new Verdict<AgentUnitOutcome>.Repair("PROTOCOL:UNIT_REPLY_INVALID");
            var rows = root.GetProperty("decisions"); if (rows.ValueKind != JsonValueKind.Array) return new Verdict<AgentUnitOutcome>.Repair("PROTOCOL:UNIT_DECISIONS_INVALID");
            var map = new Dictionary<string, JsonElement>(); foreach (var row in rows.EnumerateArray())
            { var id = row.GetProperty("unitId").GetString(); if (id is null || !map.TryAdd(id, row)) return new Verdict<AgentUnitOutcome>.Repair("PROTOCOL:UNIT_COVERAGE_INVALID"); }
            if (!map.Keys.ToHashSet().SetEquals(units.Select(u => u.UnitId))) return new Verdict<AgentUnitOutcome>.Repair("PROTOCOL:UNIT_COVERAGE_INVALID");
            var values = owned.ToDictionary(a => a, _ => "NOT_SELECT", StringComparer.Ordinal);
            var selectedAliases = new HashSet<string>(StringComparer.Ordinal);
            var reasons = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var unit in units)
            {
                var row = map[unit.UnitId]; var decision = row.GetProperty("decision").GetString();
                var selected = row.TryGetProperty("selected", out var items) && items.ValueKind == JsonValueKind.Array ? items : default;
                if (decision == "NOT_SELECT" && selected.ValueKind == JsonValueKind.Array && selected.GetArrayLength() != 0) return new Verdict<AgentUnitOutcome>.Repair("PROTOCOL:UNIT_DECISION_INVALID");
                if (decision == "SELECT" && (selected.ValueKind != JsonValueKind.Array || selected.GetArrayLength() == 0)) return new Verdict<AgentUnitOutcome>.Repair("PROTOCOL:UNIT_DECISION_INVALID");
                if (decision is not ("SELECT" or "NOT_SELECT")) return new Verdict<AgentUnitOutcome>.Repair("PROTOCOL:UNIT_DECISION_INVALID");
                if (decision == "NOT_SELECT")
                {
                    if (!row.TryGetProperty("reason", out var reasonNode) || reasonNode.ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(reasonNode.GetString()) || reasonNode.GetString()!.Length > 300)
                        return new Verdict<AgentUnitOutcome>.Repair("PROTOCOL:UNIT_NOT_SELECT_REASON_INVALID");
                    foreach (var index in unit.Members) reasons[source.Lines[index].Alias] = reasonNode.GetString()!;
                }
                if (decision == "SELECT") foreach (var item in selected.EnumerateArray())
                {
                    var alias = item.GetProperty("alias").GetString(); var text = item.GetProperty("sourceText").GetString();
                    if (alias is null || !selectedAliases.Add(alias) || !unit.Members.Select(i => source.Lines[i].Alias).Contains(alias) ||
                        !shown.Contains(alias) || text != source.Lines[tools.PositionOf(alias)].Text ||
                        !ValidEvidenceRefs(item, shown, tools.ObservedAliases))
                        return new Verdict<AgentUnitOutcome>.Repair("PROTOCOL:UNIT_SOURCE_BINDING_INVALID");
                    values[alias] = "SELECT";
                }
            }
            state.Budget.NoteContract(true); return new Verdict<AgentUnitOutcome>.Finish(Done(state, "COMPLETE", null, values, reasons));
        }
        catch (Exception) { return new Verdict<AgentUnitOutcome>.Repair("PROTOCOL:UNIT_REPLY_INVALID"); }
    }

    AgentUnitOutcome Done(ConversationState state, string status, string? failure, IReadOnlyDictionary<string, string>? values = null,
        IReadOnlyDictionary<string, string>? reasons = null) =>
        new(UnitId, status, failure, values ?? owned.ToDictionary(a => a, _ => "UNRESOLVED", StringComparer.Ordinal), state.Posts,
            state.ToolCallsEmitted, state.ToolCallsSucceeded, fenceUnwrapped, null, reasons, state.RepairedFrom, state.RepairsUsed,
            state.RetriesUsed, state.RequestSha256s);

    static bool ValidEvidenceRefs(JsonElement item, HashSet<string> shown, IReadOnlySet<string> observed)
    {
        if (!item.TryGetProperty("evidenceRefs", out var refs) || refs.ValueKind != JsonValueKind.Array || refs.GetArrayLength() is < 1 or > 8)
            return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in refs.EnumerateArray())
        {
            var alias = reference.GetString();
            if (alias is null || !seen.Add(alias) || (!shown.Contains(alias) && !observed.Contains(alias))) return false;
        }
        return true;
    }

    sealed class ToolHost(IAgentTools tools) : IConversationTools
    {
        public IReadOnlyList<string> ReservedNames => tools.ReservedNames; public int MaxResultBytes => tools.MaxResultBytes;
        public int RejectionBytes(string id, string? name) => tools.RejectionBytes(id, name);
        public ToolOutput Reject(string id, string? name, string? args, string code) { var x = tools.Reject(id, name, args, code); return new(x.Status, x.Content); }
        public ToolOutput Execute(string id, string? name, string? args) { var x = tools.Execute(id, name, args); return new(x.Status, x.Content); }
        public IReadOnlyList<JsonObject> TakeImages() => tools.TakeImages();
    }
}
