using System.Diagnostics;
using System.Text.Json;
using DocxHeaderExtractor.Agents.Budget;
using DocxHeaderExtractor.Agents.Transport;
using DocxHeaderExtractor.Contracts.Models;
using DocxHeaderExtractor.HeadingAgent.Formats;
using DocxHeaderExtractor.HeadingAgent.Grouping;
using DocxHeaderExtractor.HeadingAgent.Protocol;
using DocxHeaderExtractor.HeadingAgent.Runtime;
using DocxHeaderExtractor.HeadingAgent.Selectors;

if (args.Length is < 4 or > 5) throw new ArgumentException("Usage: <pdf> <frozen-full-run-or-review-outcome> <output> <source-identity> [pairwise]");
var pairwise = args.Length == 5 && args[4] == "pairwise";
if (args.Length == 5 && !pairwise) throw new ArgumentException("Unknown grouping variant");
var output = Path.GetFullPath(args[2]); Directory.CreateDirectory(output);
if (File.Exists(Path.Combine(output, "trial-start.json"))) throw new InvalidOperationException("REFUSE_OVERWRITING_TRIAL");
var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("API_KEY_NOT_CONFIGURED");
var source = AgentSource.Load(Path.GetFullPath(args[0]), out var failure,
    instruction: PdfMembershipInstructionV5.Instruction, sourceBound: true) ?? throw new InvalidOperationException(failure);
using var fixture = JsonDocument.Parse(File.ReadAllText(args[1])); var frozen = fixture.RootElement;
var json = new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true };
var selected = new HashSet<string>(); var owned = new HashSet<string>(); var extents = new Dictionary<string, TextOffsetSpan>();
var sourceHash = frozen.TryGetProperty("input", out var input) ? input.GetProperty("sha256").GetString() : frozen.GetProperty("sourceSha256").GetString();
if (sourceHash != source.SourceSha256) throw new InvalidOperationException("FROZEN_SOURCE_MISMATCH");
if (frozen.TryGetProperty("pipelineAudit", out var audit))
{
    foreach (var row in audit.GetProperty("rawModelResponses").EnumerateArray())
    {
        using var parsed = JsonDocument.Parse(row.GetString()!); var m = parsed.RootElement;
        if (m.TryGetProperty("unit", out _) && m.TryGetProperty("decisions", out var decisions))
            foreach (var d in decisions.EnumerateObject()) if (!owned.Add(d.Name)) throw new InvalidOperationException("FROZEN_DUPLICATE_OWNER");
        if (m.TryGetProperty("stage", out var stage) && stage.GetString() == "pre-binding-alias-state")
            foreach (var a in m.GetProperty("aliases").EnumerateObject())
                if (a.Value.GetProperty("FinalSelection").GetString() == "SELECT") selected.Add(a.Name);
        if (m.TryGetProperty("unit", out _) && m.TryGetProperty("headingExtents", out var e) && e.ValueKind == JsonValueKind.Object)
            foreach (var a in e.EnumerateObject()) extents[a.Name] = new(a.Value.GetProperty("start").GetInt32(), a.Value.GetProperty("end").GetInt32());
    }
}
else
{
    foreach (var a in frozen.GetProperty("selected").EnumerateArray()) selected.Add(a.GetString()!);
    foreach (var o in frozen.GetProperty("outcomes").Deserialize<AgentUnitOutcome[]>(json)!)
    {
        foreach (var a in o.Decisions.Keys) if (!owned.Add(a)) throw new InvalidOperationException("FROZEN_DUPLICATE_OWNER");
        if (o.Extents is not null) foreach (var e in o.Extents) extents[e.Key] = new(e.Value.Start, e.Value.End);
    }
}
if (!selected.IsSubsetOf(source.Lines.Select(l => l.Alias))) throw new InvalidOperationException("FROZEN_UNKNOWN_ALIAS");
File.WriteAllText(Path.Combine(output, "trial-start.json"), JsonSerializer.Serialize(new
{
    sourceSha256 = source.SourceSha256, frozenSource = Path.GetFullPath(args[1]), selected, ownedTargets = owned,
    buildIdentity = JsonDocument.Parse(File.ReadAllText(args[3])).RootElement.Clone(), repetitions = 2,
    instruction = PdfBoundaryInstruction.Instruction, pairwise, purpose = "Live grouping only; frozen membership and extents, no Gold input."
}, json));
var resources = new RunResourceBudget(0.25, 100); using var scope = resources.Enter();
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
for (var repetition = 1; repetition <= 2; repetition++)
{
    var budget = AgentSelector.BudgetFor(source, PdfMembershipInstructionV5.Instruction);
    using var handler = AgentModel.CreateHandler();
    using var model = new AgentModel(handler, key, budget, cancellationToken: deadline.Token, rateLimitRetries: AgentModel.ProductionRateLimitRetries);
    var gate = new object(); var replies = new List<ModelReply>();
    var traced = AgentSelector.Traced(model, r => { lock (gate) { replies.Add(r);
        File.WriteAllText(Path.Combine(output, $"{repetition:00}.replies.json"), JsonSerializer.Serialize(replies, json)); } });
    var clock = Stopwatch.StartNew();
    var result = AgentGrouping.Group(source, selected, (unit, turn, request) =>
    {
        File.WriteAllText(Path.Combine(output, $"{repetition:00}.{unit}.{turn}.request.json"), request.Body);
        return traced(unit, turn, request);
    }, budget, 6, deadline.Token, 0, PdfBoundaryInstruction.Instruction, headingExtents: extents, pairwisePdf: pairwise);
    clock.Stop();
    File.WriteAllText(Path.Combine(output, $"{repetition:00}.outcome.json"), JsonSerializer.Serialize(new
    { repetition, seconds = clock.Elapsed.TotalSeconds, sourceSha256 = source.SourceSha256,
        result.Groups, result.Failures, result.Observation, resources = ResourceAudit.Observe(budget) }, json));
    Console.WriteLine(JsonSerializer.Serialize(new { repetition, seconds = clock.Elapsed.TotalSeconds, groups = result.Groups.Count, result.Failures }));
}
File.WriteAllText(Path.Combine(output, "trial-complete.json"), JsonSerializer.Serialize(resources.Observe(), json));
