using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DocxHeaderExtractor.App;
using DocxHeaderExtractor.Contracts.Agents;
using DocxHeaderExtractor.Contracts.Provider;
using DocxHeaderExtractor.DocumentProcessing.Harness;
using DocxHeaderExtractor.HeadingAgent.Selectors;

/// <summary>Qualification-only paired control/candidate with unchanged GROUP and PLACE contracts.</summary>
static class DocxBoundaryTrial
{
    public const string Addition = "Formatting evidence formatBoundaries gives the end of the preceding non-whitespace formatting stretch and the start of the following non-whitespace stretch; the gap between them may contain whitespace. These are measurements, not required heading boundaries. Decide the extent from the source and its context. A headingExtent must not begin or end with whitespace. Its text must be the exact source substring at its UTF-16 start and exclusive end; do not include a whitespace gap at the boundary.";
    public const string TrimOnly = "For each headingExtent, the text must be the exact source substring at its UTF-16 start and exclusive end, and must not begin or end with whitespace. Decide the extent yourself from the source and context.";

    sealed class Provider(OpenRouterProvider route, IHeadingSelector selector) : IModelProvider
    {
        public ModelRoute CreateRoute() => route.CreateRoute();
        public T? Create<T>(string role) where T : class => role == AgentRoles.DocxSelector ? selector as T : null;
    }

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 3) throw new ArgumentException("docx-boundary-ab <output-dir> <docx>...");
        var output = Path.GetFullPath(args[1]);
        if (Directory.Exists(output)) throw new IOException("OUTPUT_DIRECTORY_ALREADY_EXISTS");
        var route = OpenRouterProvider.FromEnvironment();
        if (!route.HasKey) throw new InvalidOperationException("OPENROUTER_API_KEY_MISSING");
        Directory.CreateDirectory(output);
        var original = new DocxSelector();
        var baseline = (string)typeof(AgentSelector).GetField("instruction", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(original)!;
        var candidate = baseline + "\n" + Addition;
        var trimOnly = baseline + "\n" + TrimOnly;
        var json = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var compact = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        File.WriteAllText(Path.Combine(output, "identity.json"), JsonSerializer.Serialize(new { baselineInstructionSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(baseline))), candidateInstructionSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(candidate))), trimOnlyInstructionSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(trimOnly))), addition = Addition, trimOnly = TrimOnly, mode = args[0], note = "GROUP and PLACE remain baseline; instruction-only references a field intentionally absent, trim-only does not." }, json));
        var guard = new object();
        foreach (var input in args.Skip(2))
        {
            var file = Path.GetFullPath(input);
            var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)));
            var name = Path.GetFileNameWithoutExtension(file);
            var phases = args[0] == "docx-boundary-isolate" ? new[] { "facts-only", "instruction-only" } : args[0] == "docx-boundary-trim-only" ? new[] { "trim-only" } : args[0] == "docx-boundary-batch-repair" ? new[] { "batch-repair" } : args[0] == "docx-production-batch-repair" ? new[] { "batch-production" } : args[0] == "docx-format-runs-ab" ? new[] { "control-a", "format-runs-a", "control-b", "format-runs-b" } : args[0] == "docx-format-runs-pair" ? new[] { "control-a", "format-runs-a" } : new[] { "control", "candidate" };
            foreach (var phase in phases)
            {
                var log = Path.Combine(output, name + "-" + phase + "-responses.jsonl");
                void Observe(ModelReply reply)
                {
                    var row = new { reply.Unit, reply.Turn, reply.Content, reply.FinishReason, reply.RequestSha256, reply.ToolCalls, reply.ToolResults };
                    lock (guard) File.AppendAllText(log, JsonSerializer.Serialize(row, compact) + "\n");
                    Console.WriteLine($"{name} {phase} reply {reply.Unit} turn={reply.Turn}");
                }
                var selector = new DocxSelector(instruction: phase is "trim-only" or "batch-repair" ? trimOnly : phase is "candidate" or "instruction-only" ? candidate : baseline, onModelReply: Observe,
                    qualificationFormatBoundaryEvidence: phase is "candidate" or "facts-only", qualificationGroupingInstruction: baseline,
                    qualificationBatchExtentErrors: phase is "batch-repair" or "batch-production",
                    qualificationFormatRunEvidence: phase.StartsWith("format-runs-", StringComparison.Ordinal));
                var provider = new Provider(route, selector);
                var options = new PipelineOptions { DocxReuseSelection = false, MaxProviderCostUsd = 1, MaxProviderAttempts = 80 };
                var clock = Stopwatch.StartNew();
                try
                {
                    var result = await new DocumentRunner(options, provider).ExtractAsync(file);
                    var audit = result.HeadingPipeline?.Audit;
                    File.WriteAllText(Path.Combine(output, name + "-" + phase + ".json"), JsonSerializer.Serialize(new { sourceSha256 = hash, phase,
                        seconds = clock.Elapsed.TotalSeconds, selectorId = selector.SelectorId, outline = result.Outline, budget = result.ResourceBudget,
                        selectionCalls = audit?.SelectionProviderCalls, selectionResources = audit?.SelectionResources,
                        selectionLinesAnswered = audit?.SelectionLinesAnswered, selectionLinesTotal = audit?.SelectionLinesTotal,
                        grouping = audit?.Grouping, placement = audit?.PlacementExecution, stageSeconds = audit?.HeadingStageSeconds }, json));
                    Console.WriteLine($"{name} {phase} complete seconds={clock.Elapsed.TotalSeconds:F1} calls={audit?.SelectionProviderCalls} place={audit?.PlacementExecution?.LogicalCalls}");
                    if (Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))) != hash) throw new InvalidDataException("SOURCE_CHANGED");
                }
                catch (Exception ex)
                {
                    File.WriteAllText(Path.Combine(output, name + "-" + phase + "-error.txt"), ex.ToString());
                    Console.Error.WriteLine($"{name} {phase} failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        return 0;
    }
}
