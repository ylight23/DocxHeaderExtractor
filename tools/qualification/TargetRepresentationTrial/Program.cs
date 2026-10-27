using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Agents;
using DocxHeaderExtractor.Agents.Budget;
using DocxHeaderExtractor.Agents.Transport;
using DocxHeaderExtractor.Conversation;
using DocxHeaderExtractor.HeadingAgent.Formats;
using DocxHeaderExtractor.HeadingAgent.Protocol;
using DocxHeaderExtractor.HeadingAgent.Runtime;
using DocxHeaderExtractor.HeadingAgent.Selectors;
using DocxHeaderExtractor.HeadingAgent.Grouping;
using DocxHeaderExtractor.Contracts.Models;

if (args.Length == 2 && args[1] == "pdf-v14-optimization-audit")
{
    PdfRequestOptimizationTrial.Audit(Path.GetFullPath(args[0]));
    return;
}
if (args.Length == 5 && args[2] == "pdf-v14-optimized-luna-replay")
{
    var jobs = int.Parse(args[3]);
    if (jobs is not (6 or 8)) throw new ArgumentException("QUALIFICATION_JOBS_MUST_BE_6_OR_8");
    Environment.SetEnvironmentVariable("DHX_OPENROUTER_MAX_CONCURRENCY", jobs.ToString());
    await NativePdf072Luna.Run(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]), Path.GetFullPath(args[4]), adaptiveVisual: true,
        membershipVersion: 14, placementBatchSize: 32, optimizedRequests: true, parallelism: jobs);
    return;
}
if (args.Length == 3 && args[2] == "pdf-v14-optimization-packets")
{
    PdfRequestOptimizationTrial.Run(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]));
    return;
}
if (args.Length == 4 && args[2] == "pdf-v14-optimized-openrouter")
{
    await NativePdfLiveQualification.Run(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]), int.Parse(args[3]));
    return;
}
if (args.Length == 4 && args[2] == "pdf-v14-optimized-luna")
{
    var jobs = int.Parse(args[3]);
    if (jobs is not (6 or 8)) throw new ArgumentException("QUALIFICATION_JOBS_MUST_BE_6_OR_8");
    // Separate qualification process: align the scheduler and account gate without changing app defaults.
    Environment.SetEnvironmentVariable("DHX_OPENROUTER_MAX_CONCURRENCY", jobs.ToString());
    await NativePdf072Luna.Run(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]), adaptiveVisual: true,
        membershipVersion: 14, placementBatchSize: 32, optimizedRequests: true, parallelism: jobs, visualGrayscale: true);
    return;
}
if (args.Length == 3 && args[2] == "production-generic-e2e-luna")
{
    await ProductionGenericE2ELuna.Run(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]));
    return;
}
if (args.Length == 3 && args[2] == "production-full-e2e-luna")
{
    await ProductionGenericE2ELuna.RunFull(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]));
    return;
}
if (args.Length == 3 && args[2] == "production-full-e2e-luna-resume")
{
    await ProductionGenericE2ELuna.ResumeFull(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]));
    return;
}
if (args.Length == 3 && args[2] == "production-full-e2e-luna-placement")
{
    await ProductionGenericE2ELuna.PlaceFrozen(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]));
    return;
}
if (args.Length == 3 && args[2] == "native-pdf072-luna")
{
    await NativePdf072Luna.Run(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]));
    return;
}
if (args.Length == 3 && args[2] == "integrated-adaptive-pdf072-luna")
{
    await NativePdf072Luna.Run(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]), adaptiveVisual: true);
    return;
}
if (args.Length == 5 && args[2] == "pdf-v14-optimized-shape-replay")
{
    // The optimized run again with ShapeReview; every request it already answered is replayed byte for byte.
    var jobs = int.Parse(args[3]);
    Environment.SetEnvironmentVariable("DHX_OPENROUTER_MAX_CONCURRENCY", jobs.ToString());
    await NativePdf072Luna.Run(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]), Path.GetFullPath(args[4]), adaptiveVisual: true,
        membershipVersion: 14, placementBatchSize: 32, optimizedRequests: true, parallelism: jobs, shapeReview: true);
    return;
}
if (args.Length == 5 && args[2] == "production-bridge-replay")
{
    await NativePdf072Luna.RunProduction(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]), crop: args[3] == "crop", replayDir: Path.GetFullPath(args[4]));
    return;
}
if (args.Length == 4 && args[2] == "production-bridge")
{
    await NativePdf072Luna.RunProduction(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]), crop: args[3] == "crop", bilevel: args[3] == "bilevel");
    return;
}
if (args.Length == 4 && args[2] == "production-live")
{
    await ProductionLive.Run(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]), crop: args[3] == "crop");
    return;
}
if (args.Length == 3 && args[2] == "visual-crop-audit")
{
    VisualCropAudit.Run(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]));
    return;
}
if (args.Length == 3 && args[2] == "production-v14-bridge")
{
    // The 2026-10-26 production PDF defaults: V14 units, V12 instruction, page images, default placement batch.
    await NativePdf072Luna.Run(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]), adaptiveVisual: true, membershipVersion: 14);
    return;
}
if (args.Length == 3 && args[2] == "production-v12-bridge")
{
    // The PDF defaults before 2026-10-26: V12 segment selection, no page images.
    await NativePdf072Luna.Run(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]));
    return;
}
if (args.Length == 3 && args[2] == "integrated-adaptive-pdf072-v14-luna")
{
    await NativePdf072Luna.Run(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]), adaptiveVisual: true,
        membershipVersion: 14, placementBatchSize: 32);
    return;
}
if (args.Length == 4 && args[2] == "integrated-adaptive-pdf-v14-luna-replay")
{
    await NativePdf072Luna.Run(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]), Path.GetFullPath(args[3]),
        adaptiveVisual: true, membershipVersion: 14, placementBatchSize: 32);
    return;
}
if (args.Length == 3 && args[2] == "clean-multimodal-pdf072-luna")
{
    await CleanMultimodalPdf072Luna.Run(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]));
    return;
}
if (args.Length == 4 && args[2] == "native-pdf072-luna-recovery")
{
    await NativePdf072Luna.Run(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]), Path.GetFullPath(args[3]));
    return;
}
if (args.Length == 4 && args[2] == "production-generic-e2e-luna-resume")
{
    ProductionGenericE2ELuna.Resume(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]), Path.GetFullPath(args[3]));
    return;
}
if (args.Length is < 2 or > 3) throw new ArgumentException("Usage: <pdf> <new-output-directory> [live|geometry|geometry-live|geometry-e2e|geometry-e2e-luna|production-generic-e2e-luna]");
var geometryTrial = args.Length == 3 && args[2].StartsWith("geometry", StringComparison.Ordinal);
var endToEnd = args.Length == 3 && args[2] is "geometry-e2e" or "geometry-e2e-luna";
var lunaBridge = args.Length == 3 && args[2] == "geometry-e2e-luna";
var output = Path.GetFullPath(args[1]);
if (Directory.Exists(output)) throw new InvalidOperationException("REFUSE_OVERWRITING_TRIAL");
Directory.CreateDirectory(output);
var json = new JsonSerializerOptions { WriteIndented = true };
var instruction = PdfMembershipInstructionV12.Instruction;
var source = AgentSource.Load(Path.GetFullPath(args[0]), out var failure, instruction: instruction, sourceBound: true)
    ?? throw new InvalidOperationException(failure);
var v13 = AgentSource.Load(Path.GetFullPath(args[0]), out failure, instruction: PdfMembershipInstructionV13.Instruction,
    sourceBound: true, includeLayoutBlockEvidence: true)!;
var v14 = AgentSource.Load(Path.GetFullPath(args[0]), out failure, instruction: instruction, sourceBound: true, unitSelection: true)!;
void Save(string name, object data) => File.WriteAllText(Path.Combine(output, name), JsonSerializer.Serialize(data, json));
JsonObject? geometry = null;
if (geometryTrial)
{
    PdfRasterGeometry.SelfTest();
    geometry = PdfRasterGeometry.Measure(Path.GetFullPath(args[0]), source, 14, output);
    Save("page-14-geometry.json", geometry);
    if (geometry["aliasesWithoutInk"]!.AsArray().Count != 0) throw new InvalidOperationException("MAPPED_ALIAS_HAS_NO_IMAGE_INK");
}
var observations = new List<object>();
for (var segment = 0; segment < source.Cores.Count; segment++)
{
    if (source.Cores[segment] != v13.Cores[segment] || source.Cores[segment] != v14.Cores[segment])
        throw new InvalidOperationException("HISTORICAL_SCOPE_DIFF_REQUIRES_EXPLICIT_ALIGNMENT");
    var old13 = AgentUnit.PrepareFirstRequest(v13, segment, PdfMembershipInstructionV13.Instruction);
    var old14 = PdfUnitSelectionTask.PrepareFirstRequest(v14, segment, instruction);
    File.WriteAllText(Path.Combine(output, $"historical-v13-SEG-{segment:000}.request.json"), old13.Body);
    File.WriteAllText(Path.Combine(output, $"historical-v14-SEG-{segment:000}.request.json"), old14.Body);
    var original = new SelectionTask(source, segment, instruction, null, null);
    var a = new RepresentationTask(original, source, segment, false, geometryTrial: geometryTrial).Build([], "auto");
    var b = new RepresentationTask(original, source, segment, !geometryTrial,
        geometry: geometryTrial && source.Lines[source.Cores[segment].Start].Page == 14 ? geometry : null, geometryTrial: geometryTrial).Build([], "auto");
    var wa = JsonNode.Parse(a.Body)!; var wb = JsonNode.Parse(b.Body)!;
    var pa = Packet(wa); var pb = Packet(wb);
    var flat = pa["targets"]!.DeepClone();
    var expanded = geometryTrial ? pb["targets"]!.DeepClone() : new JsonArray(pb["targets"]!.AsArray().SelectMany(u => u!["members"]!.AsArray()).Select(m => m!.DeepClone()).ToArray());
    if (!JsonNode.DeepEquals(flat, expanded)) throw new InvalidOperationException("TARGET_CONTENT_OR_ORDER_CHANGED");
    pa.Remove("targets"); pb.Remove("targets");
    if (geometryTrial) pb.Remove("pageGeometryEvidence");
    if (!JsonNode.DeepEquals(pa, pb)) throw new InvalidOperationException("NON_TARGET_EVIDENCE_CHANGED");
    wa["messages"]![1]!["content"]![0]!["text"] = "<verified target representation difference>";
    wb["messages"]![1]!["content"]![0]!["text"] = "<verified target representation difference>";
    if (!JsonNode.DeepEquals(wa, wb)) throw new InvalidOperationException("WIRE_SETTINGS_OR_SYSTEM_CHANGED");
    foreach (var (arm, request) in new[] { ("alias", a), (geometryTrial ? "geometry" : "unit", b) })
    {
        var resources = RequestResources.Measure(request, source.ResourcePolicy);
        if (source.ResourcePolicy.Refusal(resources, reserveContinuation: true) is { } refused)
            throw new InvalidOperationException(arm + ":" + refused);
        File.WriteAllText(Path.Combine(output, $"{arm}-SEG-{segment:000}.request.json"), request.Body);
        observations.Add(new { segment, page = source.Lines[source.Cores[segment].Start].Page, arm,
            targets = source.Scope(segment).Decide.Length, request.Sha256, resources, admission = "PASS",
            historicalV13Sha256 = old13.Sha256, historicalV14Sha256 = old14.Sha256 });
    }
}
Save("packet-parity.json", new { source.SourceSha256, instructionSha256 = AgentProtocol.Sha(instruction),
    intervention = geometryTrial ? "Only pageGeometryEvidence added to alias targets on page 14. Same prompt, source text, facts, context, styles, tools, parser and budgets. Other pages are unchanged packet checks, not image-preprocessed pages."
        : "Only targets representation: flat aliases versus parser blocks containing identical aliases. Same source-bound alias response, parser, prompt, facts, context facts, styles, tools, generation settings and budgets.",
    limitation = "This isolates representation under a common alias-output contract; it does not separately measure V14's unit-output contract.", observations });
Console.WriteLine("PACKET_PARITY_PASS: " + observations.Count);
if (args.Length != 3 || args[2] == "geometry") return;
if (args[2] is not ("live" or "geometry-live" or "geometry-e2e" or "geometry-e2e-luna")) throw new ArgumentException("Unknown mode");
var key = lunaBridge ? "LOCAL_LUNA_IN_SESSION_ONLY" : Environment.GetEnvironmentVariable("OPENROUTER_API_KEY") ?? throw new InvalidOperationException("API_KEY_MISSING");
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(12));
// A single pair on page 14 is a diagnostic, not a stability qualification. Gold never enters either request.
var targetSegment = Enumerable.Range(0, source.Cores.Count).Single(i => source.Lines[source.Cores[i].Start].Page == 14);
await Task.WhenAll(new[] { false, true }.Select(grouped => Task.Run(() =>
{
    var arm = grouped ? geometryTrial ? "geometry" : "unit" : "alias";
    var budget = AgentSelector.BudgetFor(source, instruction);
    using var handler = lunaBridge ? null : AgentModel.CreateHandler();
    using var model = lunaBridge ? null : new AgentModel(handler!, key, budget, cancellationToken: deadline.Token,
        rateLimitRetries: AgentModel.ProductionRateLimitRetries);
    using var trace = new StreamWriter(Path.Combine(output, arm + "-replies.ndjson")) { AutoFlush = true };
    var task = new RepresentationTask(new SelectionTask(source, targetSegment, instruction, null, null), source, targetSegment,
        grouped && !geometryTrial, geometry: grouped && geometryTrial ? geometry : null, geometryTrial: geometryTrial);
    var clock = System.Diagnostics.Stopwatch.StartNew();
    ModelTurn Call(string unit, int turn, PreparedRequest request)
    {
        // Pass the same optional geometry through later phases. Both arms share the description;
        // only the treatment carries the measured evidence. Selection already builds that packet.
        if (endToEnd && !unit.StartsWith("SEG-", StringComparison.Ordinal))
        {
            var wire = JsonNode.Parse(request.Body)!;
            var system = wire["messages"]![0]!["content"];
            const string note = "\nOptional pageGeometryEvidence is image measurements and mapped source-box relations, not heading labels. Use with source text and context; geometry alone does not determine membership.";
            if (system is JsonArray) system[0]!["text"] = system[0]!["text"]!.GetValue<string>() + note;
            else wire["messages"]![0]!["content"] = system!.GetValue<string>() + note;
            var content = wire["messages"]![1]!["content"];
            var payloadText = content is JsonArray ? content[0]!["text"]!.GetValue<string>() : content!.GetValue<string>();
            var payload = JsonNode.Parse(payloadText)!.AsObject();
            if (grouped && geometry is not null) payload["pageGeometryEvidence"] = geometry.DeepClone();
            if (content is JsonArray) content[0]!["text"] = payload.ToJsonString(AgentUnit.ReviewJson);
            else wire["messages"]![1]!["content"] = payload.ToJsonString(AgentUnit.ReviewJson);
            var body = wire.ToJsonString(AgentUnit.ReviewJson);
            request = new(body, Encoding.UTF8.GetByteCount(body), AgentProtocol.Sha(body));
        }
        if (source.ResourcePolicy.Refusal(RequestResources.Measure(request, source.ResourcePolicy)) is { } refusal)
            throw new InvalidOperationException("ACTUAL_WIRE_REFUSED:" + refusal);
        File.WriteAllText(Path.Combine(output, $"{arm}-{unit}-turn-{turn:00}.request.json"), request.Body);
        var reply = lunaBridge ? LunaFileTurn(output, arm, unit, turn, request, deadline.Token) : model!.Turn(unit, turn, request);
        trace.WriteLine(JsonSerializer.Serialize(new { unit, turn, request.Sha256, reply }));
        return reply;
    }
    var outcome = ToolConversation.Run(task, Call, budget, 5000, maxPosts: 6);
    Save(arm + "-outcome.json", new { arm, seconds = clock.Elapsed.TotalSeconds, outcome });
    Console.WriteLine(arm + ":" + outcome.Status);
    if (!endToEnd || outcome.Status != "COMPLETE") return;
    var chosen = outcome.Decisions.Where(d => d.Value == "SELECT").Select(d => d.Key).ToHashSet(StringComparer.Ordinal);
    var extents = outcome.Extents?.ToDictionary(e => e.Key, e => new TextOffsetSpan(e.Value.Start, e.Value.End)) ?? [];
    var readings = outcome.ReadingChoices ?? new Dictionary<string, string>();
    var groupClock = System.Diagnostics.Stopwatch.StartNew();
    var groups = AgentGrouping.Group(source, chosen, Call, budget, 1, deadline.Token, 5000,
        PdfBoundaryInstruction.Instruction, headingExtents: extents, headingReadings: readings, pairwisePdf: true);
    Save(arm + "-group.json", new { seconds = groupClock.Elapsed.TotalSeconds, result = groups });
    if (!groups.Groups.SelectMany(g => g).ToHashSet(StringComparer.Ordinal).SetEquals(chosen))
        throw new InvalidOperationException("GROUP_CHANGED_MEMBERSHIP");
    var verifyClock = System.Diagnostics.Stopwatch.StartNew();
    var verified = GroupVerifier.Verify(source, groups, extents, readings, Call, budget, 1, deadline.Token,
        5000, instruction, allowMembershipReview: false);
    Save(arm + "-verify.json", new { seconds = verifyClock.Elapsed.TotalSeconds, result = verified });
    var unresolved = verified.UnresolvedAliases.Concat(verified.Provenance.Where(p => p.Decision == "FALLBACK").Select(p => p.Alias)).ToHashSet(StringComparer.Ordinal);
    var finalGroups = verified.Groups.Where(g => g.All(a => chosen.Contains(a) && !unresolved.Contains(a))).ToArray();
    var returned = finalGroups.SelectMany(g => g).ToHashSet(StringComparer.Ordinal);
    if (!returned.IsSubsetOf(chosen)) throw new InvalidOperationException("VERIFY_ADDED_UNSELECTED_ALIAS");
    var states = outcome.Decisions.ToDictionary(d => d.Key, d => d.Value != "SELECT" ? d.Value
        : verified.Memberships.GetValueOrDefault(d.Key) == "NOT_HEADING" ? "NOT_SELECT"
        : returned.Contains(d.Key) ? "SELECT" : "UNRESOLVED", StringComparer.Ordinal);
    Save(arm + "-final.json", new { source.SourceSha256, page = 14, arm, experiment = "SELECT -> production pairwise GROUP -> qualification semantic VERIFY -> source-bound final groups; no hierarchy placement",
        seconds = clock.Elapsed.TotalSeconds, selection = outcome.Decisions, finalStates = states,
        finalGroups, selectedAliases = returned.Order().ToArray(),
        withheldAliases = chosen.Except(returned).Order().ToArray(), verified.Memberships, verified.Failures, verified.UnresolvedAliases,
        finalHeadings = finalGroups.Select(g => new { aliases = g, text = string.Join(" ", g.Select(a =>
            { var line = source.Lines.Single(l => l.Alias == a); return extents.TryGetValue(a, out var span) ? line.Text[span.Start..span.End] : line.Text; })) }),
        resources = ResourceAudit.Observe(budget) });
    Console.WriteLine(arm + ":FINAL:" + returned.Count + ":UNRESOLVED:" + states.Values.Count(v => v == "UNRESOLVED"));
}, deadline.Token)));

static ModelTurn LunaFileTurn(string output, string arm, string unit, int turn, PreparedRequest request, CancellationToken cancellationToken)
{
    var pending = Path.Combine(output, "luna-requests");
    var authored = Path.Combine(output, "luna-responses");
    Directory.CreateDirectory(pending); Directory.CreateDirectory(authored);
    var requestPath = Path.Combine(pending, request.Sha256 + ".request.json");
    File.WriteAllText(requestPath, request.Body);
    var responsePath = Path.Combine(authored, request.Sha256 + ".response.json");
    Console.WriteLine($"LUNA_REQUEST_READY arm={arm} phase={unit} turn={turn} sha256={request.Sha256} path={requestPath} response={responsePath}");
    while (!File.Exists(responsePath)) { cancellationToken.ThrowIfCancellationRequested(); Thread.Sleep(200); }
    using var doc = JsonDocument.Parse(File.ReadAllText(responsePath));
    var root = doc.RootElement;
    if (root.GetProperty("requestSha256").GetString() != request.Sha256) throw new InvalidOperationException("LUNA_REPLY_REQUEST_SHA_MISMATCH");
    var calls = root.TryGetProperty("toolCalls", out var cs)
        ? cs.EnumerateArray().Select(c => new ToolCall(c.GetProperty("id").GetString(), c.GetProperty("name").GetString(), c.GetProperty("arguments").GetString() ?? "")).ToArray()
        : Array.Empty<ToolCall>();
    return new ModelTurn(root.TryGetProperty("content", out var content) ? content.GetString() : null, calls,
        root.TryGetProperty("finishReason", out var fr) ? fr.GetString() : "stop");
}

static JsonObject Packet(JsonNode wire) => JsonNode.Parse(wire["messages"]![1]!["content"]![0]!["text"]!.GetValue<string>())!.AsObject();

sealed class RepresentationTask(SelectionTask inner, AgentSource source, int segment, bool grouped,
    JsonObject? geometry = null, bool geometryTrial = false) : IConversationTask<AgentUnitOutcome>
{
    const string CommonInstruction = "\nInput targets may be a flat array of source occurrences or an array of parser layout units containing members. Flatten members in source order to obtain the same owned aliases. Layout units are geometry only. Return the existing alias-level decisions schema for every owned alias, including UNRESOLVED when appropriate; headingExtent and source binding retain their existing meaning. targetFacts and contextFacts are measurements, not semantic labels.";
    public string UnitId => inner.UnitId;
    public ConversationLimits Limits => inner.Limits;
    public IConversationTools Tools => inner.Tools;
    public string ImagesNote => inner.ImagesNote;
    public PreparedRequest Build(JsonArray history, string toolChoice)
    {
        var wire = JsonNode.Parse(inner.Build(history, toolChoice).Body)!;
        wire["messages"]![0]!["content"]![0]!["text"] = SourceBoundSelection.SystemPrompt + CommonInstruction +
            (geometryTrial ? "\nOptional pageGeometryEvidence contains measured image ink, frames and whitespace, plus peer relations derived from mapped parser boxes. It contains no semantic labels. Use it as source evidence together with text and context; geometry alone does not determine heading membership." : "");
        var packet = JsonNode.Parse(wire["messages"]![1]!["content"]![0]!["text"]!.GetValue<string>())!.AsObject();
        PdfLayoutBlockEvidence.Attach(packet["targetFacts"]!.AsArray(), source);
        var core = source.Cores[segment];
        var (_, shown) = source.Scope(segment);
        // The source facts handler accepts contiguous ranges. Read each context side separately.
        packet["contextFacts"] = new JsonArray(new[] { shown.Where(i => i < core.Start), shown.Where(i => i >= core.End) }
            .SelectMany(side => AgentRequestFacts.Read(source, side.Select(i => source.Lines[i].Alias).ToArray()))
            .Select(f => f!.DeepClone()).ToArray());
        if (grouped)
        {
            var byAlias = packet["targets"]!.AsArray().ToDictionary(t => t!["alias"]!.GetValue<string>(), t => t!);
            packet["targets"] = new JsonArray(PdfSelectionUnits.ForCore(source, PdfSelectionUnits.Build(source), source.Cores[segment])
                .Select(u => (JsonNode)new JsonObject { ["unitId"] = u.UnitId,
                    ["members"] = new JsonArray(u.Members.Select(i => byAlias[source.Lines[i].Alias].DeepClone()).ToArray()) }).ToArray());
        }
        if (geometry is not null) packet["pageGeometryEvidence"] = geometry.DeepClone();
        wire["messages"]![1]!["content"]![0]!["text"] = packet.ToJsonString(AgentUnit.ReviewJson);
        var body = wire.ToJsonString(AgentUnit.ReviewJson);
        return new(body, Encoding.UTF8.GetByteCount(body), AgentProtocol.Sha(body));
    }
    public IReadOnlyList<string> ChoicesAfterRepair(string failure) => inner.ChoicesAfterRepair(failure);
    public string RepairMessage(string failure, IReadOnlyList<string>? missing, string? detail) => inner.RepairMessage(failure, missing, detail);
    public void NoteToolRound(ModelTurn reply) => inner.NoteToolRound(reply);
    public Verdict<AgentUnitOutcome> Judge(ModelTurn reply, ConversationState state) => inner.Judge(reply, state);
    public AgentUnitOutcome Stop(ConversationState state, string status, string failure) => inner.Stop(state, status, failure);
}
