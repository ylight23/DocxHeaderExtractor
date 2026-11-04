using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.App;
using DocxHeaderExtractor.Contracts.Agents;
using DocxHeaderExtractor.Contracts.Provider;
using DocxHeaderExtractor.DocumentProcessing.Harness;
using DocxHeaderExtractor.HeadingAgent.Selectors;

/// <summary>Qualification only: replay one validated SELECT while running production GROUP/PLACE with real provider.</summary>
static class DocxFrozenGroupTrial
{
    const string Addition = "When deciding whether adjacent selected parts form one heading, evaluate their source text and the content immediately after the run. Join parts only when they jointly name the same unit. If a selected part introduces its own unit of content, keep it separate from another heading even when adjacent, similarly styled, or under a broader heading. A short label and a following title may still form one heading when the source shows that they name one unit together. Use the evidence in this document; do not change selection membership.";
    sealed class Provider(OpenRouterProvider route, IHeadingSelector selector) : IModelProvider
    {
        public ModelRoute CreateRoute() => route.CreateRoute();
        public T? Create<T>(string role) where T : class => role == AgentRoles.DocxSelector ? selector as T : null;
    }
    sealed class ReplayHandler(string response, string logPath) : DelegatingHandler(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            var node = JsonNode.Parse(body)!;
            var content = node["messages"]![1]!["content"]!;
            var text = content is JsonArray a ? a[0]!["text"]!.GetValue<string>() : content.GetValue<string>();
            var payload = JsonNode.Parse(text)!;
            if (payload["stage"]?.GetValue<string>() == "SEGMENT")
            {
                string Event(object o) => "data: " + JsonSerializer.Serialize(o) + "\n\n";
                var sse = Event(new { id = "frozen-selection", provider = "Alibaba", model = "qwen/qwen3.7-flash", choices = new[] { new { index = 0, delta = new { content = response } } } })
                    + Event(new { id = "frozen-selection", provider = "Alibaba", model = "qwen/qwen3.7-flash", choices = new[] { new { index = 0, delta = new { }, finish_reason = "stop" } }, usage = new { prompt_tokens = 0, completion_tokens = 0, cost = 0.0 } })
                    + "data: [DONE]\n\n";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") };
            }
            if (payload["stage"]?.GetValue<string>() == "GROUP") File.WriteAllText(logPath, body);
            return await base.SendAsync(request, ct);
        }
    }
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length is < 4 or > 5 || args.Length == 5 && args[4] != "production") throw new ArgumentException("docx-frozen-group <output-dir> <docx> <response-jsonl> [production]");
        var output = Path.GetFullPath(args[1]);
        if (Directory.Exists(output)) throw new IOException("OUTPUT_DIRECTORY_ALREADY_EXISTS");
        var route = OpenRouterProvider.FromEnvironment();
        if (!route.HasKey) throw new InvalidOperationException("OPENROUTER_API_KEY_MISSING");
        var input = Path.GetFullPath(args[2]);
        var sourceHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(input)));
        var selection = File.ReadLines(args[3]).Select(line => JsonNode.Parse(line)!).Where(r => r["Unit"]!.GetValue<string>() == "SEG-000").Last()["Content"]!.GetValue<string>();
        var selectionHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(selection)));
        var baseline = (string)typeof(AgentSelector).GetField("instruction", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(new DocxSelector())!;
        Directory.CreateDirectory(output);
        var json = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        File.WriteAllText(Path.Combine(output, "identity.json"), JsonSerializer.Serialize(new { sourceHash, selectionHash, selectionSource = Path.GetFullPath(args[3]), baselineInstructionSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(baseline))), groupingAddition = Addition }, json));
        foreach (var phase in args.Length == 5 ? new[] { "production" } : new[] { "control-a", "candidate-a", "control-b", "candidate-b" })
        {
            var groupRequest = Path.Combine(output, phase + "-group-request.json");
            var replies = Path.Combine(output, phase + "-responses.jsonl");
            void Observe(ModelReply reply)
            {
                File.AppendAllText(replies, JsonSerializer.Serialize(new { reply.Unit, reply.Turn, reply.Content, reply.FinishReason, reply.RequestSha256 }) + "\n");
                Console.WriteLine($"{phase} {reply.Unit} turn={reply.Turn}");
            }
            string? groupInstruction = phase == "production" ? null : phase.StartsWith("candidate", StringComparison.Ordinal) ? baseline + "\n" + Addition : baseline;
            var selector = new DocxSelector(handlerFactory: () => new ReplayHandler(selection, groupRequest), onModelReply: Observe, instruction: baseline, qualificationGroupingInstruction: groupInstruction);
            var options = new PipelineOptions { DocxReuseSelection = false, MaxProviderCostUsd = 1, MaxProviderAttempts = 30 };
            var clock = Stopwatch.StartNew();
            try
            {
                var result = await new DocumentRunner(options, new Provider(route, selector)).ExtractAsync(input);
                File.WriteAllText(Path.Combine(output, phase + ".json"), JsonSerializer.Serialize(new { sourceHash, selectionHash, phase, seconds = clock.Elapsed.TotalSeconds,
                    outline = result.Outline, grouping = result.HeadingPipeline?.Audit?.Grouping, placement = result.HeadingPipeline?.Audit?.PlacementExecution, budget = result.ResourceBudget }, json));
                Console.WriteLine($"{phase} complete {clock.Elapsed.TotalSeconds:F1}s");
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(output, phase + "-error.txt"), ex.ToString()); Console.Error.WriteLine(phase + " " + ex.Message); }
        }
        if (Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(input))) != sourceHash) throw new InvalidDataException("SOURCE_CHANGED");
        return 0;
    }
}
