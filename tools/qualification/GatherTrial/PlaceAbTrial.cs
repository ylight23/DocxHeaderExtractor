using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DocxHeaderExtractor.App;
using DocxHeaderExtractor.Agents.Budget;
using DocxHeaderExtractor.Contracts.Agents;
using DocxHeaderExtractor.Contracts.Provider;
using DocxHeaderExtractor.DocumentProcessing.Harness;
using DocxHeaderExtractor.HeadingAgent.Placement;
using DocxHeaderExtractor.HeadingAgent.Selectors;
using DocxHeaderExtractor.DocumentProcessing.Gather;

/// <summary>
/// Qualification only: one production DOCX extraction per file, capturing the exact PLACE input it built; then PLACE on that same input,
/// split (production default) and whole-document, several times each. Saves parents per run. Outputs contain document text: do not commit.
/// </summary>
static class PlaceAbTrial
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static void Save(string path, object data) => File.WriteAllText(path, JsonSerializer.Serialize(data, Json));

    /// <summary>Rebuild a gather plan from two saved split-run outlines, verifying both original file hashes. Never calls a model or writes Word.</summary>
    public static int SplitReplay(string[] args)
    {
        if (args.Length != 4) throw new ArgumentException("split-replay <target-summary.json> <source-summary.json> <output-directory>");
        var output = Path.GetFullPath(args[3]);
        if (Directory.Exists(output)) throw new IOException("OUTPUT_DIRECTORY_ALREADY_EXISTS");
        (string Path, IReadOnlyList<DocxHeaderExtractor.DocumentProcessing.Output.HeadingRecord> Headings) Load(string file)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            var path = root.GetProperty("input").GetString() ?? throw new InvalidDataException("INPUT_MISSING");
            var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
            if (hash != root.GetProperty("sha256").GetString()) throw new InvalidDataException("SOURCE_HASH_MISMATCH");
            var heads = root.GetProperty("headings").EnumerateArray().Select((h, i) => new DocxHeaderExtractor.DocumentProcessing.Output.HeadingRecord
            {
                Index = i, StableId = h.GetProperty("StableId").GetString()!, Text = h.GetProperty("Text").GetString()!,
                Level = h.GetProperty("Level").ValueKind == JsonValueKind.Null ? null : h.GetProperty("Level").GetInt32(),
                HierarchyResolution = h.GetProperty("HierarchyResolution").GetString(),
            }).ToArray();
            return (path, heads);
        }
        var target = Load(args[1]);
        var source = Load(args[2]);
        var plan = GatherByHeading.RequireNewHeadingReview(GatherByHeading.Build(target.Path, target.Headings,
            [new GatherSourceFile("T9", source.Path, source.Headings)], addMissingHeadings: true));
        Directory.CreateDirectory(output);
        Save(Path.Combine(output, "plan.json"), plan);
        var demo = GatherWriteback.CreateDemo(target.Path, Path.Combine(output, "DEMO_CHUA_DUYET.docx"),
            GatherByHeading.DecidedByChain(plan.Sections).Where(s => s.Run.Facts.Count > 0).ToList(), plan.Uncertain, DateTime.UtcNow);
        Save(Path.Combine(output, "demo-write-result.json"), demo);
        Console.WriteLine($"target headings={target.Headings.Count}; source headings={source.Headings.Count}; receiving={plan.Sections.Count(s => s.Run.Facts.Count > 0)}; uncertain={plan.Uncertain.Count}; excluded={plan.Excluded.Count}");
        Console.WriteLine($"demo skipped={demo.SkippedHeadings.Count}");
        foreach (var group in plan.Uncertain.GroupBy(u => u.Kind)) Console.WriteLine($"uncertain {group.Key}={group.Count()}");
        return 0;
    }

    sealed class Provider(OpenRouterProvider route, IHeadingPlacer placer, IHeadingSelector? selector = null) : IModelProvider
    {
        public ModelRoute CreateRoute() => route.CreateRoute();
        public T? Create<T>(string role) where T : class => role == AgentRoles.Placer ? placer as T : role == AgentRoles.DocxSelector ? selector as T : null;
    }

    /// <summary>Production placer (split), recording the request it was given and its proposal.</summary>
    sealed class CapturingPlacer(bool wholeDocument = false) : IHeadingPlacer
    {
        readonly HeadingPlacer inner = new(wholeDocument: wholeDocument, repairParentChains: false);
        public PlacementRequest? Request { get; private set; }
        public PlacementProposal? Proposal { get; private set; }
        public string Contract => inner.Contract;
        public async Task<PlacementProposal> ProposeAsync(PlacementRequest request, ModelAccess access, CancellationToken ct)
        {
            Request = request;
            Proposal = await inner.ProposeAsync(request, access, ct);
            return Proposal;
        }
    }

    static object Summary(string mode, int run, PlacementRequest request, PlacementProposal p, double usd)
    {
        var parents = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (alias, parent) in p.Parents) parents[alias] = parent;
        return new
        {
            mode, run, usd, calls = p.LogicalCalls, failures = p.Failures,
            parents = request.Ordered.Select(h => new { h.Alias, h.SourceId, h.Text, parent = parents.GetValueOrDefault(h.Alias),
                basis = p.Basis.GetValueOrDefault(h.Alias) }),
        };
    }

    /// <summary>
    /// split-run &lt;output&gt; &lt;target docx&gt; [&lt;source docx&gt;...]: one extraction per file with DocxSplitNumberedGroups on, saving diagnostics;
    /// when sources are supplied, also build a gather plan.
    /// </summary>
    public static async Task<int> SplitRunAsync(string[] args)
    {
        if (args.Length < 3) throw new ArgumentException("split-run <output-directory> <target docx> [<source docx> ...]");
        var output = Path.GetFullPath(args[1]);
        if (Directory.Exists(output)) throw new IOException("OUTPUT_DIRECTORY_ALREADY_EXISTS");
        var provider = OpenRouterProvider.FromEnvironment();
        if (!provider.HasKey) throw new InvalidOperationException("OPENROUTER_API_KEY_MISSING");
        Directory.CreateDirectory(output);
        var inputs = args.Skip(2).Select(Path.GetFullPath).ToArray();
        var hashes = inputs.ToDictionary(p => p, p => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(p))));
        var outlines = new Dictionary<string, IReadOnlyList<DocxHeaderExtractor.DocumentProcessing.Output.HeadingRecord>>();
        var total = 0.0;
        var extracted = await Task.WhenAll(inputs.Select(async input =>
        {
            var wholeDocument = args[0] is "whole-run" or "whole-capture";
            var wireDir = Path.Combine(output, "wire");
            if (args[0] is "split-capture" or "whole-capture") Directory.CreateDirectory(wireDir);
            var capture = new CapturingPlacer(wholeDocument);
            // Keep a per-run cap, but allow the conservative reservations for parallel selection requests.
            var options = new PipelineOptions { DocxReuseSelection = false, DocxSplitNumberedGroups = true, DocxWholeDocumentPlacement = wholeDocument, MaxProviderCostUsd = 0.50, MaxProviderAttempts = 40 };
            var replyPath = Path.Combine(output, Path.GetFileNameWithoutExtension(input) + ".selector-replies.jsonl");
            File.WriteAllText(replyPath, "");
            var writeLock = new object();
            var selector = new DocxSelector(onModelReply: reply =>
            {
                var line = JsonSerializer.Serialize(reply, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + Environment.NewLine;
                lock (writeLock) File.AppendAllText(replyPath, line);
            }, extraction: options.Extraction, boundarySnapParagraphs: options.DocxBoundarySnapParagraphs,
                splitNumberedGroups: options.DocxSplitNumberedGroups,
                handlerFactory: args[0] is "split-capture" or "whole-capture" ? WireFactory(wireDir) : null);
            var result = await new DocumentRunner(options, new Provider(provider, capture, selector)).ExtractAsync(input);
            return (input, result, capture.Request, capture.Proposal, wholeDocument, capture.Contract);
        }));
        foreach (var (input, result, request, proposal, wholeDocument, placementContract) in extracted)
        {
            var name = Path.GetFileNameWithoutExtension(input);
            outlines[input] = result.Outline.Headings;
            total += result.ResourceBudget.ConfirmedUsd;
            var lines = request?.Evidence.Source?.Lines.ToDictionary(l => l.Alias, l => l.SourceId) ?? [];
            var grouping = request?.Evidence.Source?.AliasStates
                .SelectMany(kv => kv.Value.Provenance.Where(p => p.Stage == "grouping").Select(p => new
                {
                    alias = kv.Key, sourceId = lines.GetValueOrDefault(kv.Key), run = p.CandidateId, p.Decision, p.FailureCodes,
                })).OrderBy(x => x.alias).ToArray();
            var merged = request?.Ordered.Where(h => h.Parts.Count > 1).Select(h => new { h.SourceId, parts = h.Parts.Select(p => p.SourceId), h.Text }).ToArray();
            Save(Path.Combine(output, name + ".json"), new { input, sha256 = hashes[input], usd = result.ResourceBudget.ConfirmedUsd, resourceBudget = result.ResourceBudget,
                wholeDocumentPlacement = wholeDocument, placementContract,
                headings = result.Outline.Headings.Select(h => new { h.StableId, h.Level, h.Text, h.HierarchyResolution }),
                grouping, mergedHeadings = merged, placement = result.HeadingPipeline?.Audit?.PlacementExecution });
            // Diagnostic output includes source text, packets and model responses. Keep in the private run directory, never commit it.
            Save(Path.Combine(output, name + ".diagnostics.json"), new
            {
                selectionAliasStates = request?.Evidence.Source?.AliasStates ?? result.HeadingPipeline?.Audit?.HeadingAliasStates,
                placementRequest = request,
                placementProposal = proposal,
                placementCaptured = proposal is not null,
            });
            var split = grouping?.Count(g => g.FailureCodes.Contains("GROUP_SPLIT_SEPARATELY_NUMBERED")) ?? 0;
            Console.WriteLine($"{name}: {result.Outline.Headings.Count} headings, {result.Outline.Headings.Count(h => h.Level is null)} without level, " +
                $"multi-line runs {grouping?.Select(g => g.run).Distinct().Count() ?? 0}, aliases split by numbering {split}, merged headings {merged?.Length ?? 0}; USD {result.ResourceBudget.ConfirmedUsd}");
        }
        var target = inputs[0];
        var sources = inputs.Skip(1).Select(p => new GatherSourceFile(Path.GetFileNameWithoutExtension(p), p, outlines[p])).ToList();
        if (sources.Count > 0)
        {
            var plan = GatherByHeading.Build(target, outlines[target], sources, addMissingHeadings: true);
            Save(Path.Combine(output, "plan-summary.json"), new
            {
                uncertain = plan.Uncertain.Select(u => new { u.Kind, u.SourceHeading, u.Source, facts = u.Facts.Count }),
                receiving = plan.Sections.Where(s => s.Run.Facts.Count > 0).Select(s => new { s.Heading, facts = s.Run.Facts.Count }),
                newHeadings = plan.Sections.Count(s => s.NewHeadingDepth is not null),
            });
            Console.WriteLine($"gather into {Path.GetFileName(target)}: uncertain {plan.Uncertain.Count}, receiving sections {plan.Sections.Count(s => s.Run.Facts.Count > 0)}, " +
                $"new headings {plan.Sections.Count(s => s.NewHeadingDepth is not null)}");
        }
        foreach (var p in inputs)
            if (Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(p))) != hashes[p]) throw new InvalidDataException("SOURCE_CHANGED");
        if (args[0] is "split-capture" or "whole-capture")
        {
            // Verify the evidence: every index line names files whose bytes hash to the recorded request hash; no key in any file.
            var wireDir = Path.Combine(output, "wire");
            var index = File.ReadAllLines(Path.Combine(wireDir, "wire-index.jsonl")).Where(l => l.Length > 0).Select(l => JsonDocument.Parse(l).RootElement).ToList();
            var verified = 0;
            foreach (var entry in index)
            {
                var req = File.ReadAllBytes(Path.Combine(wireDir, entry.GetProperty("requestFile").GetString()!));
                var resp = File.ReadAllBytes(Path.Combine(wireDir, entry.GetProperty("responseFile").GetString()!));
                if (Convert.ToHexStringLower(SHA256.HashData(req)) == entry.GetProperty("requestSha256").GetString() && resp.Length == entry.GetProperty("responseBytes").GetInt32()) verified++;
            }
            var key = Environment.GetEnvironmentVariable(OpenRouterProvider.ApiKeyVariable);
            var keyFound = !string.IsNullOrEmpty(key) && Directory.GetFiles(output, "*", SearchOption.AllDirectories).Any(f => File.ReadAllText(f).Contains(key, StringComparison.Ordinal));
            Console.WriteLine($"wire captured: {index.Count} calls; verified {verified}/{index.Count}; stages {string.Join(",", index.Select(e => e.GetProperty("stage").GetString()).GroupBy(s => s).Select(g => g.Key + "=" + g.Count()))}; key found in outputs: {keyFound}");
            if (verified != index.Count || keyFound) throw new InvalidDataException("WIRE_CAPTURE_VERIFICATION_FAILED");
        }
        Console.WriteLine($"Total confirmed USD {total}");
        return 0;
    }

    static Func<HttpMessageHandler> WireFactory(string dir) => () => new WireCapture(dir);

    /// <summary>
    /// Qualification capture only: records the exact body sent to the provider and the raw reply of every SELECT and GROUP call,
    /// each as a file named by sequence and request hash, then an index line. Headers (which carry the key) are never written.
    /// </summary>
    sealed class WireCapture(string dir) : DelegatingHandler(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        static readonly object Gate = new();
        static int sequence;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            var bytes = Encoding.UTF8.GetBytes(body);
            var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
            int n;
            lock (Gate) n = ++sequence;
            var stem = Path.Combine(dir, $"{n:0000}-{sha[..12]}");
            File.WriteAllBytes(stem + ".request.json", bytes);
            var stage = StageOf(body);
            var response = await base.SendAsync(request, ct);
            var raw = await response.Content.ReadAsByteArrayAsync(ct);
            File.WriteAllBytes(stem + ".response.txt", raw);
            var copy = new ByteArrayContent(raw);
            foreach (var h in response.Content.Headers) copy.Headers.TryAddWithoutValidation(h.Key, h.Value);
            response.Content = copy;
            var line = JsonSerializer.Serialize(new { seq = n, stage, requestSha256 = sha, requestBytes = bytes.Length,
                requestFile = Path.GetFileName(stem + ".request.json"), responseFile = Path.GetFileName(stem + ".response.txt"),
                responseBytes = raw.Length, httpStatus = (int)response.StatusCode, finishedAt = DateTime.UtcNow });
            lock (Gate) File.AppendAllText(Path.Combine(dir, "wire-index.jsonl"), line + Environment.NewLine, Encoding.UTF8);
            return response;
        }

        static string StageOf(string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var content = doc.RootElement.GetProperty("messages")[1].GetProperty("content");
                var text = content.ValueKind == JsonValueKind.Array ? content[0].GetProperty("text").GetString() : content.GetString();
                using var payload = JsonDocument.Parse(text!);
                return payload.RootElement.TryGetProperty("stage", out var s) ? s.GetString() ?? "UNKNOWN" : "UNKNOWN";
            }
            catch (Exception) { return "UNPARSED"; }
        }
    }

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 3) throw new ArgumentException("place-ab <output-directory> <docx> [<docx> ...]");
        var output = Path.GetFullPath(args[1]);
        if (Directory.Exists(output)) throw new IOException("OUTPUT_DIRECTORY_ALREADY_EXISTS");
        var provider = OpenRouterProvider.FromEnvironment();
        if (!provider.HasKey) throw new InvalidOperationException("OPENROUTER_API_KEY_MISSING");
        var key = provider.CreateRoute().ApiKey!;
        Directory.CreateDirectory(output);
        var total = 0.0;
        foreach (var input in args.Skip(2).Select(Path.GetFullPath))
        {
            var name = Path.GetFileNameWithoutExtension(input);
            var dir = Path.Combine(output, name);
            Directory.CreateDirectory(dir);
            var sha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(input)));
            var capture = new CapturingPlacer();
            var options = new PipelineOptions { DocxReuseSelection = false, MaxProviderCostUsd = 0.05, MaxProviderAttempts = 40 };
            var result = await new DocumentRunner(options, new Provider(provider, capture)).ExtractAsync(input);
            var request = capture.Request ?? throw new InvalidOperationException("PLACE_NOT_CALLED");
            var extractionUsd = result.ResourceBudget.ConfirmedUsd;
            total += extractionUsd;
            Save(Path.Combine(dir, "placement-input.json"), request);
            Save(Path.Combine(dir, "split-1.json"), Summary("split", 1, request, capture.Proposal!, 0));
            Console.WriteLine($"{name}: {request.Ordered.Count} headings; extraction USD {extractionUsd}");

            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            var access = new ModelAccess(key, () => deadline.Token.ThrowIfCancellationRequested());
            foreach (var (mode, whole, from, to) in new[] { ("split", false, 2, 3), ("whole", true, 1, 3) })
                for (var i = from; i <= to; i++)
                {
                    var budget = new RunResourceBudget(0.05, 20);
                    PlacementProposal proposal;
                    using (budget.Enter())
                        proposal = await new HeadingPlacer(wholeDocument: whole, repairParentChains: false).ProposeAsync(request, access, deadline.Token);
                    var usd = budget.Observe().ConfirmedUsd;
                    total += usd;
                    Save(Path.Combine(dir, $"{mode}-{i}.json"), Summary(mode, i, request, proposal, usd));
                    Console.WriteLine($"  {mode}-{i}: calls={proposal.LogicalCalls} failures={proposal.Failures.Count} USD {usd}");
                }
            if (Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(input))) != sha) throw new InvalidDataException("SOURCE_CHANGED");
        }
        Console.WriteLine($"Total confirmed USD {total}");
        return 0;
    }
}
