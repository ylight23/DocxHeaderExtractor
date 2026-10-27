using System.Text.Json;
using DocxHeaderExtractor.HeadingAgent.Formats;
using DocxHeaderExtractor.HeadingAgent.Protocol;
using DocxHeaderExtractor.HeadingAgent.Runtime;
using DocxHeaderExtractor.HeadingAgent.Selectors;
using DocxHeaderExtractor.Agents.Budget;
using DocxHeaderExtractor.Agents.Transport;
using System.Diagnostics;

if (args.Length is < 2 or > 4) throw new ArgumentException("Usage: <pdf> <artifact-root> [canary <segment>|production-geometry]");
var productionGeometry = args.Length == 3 && args[2] == "production-geometry";
var selectedInstruction = productionGeometry ? PdfMembershipInstructionV12.Instruction : PdfAgentProtocol.Instruction;
var source = AgentSource.Load(Path.GetFullPath(args[0]), out var failure, instruction: selectedInstruction,
    sourceBound: productionGeometry, rasterGeometry: productionGeometry) ?? throw new InvalidOperationException(failure);
var output = Path.GetFullPath(args[1]);
Directory.CreateDirectory(output);
var json = new JsonSerializerOptions { WriteIndented = true };
if (args.Length > 2 && !productionGeometry)
{
    if (args[2] != "canary") throw new ArgumentException("Unknown audit mode");
    RunCanary(source, Path.GetFullPath(args[0]), output, args.Length == 4 ? int.Parse(args[3]) : 0);
    return;
}
File.WriteAllText(Path.Combine(output, "source-facts.json"), JsonSerializer.Serialize(new
{
    source.SourceSha256, source.SourceAliasUniverseSha256, source.EvidenceStoreSha256,
    lines = source.Lines.Select((l, i) => new { l.Alias, l.Ordinal, l.Page, l.Text, l.Bbox, l.Typography,
        parserBlock = source.Blocks[i], facts = source.LayoutFacts[l.Alias] }),
    documentStyles = source.Flavor.CreateTools(source).DocumentStyles(),
    scopes = source.Cores.Select((c, i) => new { segment = i, c.Start, c.End })
}, json));
foreach (var index in Enumerable.Range(0, source.Cores.Count))
{
    var request = AgentUnit.PrepareFirstRequest(source, index, selectedInstruction);
    File.WriteAllText(Path.Combine(output, $"SEG-{index:000}.request.json"), request.Body);
}
if (productionGeometry)
{
    File.WriteAllText(Path.Combine(output, "production-prompts.json"), JsonSerializer.Serialize(new
    {
        membershipInstructionVersion = 12,
        selectionInstruction = selectedInstruction,
        selectionSystemPrompt = source.Flavor.SystemPrompt,
        groupingInstruction = PdfGeneralBoundaryInstruction.Instruction,
        groupingSystemPrompt = DocxHeaderExtractor.HeadingAgent.Grouping.PdfBoundaryDecisions.PromptFor(true),
        responseProtocol = source.Flavor.AnswerVersion
    }, json));
    File.WriteAllText(Path.Combine(output, "production-geometry-plan.json"), JsonSerializer.Serialize(new
    {
        source.SourceSha256, source.PlanId, source.ResourcePolicy, source.Plan,
        pages = source.RasterGeometry.Select(p => new { page = p.Key, status = p.Value["status"]!.GetValue<string>(),
            reason = p.Value["reason"]?.GetValue<string>() }),
        requests = Enumerable.Range(0, source.Cores.Count).Select(i =>
        {
            var request = AgentUnit.PrepareFirstRequest(source, i, selectedInstruction);
            return new { segment = i, targets = source.Scope(i).Decide.Length, request.Sha256,
                resources = RequestResources.Measure(request, source.ResourcePolicy),
                refusal = source.ResourcePolicy.Refusal(RequestResources.Measure(request, source.ResourcePolicy), reserveContinuation: true) };
        }),
        providerCalls = 0, semanticAccuracyMeasured = false
    }, json));
}
Console.WriteLine(JsonSerializer.Serialize(new { source.SourceSha256, lines = source.Lines.Count, scopes = source.Cores.Count, output }));

static void RunCanary(AgentSource baseline, string pdf, string output, int segment)
{
    var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
    if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("API_KEY_NOT_CONFIGURED");
    if (File.Exists(Path.Combine(output, "canary-plan.json"))) throw new InvalidOperationException("REFUSE_OVERWRITING_CANARY");
    var candidate = AgentSource.Load(pdf, out var failure, sourceBound: true) ?? throw new InvalidOperationException(failure);
    if (!baseline.Scope(segment).Decide.SequenceEqual(candidate.Scope(segment).Decide)) throw new InvalidOperationException("CANARY_SCOPE_CHANGED");
    var json = new JsonSerializerOptions { WriteIndented = true };
    string[] sequence = ["A", "B", "B", "A"];
    File.WriteAllText(Path.Combine(output, "canary-plan.json"), JsonSerializer.Serialize(new
    {
        sourceSha256 = baseline.SourceSha256, segment, sequence,
        intervention = "B requires sourceText on every SELECT and validates it against its own target alias; no membership labels or Gold sent",
        targets = baseline.Scope(segment).Decide,
        baselineFirstRequestSha256 = AgentUnit.PrepareFirstRequest(baseline, segment, PdfAgentProtocol.Instruction).Sha256,
        candidateFirstRequestSha256 = AgentUnit.PrepareFirstRequest(candidate, segment, PdfAgentProtocol.Instruction).Sha256
    }, json));
    var runBudget = new RunResourceBudget(0.25, 100);
    using var scope = runBudget.Enter();
    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(20));
    for (var n = 0; n < sequence.Length; n++)
    {
        var profile = sequence[n];
        var source = profile == "A" ? baseline : candidate;
        var prefix = Path.Combine(output, $"{n+1:00}-{profile}");
        var budget = AgentSelector.BudgetFor(source);
        using var handler = AgentModel.CreateHandler();
        using var model = new AgentModel(handler, key, budget, cancellationToken: deadline.Token,
            rateLimitRetries: AgentModel.ProductionRateLimitRetries);
        var replies = new List<ModelReply>();
        var traced = AgentSelector.Traced(model, reply =>
        {
            replies.Add(reply);
            File.WriteAllText(prefix + ".replies.json", JsonSerializer.Serialize(replies, json));
        });
        File.WriteAllText(prefix + ".first-request.json", AgentUnit.PrepareFirstRequest(source, segment, PdfAgentProtocol.Instruction).Body);
        var clock = Stopwatch.StartNew();
        var outcome = AgentUnit.Run(source, segment, traced, budget);
        clock.Stop();
        File.WriteAllText(prefix + ".outcome.json", JsonSerializer.Serialize(new
        { profile, seconds = clock.Elapsed.TotalSeconds, outcome, resources = ResourceAudit.Observe(budget) }, json));
        Console.WriteLine(JsonSerializer.Serialize(new { profile, call = n + 1, outcome.Status, outcome.Failure,
            outcome.RepairsUsed, seconds = clock.Elapsed.TotalSeconds, selected = outcome.Decisions.Where(d => d.Value == "SELECT").Select(d => d.Key) }));
    }
    File.WriteAllText(Path.Combine(output, "resource-budget.json"), JsonSerializer.Serialize(runBudget.Observe(), json));
}
