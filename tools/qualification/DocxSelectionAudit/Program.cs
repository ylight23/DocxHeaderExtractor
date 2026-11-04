using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Agents.Budget;
using DocxHeaderExtractor.HeadingAgent.Selectors;

if (args.Length != 3 || args[0] != "run")
{
    Console.Error.WriteLine("Usage: DocxSelectionAudit run <source.docx> <new-output-directory>");
    return 2;
}
var source = Path.GetFullPath(args[1]);
var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(source)));
var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("OPENROUTER_API_KEY_MISSING");
var output = Path.GetFullPath(args[2]);
if (Directory.Exists(output)) throw new IOException("OUTPUT_DIRECTORY_ALREADY_EXISTS");
Directory.CreateDirectory(output);
var original = new DocxSelector();
var field = typeof(AgentSelector).GetField("instruction", BindingFlags.Instance | BindingFlags.NonPublic)
    ?? throw new InvalidOperationException("DEFAULT_INSTRUCTION_NOT_FOUND");
var baseline = field.GetValue(original) as string ?? throw new InvalidOperationException("DEFAULT_INSTRUCTION_MISSING");
const string addition = "Assess a candidate span separately from the remainder of its paragraph. " +
    "Prose following a candidate, numbering or ordinary formatting alone does not establish that the candidate is body text. " +
    "Conversely, emphasis, shortness or numbering alone does not establish a heading. " +
    "Decide the candidate's structural function from its own occurrence and surrounding content; use an exact heading extent when needed. " +
    "If the evidence does not establish its function, report unresolved rather than guessing.";
var candidate = baseline + "\n" + addition;
var json = new JsonSerializerOptions { WriteIndented = true };
File.WriteAllText(Path.Combine(output, "run-identity.json"), JsonSerializer.Serialize(new
{
    sourceSha256 = hash, baselineInstructionSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(baseline))),
    candidateInstructionSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(candidate))), addition,
    route = "DocxSelector", budgetUsd = 1.0, maxAttempts = 12, deadlineMinutes = 8,
}, json));
var responsePath = Path.Combine(output, "responses.jsonl");
var gate = new object();
var selector = new DocxSelector(instruction: candidate, onModelReply: reply =>
{
    lock (gate) File.AppendAllText(responsePath, JsonSerializer.Serialize(new
    {
        reply.Unit, reply.Turn, reply.Content, reply.FinishReason, reply.RequestSha256,
        toolNames = reply.ToolCalls.Select(t => t.Name).ToArray(),
    }) + Environment.NewLine);
});
var budget = new RunResourceBudget(1.0, 12);
using var scope = budget.Enter();
using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(8));
var watch = System.Diagnostics.Stopwatch.StartNew();
var selected = await selector.SelectAsync(source, key, cancel.Token);
if (hash != Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(source)))) throw new InvalidDataException("SOURCE_CHANGED_DURING_RUN");
File.WriteAllText(Path.Combine(output, "selection.json"), JsonSerializer.Serialize(new
{
    elapsedSeconds = watch.Elapsed.TotalSeconds, selectorId = selector.SelectorId, selection = selected, budget = budget.Observe(),
}, json));
Console.WriteLine($"units={selected.UnitsComplete}/{selected.UnitsTotal} selected={selected.SelectedAliases.Count} failures={selected.UnitFailures.Count} seconds={watch.Elapsed.TotalSeconds:F1}");
return selected.UnitsComplete == selected.UnitsTotal && selected.UnitFailures.Count == 0 ? 0 : 1;
