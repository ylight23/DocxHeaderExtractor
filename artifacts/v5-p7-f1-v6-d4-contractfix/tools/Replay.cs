using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DocxHeaderExtractor.V5Qualification.P7;

// Offline replay of the D4.5 live run. The real Run() of the compiled code is driven by a model that returns only the recorded replies,
// keyed by (unit, round). A unit with no recorded reply gets NO_RECORDED_REPLY, which is not a contract failure. Nothing is sent.
var repo = args[0]; var runDir = args[1]; var outPath = args[2]; var label = args[3];
var gold = P7F1V6DirectD45.ReadGoldPin(repo);
var u = P7F1V6Universe.Rebuild(repo, P7F1V6DirectD45.Document, gold.SourceSha256, out var why) ?? throw new InvalidOperationException(why);

using var sum = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(runDir, "run-summary.json")));
var recorded = new Dictionary<(string, int), (string Content, string LiveRequestSha)>();
foreach (var e in sum.RootElement.GetProperty("ledger").EnumerateArray())
{
    var raw = File.ReadAllText(Path.Combine(runDir, e.GetProperty("RawStoreKey").GetString()! + ".bin"), Encoding.UTF8);
    var (content, failure) = P7F1V6ResponseAdapter.ExtractContent(raw);
    recorded[(e.GetProperty("UnitId").GetString()!, e.GetProperty("Round").GetInt32())] = (content ?? throw new InvalidOperationException(failure), e.GetProperty("RequestSha256").GetString()!);
}

var consumed = new List<(string Unit, int Round, bool RequestMatchesLive)>();
var budget = P7F1V6DirectD45.NewBudget();
var outcome = P7F1V6DirectV2.Run(P7F1V6DirectD45.Instruction, u, (unit, round, req) =>
{
    if (!recorded.TryGetValue((unit, round), out var rec)) throw new P7F1V6DirectV2.ReplyFailure("NO_RECORDED_REPLY");
    consumed.Add((unit, round, req.Sha256 == rec.LiveRequestSha));
    return rec.Content;
}, budget, P7F1V6DirectD45.Overlap, P7F1V6DirectD45.Unit, finalize: true);

var recordedUnits = recorded.Keys.Select(k => k.Item1).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
var rows = recordedUnits.Select(id =>
{
    var unit = outcome.Units.FirstOrDefault(x => x.UnitId == id);
    return new
    {
        unit = id,
        replyRounds = recorded.Keys.Count(k => k.Item1 == id),
        consumedRounds = consumed.Count(c => c.Unit == id),
        requestsMatchLive = consumed.Where(c => c.Unit == id).All(c => c.RequestMatchesLive),
        status = unit?.Status ?? "NOT_REACHED",
        failure = unit?.Failure,
    };
}).ToList();

var report = new
{
    artifact = "p7.f1.v6.d4.5-offline-replay.v1",
    label,
    providerCalls = 0,
    promptVersion = P7F1V6DirectV2.PromptVersion,
    systemPromptSha256 = P7F1V6DirectV2.ComputedSystemPromptSha256(),
    recordedReplies = recorded.Count,
    consumedReplies = consumed.Count,
    haltReason = budget.HaltReason,
    contractFailures = budget.ContractFailures,
    unitsWithReplies = rows.Count,
    passingUnits = rows.Count(r => r.status == "COMPLETE"),
    rows,
    runStatus = outcome.Status,
    note = "Units without a recorded reply end NO_RECORDED_REPLY; run status is therefore not a prediction of a live run.",
};
File.WriteAllText(outPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n");
Console.WriteLine($"{label}: prompt {report.promptVersion} {report.systemPromptSha256[..12]}, consumed {report.consumedReplies}/{report.recordedReplies}, halt {report.haltReason ?? "none"}, contract failures {report.contractFailures}, passing {report.passingUnits}/{report.unitsWithReplies}");
foreach (var r in rows) Console.WriteLine($"  {r.unit} rounds {r.consumedRounds}/{r.replyRounds} requestMatchesLive={r.requestsMatchLive} {r.status} {r.failure}");
