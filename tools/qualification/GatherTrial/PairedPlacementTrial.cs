using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.App;
using DocxHeaderExtractor.Contracts.Agents;
using DocxHeaderExtractor.Contracts.Provider;
using DocxHeaderExtractor.DocumentProcessing.Harness;
using DocxHeaderExtractor.HeadingAgent.Placement;
using DocxHeaderExtractor.Agents.Budget;
using DocxHeaderExtractor.DocumentProcessing.Gather;

/// <summary>Qualification only: a single real placement proposal, replayed verbatim before real chain repair.</summary>
static class PairedPlacementTrial
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static void Save(string path, object data) => File.WriteAllText(path, JsonSerializer.Serialize(data, Json));

    sealed class Provider(OpenRouterProvider route, IHeadingPlacer placer) : IModelProvider
    {
        public ModelRoute CreateRoute() => route.CreateRoute();
        public T? Create<T>(string role) where T : class => role == AgentRoles.Placer ? placer as T : null;
    }

    sealed class ReplayHandler(IReadOnlyList<string> evidence) : DelegatingHandler(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        readonly Queue<JsonNode> saved = new(evidence.Select(s => JsonNode.Parse(s)!).Where(n => (string?)n["stage"] == "HIERARCHY_PLACE"));
        public int Replayed { get; private set; }
        public int Remaining => saved.Count;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            var content = body["messages"]![1]!["content"]!;
            var text = content is JsonArray a ? a[0]!["text"]!.GetValue<string>() : content.GetValue<string>();
            var packet = JsonNode.Parse(text)!;
            if ((string?)packet["stage"] != "HIERARCHY_PLACE") return await base.SendAsync(request, ct);
            if (saved.Count == 0) throw new InvalidOperationException("REPLAY_MISSING_RESPONSE");
            var prior = saved.Dequeue();
            if (!JsonNode.DeepEquals(packet, JsonNode.Parse(prior["request"]!.GetValue<string>())))
                throw new InvalidOperationException("REPLAY_PACKET_DIFFERED");
            if ((string?)prior["FinishReason"] != "stop" || prior["response"] is null)
                throw new InvalidOperationException("REPLAY_RESPONSE_NOT_COMPLETE");
            Replayed++;
            string Event(object o) => "data: " + JsonSerializer.Serialize(o) + "\n\n";
            var sse = Event(new { id = "paired-replay", provider = "Alibaba", model = "qwen/qwen3.7-flash", choices = new[] { new { index = 0, delta = new { content = prior["response"]!.GetValue<string>() } } } })
                + Event(new { id = "paired-replay", provider = "Alibaba", model = "qwen/qwen3.7-flash", choices = new[] { new { index = 0, delta = new { }, finish_reason = "stop" } }, usage = new { prompt_tokens = 0, completion_tokens = 0, cost = 0.0 } })
                + "data: [DONE]\n\n";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") };
        }
    }

    sealed class PairedPlacer(string output) : IHeadingPlacer
    {
        public string Contract => "qualification:paired-placement-replay-v1";
        public async Task<PlacementProposal> ProposeAsync(PlacementRequest request, ModelAccess access, CancellationToken ct)
        {
            Save(Path.Combine(output, "placement-input.json"), request);
            var control = await new HeadingPlacer(repairParentChains: false).ProposeAsync(request, access, ct);
            Save(Path.Combine(output, "control-proposal.json"), control);
            Save(Path.Combine(output, "control-evidence.json"), control.ModelEvidence.Select(s => JsonNode.Parse(s)).ToArray());
            var replay = new ReplayHandler(control.ModelEvidence);
            var ctor = typeof(HeadingPlacer).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
                [typeof(HttpMessageHandler), typeof(bool), typeof(bool), typeof(int)], null)
                ?? throw new InvalidOperationException("HEADING_PLACER_REPLAY_CONSTRUCTOR_MISSING");
            var candidate = (HeadingPlacer)ctor.Invoke([replay, false, true, 16]);
            var repaired = await candidate.ProposeAsync(request, access, ct);
            Save(Path.Combine(output, "repair-proposal.json"), repaired);
            Save(Path.Combine(output, "repair-evidence.json"), repaired.ModelEvidence.Select(s => JsonNode.Parse(s)).ToArray());
            if (replay.Remaining != 0) throw new InvalidOperationException("REPLAY_RESPONSES_REMAIN");
            var before = control.Parents.ToDictionary(p => p.Key, p => p.Value);
            var after = repaired.Parents.ToDictionary(p => p.Key, p => p.Value);
            Save(Path.Combine(output, "paired-relations.json"), new
            {
                replayedResponses = replay.Replayed, sameHeadingInput = true,
                rows = request.Ordered.Select(h => new { h.Alias, h.SourceId, h.Text, before = before.GetValueOrDefault(h.Alias),
                    after = after.GetValueOrDefault(h.Alias), beforeBasis = control.Basis.GetValueOrDefault(h.Alias), afterBasis = repaired.Basis.GetValueOrDefault(h.Alias) }),
                control.ChainRepair, repair = repaired.ChainRepair, control.Resources, repairResources = repaired.Resources,
                controlFailures = control.Failures, repairFailures = repaired.Failures,
                note = "Repair resources include zero-cost locally replayed calls; compare real repair calls separately."
            });
            return repaired;
        }
    }

    public static async Task<int> Frozen21Async(string[] args)
    {
        if (args.Length != 5) throw new ArgumentException("paired-frozen21 <output-directory> <docx> <old-outline-json> <recent-placement-input-json>");
        var output = Path.GetFullPath(args[1]);
        if (Directory.Exists(output)) throw new IOException("OUTPUT_DIRECTORY_ALREADY_EXISTS");
        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("OPENROUTER_API_KEY_MISSING");
        var sha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(args[2])));
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var old = JsonSerializer.Deserialize<StoredOutline>(File.ReadAllText(args[3]), opts) ?? throw new InvalidDataException("OUTLINE_INVALID");
        var recent = JsonSerializer.Deserialize<PlacementRequest>(File.ReadAllText(args[4]), opts) ?? throw new InvalidDataException("PLACEMENT_INPUT_INVALID");
        if (old.SourceSha256 != sha || recent.Evidence.Source?.SourceSha256 != sha) throw new InvalidDataException("SOURCE_HASH_MISMATCH");
        var lines = recent.Evidence.Source.Lines.ToDictionary(l => l.Alias, StringComparer.Ordinal);
        var headings = new List<DocxHeaderExtractor.Contracts.Models.CanonicalSemanticBoundHeading>();
        foreach (var h in old.Headings.OrderBy(h => h.Index))
        {
            var matches = recent.Ordered.Where(p => p.SourceId == h.StableId).ToArray();
            if (matches.Length != 1 || matches[0].Text != h.Text || matches[0].Parts.Count != 1 || h.HeadingSpan is null)
                throw new InvalidDataException("HEADING_OR_PARTS_NOT_RECONSTRUCTABLE:" + h.StableId);
            var b = matches[0]; var part = b.Parts[0];
            if (!lines.TryGetValue(part.Alias, out var line) || line.SourceId != part.SourceId || line.Ordinal != part.SourceOrdinal
                || part.Start != h.HeadingSpan.Start || part.End != h.HeadingSpan.End || part.Start < 0 || part.End > line.Text.Length
                || line.Text[part.Start..part.End] != part.Text || part.SourceId != h.StableId)
                throw new InvalidDataException("HEADING_SOURCE_BINDING_MISMATCH:" + h.StableId);
            headings.Add(b with { RelationHints = [] });
        }
        if (headings.Count != 21 || headings.Select(h => h.Alias).Distinct().Count() != 21)
            throw new InvalidDataException("FROZEN_HEADING_COUNT_INVALID");
        var frozen = new PlacementRequest(headings, recent.Evidence);
        Directory.CreateDirectory(output);
        Save(Path.Combine(output, "reconstruction.json"), new { sourceSha256 = sha, headingCount = headings.Count,
            oldOutlineSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(args[3]))),
            recentInputSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(args[4]))),
            note = "21 old heading texts, positions and single-part spans checked; selection states and full evidence from recent same-file run, not recovered old run. Excluded headings may remain in upstreamWithheld. No old parent proposal recovered." });
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var access = new ModelAccess(key, () => deadline.Token.ThrowIfCancellationRequested());
        for (var i = 1; i <= 3; i++)
        {
            var dir = Path.Combine(output, $"run-{i:00}"); Directory.CreateDirectory(dir);
            var runBudget = new RunResourceBudget(0.10, 40);
            try
            {
                using var scope = runBudget.Enter();
                var result = await new PairedPlacer(dir).ProposeAsync(frozen, access, deadline.Token);
                Save(Path.Combine(dir, "budget.json"), runBudget.Observe());
                Console.WriteLine($"run {i}: headings={headings.Count}, conflicts={result.ChainRepair?.InitialConflicts.Count}, repairCalls={result.ChainRepair?.LogicalCalls}, confirmedUsd={runBudget.Observe().ConfirmedUsd}");
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(dir, "error.txt"), ex.ToString()); Save(Path.Combine(dir, "budget.json"), runBudget.Observe()); throw; }
        }
        if (Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(args[2]))) != sha) throw new InvalidDataException("SOURCE_CHANGED");
        return 0;
    }

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 3) throw new ArgumentException("paired-placement <output-directory> <docx>");
        var output = Path.GetFullPath(args[1]);
        if (Directory.Exists(output)) throw new IOException("OUTPUT_DIRECTORY_ALREADY_EXISTS");
        var input = Path.GetFullPath(args[2]);
        var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(input)));
        var provider = OpenRouterProvider.FromEnvironment();
        if (!provider.HasKey) throw new InvalidOperationException("OPENROUTER_API_KEY_MISSING");
        Directory.CreateDirectory(output);
        Save(Path.Combine(output, "identity.json"), new { input, sha256 = hash, route = "docx-production", maxCostUsd = 0.20, note = "one SELECT/GROUP; paired PLACE replay" });
        try
        {
            var options = new PipelineOptions { DocxReuseSelection = false, MaxProviderCostUsd = 0.20, MaxProviderAttempts = 50 };
            var result = await new DocumentRunner(options, new Provider(provider, new PairedPlacer(output))).ExtractAsync(input);
            Save(Path.Combine(output, "result.json"), new { result.Outline, placement = result.HeadingPipeline?.Audit?.PlacementExecution,
                grouping = result.HeadingPipeline?.Audit?.Grouping, audit = result.HeadingPipeline?.Audit, result.ResourceBudget });
            if (Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(input))) != hash) throw new InvalidDataException("SOURCE_CHANGED");
            Console.WriteLine($"Complete: {result.Outline.Headings.Count} headings; confirmed USD {result.ResourceBudget.ConfirmedUsd}");
            return 0;
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(output, "error.txt"), ex.ToString()); throw; }
    }
}
