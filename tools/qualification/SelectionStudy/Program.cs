using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Agents.Budget;
using DocxHeaderExtractor.Agents.Transport;
using DocxHeaderExtractor.HeadingAgent.Formats;
using DocxHeaderExtractor.HeadingAgent.Protocol;
using DocxHeaderExtractor.HeadingAgent.Runtime;
using DocxHeaderExtractor.HeadingAgent.Selectors;

if (args.Length != 4) throw new ArgumentException("Usage: <pdf072> <heldout-pdf> <artifact-root> <identity-json>");
var output = Path.GetFullPath(args[2]); Directory.CreateDirectory(output);
if (File.Exists(Path.Combine(output, "study-start.json"))) throw new InvalidOperationException("REFUSE_OVERWRITING_STUDY");
var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("API_KEY_NOT_CONFIGURED");
var sources = args.Take(2).Select(p => AgentSource.Load(Path.GetFullPath(p), out var failure, sourceBound: true)
    ?? throw new InvalidOperationException(failure)).ToArray();
var jobs = new[] { (Id: "pdf072-000", Source: 0, Segment: 0), (Id: "pdf072-007", Source: 0, Segment: 7),
    (Id: "pdf072-012", Source: 0, Segment: 12), (Id: "pdf076-000", Source: 1, Segment: 0) };
var phases = new[] { (Id: "01-A-serial", Candidate: false, Jobs: 1), (Id: "02-B-parallel", Candidate: true, Jobs: 6),
    (Id: "03-B-serial", Candidate: true, Jobs: 1), (Id: "04-A-parallel", Candidate: false, Jobs: 6) };
var json = new JsonSerializerOptions { WriteIndented = true };
foreach (var job in jobs)
foreach (var candidate in new[] { false, true })
{
    var source = sources[job.Source]; var instruction = candidate ? PdfMembershipInstructionV2.Instruction : PdfAgentProtocol.Instruction;
    var request = AgentUnit.PrepareFirstRequest(source, job.Segment, instruction);
    File.WriteAllText(Path.Combine(output, job.Id + (candidate ? ".B" : ".A") + ".first-request.json"), request.Body);
    // Prompt ablation: only the instruction and its hash may differ in the user payload.
    if (candidate)
    {
        var a = JsonNode.Parse(AgentUnit.PrepareFirstRequest(source, job.Segment, PdfAgentProtocol.Instruction).Body)!;
        var b = JsonNode.Parse(request.Body)!;
        var pa = JsonNode.Parse(a["messages"]![1]!["content"]![0]!["text"]!.GetValue<string>())!.AsObject();
        var pb = JsonNode.Parse(b["messages"]![1]!["content"]![0]!["text"]!.GetValue<string>())!.AsObject();
        pa.Remove("instruction"); pa.Remove("instructionSha256"); pb.Remove("instruction"); pb.Remove("instructionSha256");
        if (!JsonNode.DeepEquals(pa, pb)) throw new InvalidOperationException("STUDY_EVIDENCE_CHANGED");
        a["messages"]![1]!["content"]![0]!["text"] = "<instruction ablation>";
        b["messages"]![1]!["content"]![0]!["text"] = "<instruction ablation>";
        if (!JsonNode.DeepEquals(a, b)) throw new InvalidOperationException("STUDY_WIRE_SETTINGS_CHANGED");
    }
}
File.WriteAllText(Path.Combine(output, "study-start.json"), JsonSerializer.Serialize(new
{
    phases = phases.Select(p => new { p.Id, p.Candidate, p.Jobs }), buildIdentity = JsonDocument.Parse(File.ReadAllText(args[3])).RootElement.Clone(),
    jobs = jobs.Select(j => new { j.Id, j.Source, j.Segment, sourceSha256 = sources[j.Source].SourceSha256,
        targets = sources[j.Source].Scope(j.Segment).Decide }),
    purpose = "A=source-bound V1; B=same protocol with clarified membership instruction; serial versus configured parallelism=6 (four concurrent jobs available). No Gold is sent."
}, json));
var runBudget = new RunResourceBudget(0.5, 200); using var scope = runBudget.Enter();
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(20));
var stdoutGate = new object();
foreach (var phase in phases)
{
    var phaseClock = Stopwatch.StartNew();
    var order = phase.Id.StartsWith("03") || phase.Id.StartsWith("04") ? jobs.Reverse().ToArray() : jobs;
    await Parallel.ForEachAsync(order, new ParallelOptions { MaxDegreeOfParallelism = phase.Jobs, CancellationToken = deadline.Token }, (job, ct) =>
    {
        var source = sources[job.Source]; var instruction = phase.Candidate ? PdfMembershipInstructionV2.Instruction : PdfAgentProtocol.Instruction;
        var budget = AgentSelector.BudgetFor(source, instruction);
        using var handler = AgentModel.CreateHandler();
        using var model = new AgentModel(handler, key, budget, cancellationToken: ct, rateLimitRetries: AgentModel.ProductionRateLimitRetries);
        var prefix = Path.Combine(output, phase.Id + "." + job.Id);
        var replies = new List<ModelReply>();
        var traced = AgentSelector.Traced(model, r =>
        {
            replies.Add(r);
            File.WriteAllText(prefix + ".replies.json", JsonSerializer.Serialize(replies, json));
        });
        var clock = Stopwatch.StartNew();
        var outcome = AgentUnit.Run(source, job.Segment, traced, budget, instruction: instruction);
        clock.Stop();
        var first = AgentUnit.PrepareFirstRequest(source, job.Segment, instruction);
        if (replies.Count == 0 || replies[0].RequestSha256 != first.Sha256) throw new InvalidOperationException("STUDY_ACTUAL_REQUEST_CHANGED");
        File.WriteAllText(prefix + ".outcome.json", JsonSerializer.Serialize(new { phase = phase.Id, candidate = phase.Candidate,
            configuredParallelism = phase.Jobs, job = job.Id, sourceSha256 = source.SourceSha256, targets = source.Scope(job.Segment).Decide,
            requestSha256 = first.Sha256, seconds = clock.Elapsed.TotalSeconds, outcome, resources = ResourceAudit.Observe(budget) }, json));
        lock (stdoutGate) Console.WriteLine(JsonSerializer.Serialize(new { phase = phase.Id, job = job.Id, outcome.Status,
            outcome.Failure, outcome.RepairsUsed, seconds = clock.Elapsed.TotalSeconds, selected = outcome.Decisions.Where(d => d.Value == "SELECT").Select(d => d.Key) }));
        return ValueTask.CompletedTask;
    });
    File.WriteAllText(Path.Combine(output, phase.Id + ".budget.json"), JsonSerializer.Serialize(new { seconds = phaseClock.Elapsed.TotalSeconds,
        runBudget = runBudget.Observe() }, json));
}
File.WriteAllText(Path.Combine(output, "study-complete.json"), JsonSerializer.Serialize(runBudget.Observe(), json));
