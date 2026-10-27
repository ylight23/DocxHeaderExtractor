using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Agents.Budget;
using DocxHeaderExtractor.Agents.Transport;
using DocxHeaderExtractor.HeadingAgent.Formats;
using DocxHeaderExtractor.HeadingAgent.Protocol;
using DocxHeaderExtractor.HeadingAgent.Runtime;
using DocxHeaderExtractor.HeadingAgent.Selectors;

if (args.Length is < 4 or > 6) throw new ArgumentException("Usage: <pdf> <frozen-run-or-scope-outcome> <output> <identity> [instruction-version=3] [initial]");
var initial = args.Length == 6 && args[5] == "initial";
if (args.Length == 6 && !initial) throw new ArgumentException("Unknown trial variant");
var output = Path.GetFullPath(args[2]); Directory.CreateDirectory(output);
if (File.Exists(Path.Combine(output, "trial-start.json"))) throw new InvalidOperationException("REFUSE_OVERWRITING_TRIAL");
var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("API_KEY_NOT_CONFIGURED");
var instruction = PdfSelector.ResolveInstruction(null, true, args.Length >= 5 ? int.Parse(args[4]) : 3);
var instructionVersion = args.Length >= 5 ? int.Parse(args[4]) : 3;
var sourcePreparation = Stopwatch.StartNew();
var source = AgentSource.Load(Path.GetFullPath(args[0]), out var failure, instruction: instruction, sourceBound: true,
    requirePageLayoutEvidence: instructionVersion == 8, preloadPageLayoutEvidence: instructionVersion is 9 or 10)
    ?? throw new InvalidOperationException(failure);
sourcePreparation.Stop();
using var fixture = JsonDocument.Parse(File.ReadAllText(args[1])); var frozen = fixture.RootElement;
var json = new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true };
static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
var before = Enumerable.Range(0, source.Cores.Count).Select(i => new AgentUnitOutcome(AgentSource.SegmentId(i), "NOT_RUN", null,
    new Dictionary<string, string>(), 0, 0, 0, false, null, null, null, 0, 0, [])).ToArray();
var sourceHash = frozen.TryGetProperty("input", out var input) ? input.GetProperty("sha256").GetString() : frozen.GetProperty("sourceSha256").GetString();
if (sourceHash != source.SourceSha256) throw new InvalidOperationException("FROZEN_SOURCE_MISMATCH");
if (frozen.TryGetProperty("pipelineAudit", out var audit))
{
    foreach (var row in audit.GetProperty("rawModelResponses").EnumerateArray())
    {
        using var parsed = JsonDocument.Parse(row.GetString()!); var m = parsed.RootElement;
        if (!m.TryGetProperty("unit", out var u) || !m.TryGetProperty("decisions", out var d)) continue;
        var index = int.Parse(u.GetString()!.AsSpan(4));
        var decisions = d.Deserialize<Dictionary<string, string>>()!;
        if (!decisions.Keys.ToHashSet().SetEquals(source.Scope(index).Decide)) throw new InvalidOperationException("FROZEN_SCOPE_CHANGED");
        var extents = m.TryGetProperty("headingExtents", out var e) && e.ValueKind == JsonValueKind.Object
            ? e.Deserialize<Dictionary<string, AgentProtocol.HeadingExtent>>(json) : null;
        before[index] = before[index] with { Status = "COMPLETE", Decisions = decisions, Extents = extents };
    }
}
else
{
    var o = frozen.GetProperty("outcome").Deserialize<AgentUnitOutcome>(json)!;
    var index = int.Parse(o.UnitId.AsSpan(4));
    if (!o.Decisions.Keys.ToHashSet().SetEquals(source.Scope(index).Decide)) throw new InvalidOperationException("FROZEN_SCOPE_CHANGED");
    before[index] = o;
}
var plan = initial ? Enumerable.Range(0, before.Length).Where(i => before[i].Status == "COMPLETE").ToArray()
    : BoundarySelectionReview.Plan(source, before);
if (initial)
foreach (var i in plan)
{
    var priorInstruction = instructionVersion switch
    {
        10 => PdfMembershipInstructionV9.Instruction,
        9 => PdfMembershipInstructionV8.Instruction,
        8 => PdfMembershipInstructionV7.Instruction,
        7 => PdfMembershipInstructionV6.Instruction,
        _ => PdfMembershipInstructionV5.Instruction
    };
    var a = JsonNode.Parse(AgentUnit.PrepareFirstRequest(source, i, priorInstruction).Body)!;
    var b = JsonNode.Parse(AgentUnit.PrepareFirstRequest(source, i, instruction).Body)!;
    var pa = JsonNode.Parse(a["messages"]![1]!["content"]![0]!["text"]!.GetValue<string>())!.AsObject();
    var pb = JsonNode.Parse(b["messages"]![1]!["content"]![0]!["text"]!.GetValue<string>())!.AsObject();
    pa.Remove("instruction"); pa.Remove("instructionSha256"); pb.Remove("instruction"); pb.Remove("instructionSha256");
    if (!JsonNode.DeepEquals(pa, pb)) throw new InvalidOperationException("INITIAL_TRIAL_EVIDENCE_CHANGED");
    a["messages"]![1]!["content"]![0]!["text"] = "<instruction ablation>";
    b["messages"]![1]!["content"]![0]!["text"] = "<instruction ablation>";
    if (instructionVersion == 8)
    {
        var system = b["messages"]![0]!["content"]![0]!["text"]!.GetValue<string>();
        if (!system.Contains("successful get_page_layout result showing every target alias is required", StringComparison.Ordinal))
            throw new InvalidOperationException("PAGE_LAYOUT_EVIDENCE_PROMPT_MISSING");
        a["messages"]![0]!["content"]![0]!["text"] = SourceBoundSelection.SystemPrompt;
        b["messages"]![0]!["content"]![0]!["text"] = a["messages"]![0]!["content"]![0]!["text"]!.DeepClone();
    }
    if (instructionVersion is 9 or 10)
    {
        var evidence = pb["sourceEvidence"]!;
        if (!evidence["complete"]!.GetValue<bool>() || evidence["coveredTargetAliases"]!.AsArray().Count != source.Scope(i).Decide.Length)
            throw new InvalidOperationException("PREFLIGHT_EVIDENCE_INCOMPLETE");
        var system = b["messages"]![0]!["content"]![0]!["text"]!.GetValue<string>();
        if (!system.Contains("completed pagination before this classification request", StringComparison.Ordinal))
            throw new InvalidOperationException("PREFLIGHT_SYSTEM_PROMPT_MISSING");
    }
    if (!JsonNode.DeepEquals(a, b)) throw new InvalidOperationException("INITIAL_TRIAL_WIRE_SETTINGS_CHANGED");
}
File.WriteAllText(Path.Combine(output, "trial-start.json"), JsonSerializer.Serialize(new
{
    sourceSha256 = source.SourceSha256, frozenSource = Path.GetFullPath(args[1]), instructionVersion,
    instructionSha256 = AgentProtocol.Sha(instruction),
    selectorAssembly = typeof(PdfSelector).Assembly.Location,
    selectorAssemblySha256 = Sha256File(typeof(PdfSelector).Assembly.Location),
    trialAssembly = typeof(Program).Assembly.Location,
    trialAssemblySha256 = Sha256File(typeof(Program).Assembly.Location),
    maxPostsPerUnit = instructionVersion >= 8 ? AgentProtocol.MaxPostsPerUnit : 2,
    requiredEvidence = instructionVersion >= 8 ? "Every target alias must occur in a successful get_page_layout result." : null,
    evidenceBeforeFirstClassification = instructionVersion is 9 or 10,
    boundedNameReview = instructionVersion == 9,
    sourcePreparationSeconds = sourcePreparation.Elapsed.TotalSeconds,
    buildIdentity = JsonDocument.Parse(File.ReadAllText(args[3])).RootElement.Clone(),
    plannedSegments = plan, initial, repetitions = 2, completeOriginalScopes = before.Count(o => o.Status == "COMPLETE"),
    ownedTargets = before.Where(o => o.Status == "COMPLETE").SelectMany(o => o.Decisions.Keys),
    beforeSelected = before.SelectMany(o => o.Decisions.Where(d => d.Value == "SELECT").Select(d => d.Key))
}, json));
var runBudget = new RunResourceBudget(0.25, 80); using var scope = runBudget.Enter();
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
for (var repetition = 1; repetition <= 2; repetition++)
{
    var outcomes = before.ToArray(); var budgetPreparation = Stopwatch.StartNew();
    var budget = AgentSelector.BudgetFor(source, instruction, boundaryReview: true);
    budgetPreparation.Stop();
    using var handler = AgentModel.CreateHandler();
    using var model = new AgentModel(handler, key, budget, cancellationToken: deadline.Token, rateLimitRetries: AgentModel.ProductionRateLimitRetries);
    var gate = new object(); var replies = new List<ModelReply>();
    var traced = AgentSelector.Traced(model, r =>
    {
        lock (gate)
        {
            replies.Add(r);
            File.WriteAllText(Path.Combine(output, $"{repetition:00}.replies.json"), JsonSerializer.Serialize(replies, json));
        }
    });
    var clock = Stopwatch.StartNew();
    AgentUnit.Model Execute = (unit, turn, request) =>
    {
        File.WriteAllText(Path.Combine(output, $"{repetition:00}.{unit}.{turn}.request.json"), request.Body);
        return traced(unit, turn, request);
    };
    string[] events, failures;
    string[] nameReviewEvents = [], nameReviewFailures = [];
    if (!initial) (events, failures) = BoundarySelectionReview.Apply(source, outcomes, Execute, budget, 6, instruction, deadline.Token);
    else
    {
        var revised = new AgentUnitOutcome[plan.Length];
        Parallel.For(0, plan.Length, new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = deadline.Token },
            n => revised[n] = AgentUnit.Run(source, plan[n], Execute, budget, 0, instruction,
            maxPosts: instructionVersion >= 8 ? AgentProtocol.MaxPostsPerUnit : 2));
        if (instructionVersion == 9)
        {
            var provisional = before.ToArray();
            for (var n = 0; n < plan.Length; n++) provisional[plan[n]] = revised[n];
            (nameReviewEvents, nameReviewFailures) = BoundarySelectionReview.Apply(source, provisional, Execute, budget, 6,
                instruction, deadline.Token);
            for (var n = 0; n < plan.Length; n++) revised[n] = provisional[plan[n]];
        }
        var eventRows = new List<string>(); var errors = new List<string>();
        for (var n = 0; n < plan.Length; n++)
        {
            var i = plan[n]; var after = revised[n];
            var applied = after.Status == "COMPLETE" && before[i].Decisions.Keys.ToHashSet().SetEquals(after.Decisions.Keys);
            if (applied) outcomes[i] = after;
            else errors.Add(after.UnitId + ":" + (after.Failure ?? after.Status));
            eventRows.Add(JsonSerializer.Serialize(new { stage = "initial-selection-trial", unit = before[i].UnitId, applied,
                after.Status, after.Failure, after.Posts, after.RepairsUsed, before = before[i].Decisions, after = after.Decisions, after.RequestSha256s }));
        }
        events = eventRows.ToArray(); failures = errors.Concat(nameReviewFailures).ToArray();
    }
    clock.Stop();
    File.WriteAllText(Path.Combine(output, $"{repetition:00}.outcome.json"), JsonSerializer.Serialize(new
    {
        repetition, sourceSha256 = source.SourceSha256, seconds = clock.Elapsed.TotalSeconds,
        requestBudgetPreparationSeconds = budgetPreparation.Elapsed.TotalSeconds,
        outcomes = outcomes.Where(o => o.Status == "COMPLETE"), events, failures, nameReviewEvents,
        selected = outcomes.Where(o => o.Status == "COMPLETE").SelectMany(o => o.Decisions.Where(d => d.Value == "SELECT").Select(d => d.Key)),
        resources = ResourceAudit.Observe(budget)
    }, json));
    Console.WriteLine(JsonSerializer.Serialize(new { repetition, seconds = clock.Elapsed.TotalSeconds, failures,
        selected = outcomes.Where(o => o.Status == "COMPLETE").SelectMany(o => o.Decisions.Where(d => d.Value == "SELECT").Select(d => d.Key)) }));
}
File.WriteAllText(Path.Combine(output, "trial-complete.json"), JsonSerializer.Serialize(runBudget.Observe(), json));
