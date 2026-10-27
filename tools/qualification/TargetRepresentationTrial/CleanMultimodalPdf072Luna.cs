using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Agents;
using DocxHeaderExtractor.Agents.Budget;
using DocxHeaderExtractor.Agents.Transport;
using DocxHeaderExtractor.Contracts.Agents;
using DocxHeaderExtractor.Contracts.Provider;
using DocxHeaderExtractor.DocumentProcessing.Harness;
using DocxHeaderExtractor.DocumentProcessing.Intake;
using DocxHeaderExtractor.DocumentProcessing.Output;
using DocxHeaderExtractor.HeadingAgent.Formats;
using DocxHeaderExtractor.HeadingAgent.Placement;
using DocxHeaderExtractor.HeadingAgent.Protocol;
using DocxHeaderExtractor.HeadingAgent.Selectors;
using DocxHeaderExtractor.Sources.Scan;

/// <summary>Test-only multimodal SELECT adapter around the public PdfHandler route.</summary>
internal static class CleanMultimodalPdf072Luna
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    internal static async Task Run(string pdfPath, string output)
    {
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new InvalidOperationException("REFUSE_OVERWRITING_TRIAL");
        Directory.CreateDirectory(output);
        var instruction = PdfMembershipInstructionV12.Instruction;
        var source = AgentSource.Load(pdfPath, out var failure, instruction: instruction, sourceBound: true, rasterGeometry: true)
            ?? throw new InvalidOperationException(failure);
        var pageByAlias = source.Lines.ToDictionary(l => l.Alias, l => l.Page ?? throw new InvalidDataException("ALIAS_PAGE_MISSING"), StringComparer.Ordinal);
        var rasterPages = PdfScanPageRenderer.Render(pdfPath);
        var imageDir = Path.Combine(output, "rendered-pages"); Directory.CreateDirectory(imageDir);
        foreach (var p in rasterPages) File.WriteAllBytes(Path.Combine(imageDir, $"page-{p.Page:000}.png"), p.Bytes);
        Save(Path.Combine(output, "image-manifest.json"), new
        {
            renderer = PdfScanPageRenderer.CacheBasis, pageCount = rasterPages.Count,
            pages = rasterPages.Select(p => new { page = p.Page, mimeType = "image/png", p.Width, p.Height, byteLength = p.Bytes.Length, p.Sha256,
                path = Path.Combine("rendered-pages", $"page-{p.Page:000}.png") })
        });
        var bridge = new LunaSseBridge(output, pageByAlias, rasterPages.ToDictionary(p => p.Page));
        var selectorReplies = new ConcurrentQueue<ModelReply>();
        var selector = new PdfSelector(handlerFactory: () => new LunaHttpHandler(bridge), onModelReply: selectorReplies.Enqueue,
            sourceBound: true, clarifyMembership: true, membershipInstructionVersion: 12,
            separateGroupingInstruction: true, pairwiseGrouping: true, generalBoundaryPrompt: true, rasterGeometry: true);
        var placer = new HeadingPlacer(request => bridge.Turn(request, "HIERARCHY_PLACE"), wholeDocument: false, repairParentChains: true);
        var agents = new HeadingAgents(Pdf: selector, Placer: placer);
        var options = new PipelineOptions
        {
            SourceCacheRoot = Path.Combine(output, "source-cache"), PdfSourceBoundSelection = true, PdfRasterGeometryEvidence = true,
            PdfClarifyMembership = true, PdfMembershipInstructionVersion = 12, PdfSeparateGroupingInstruction = true,
            PdfPairwiseGrouping = true, PdfRepairParentChains = true, WholeDocumentPlacement = false, RepairParentChains = false,
        };
        var provider = new LunaRouteProvider();
        var file = UploadedFile.FromLocalPath(pdfPath);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        RunResult run;
        try { run = await new PdfHandler(options, provider, agents).ExtractAsync(file); }
        catch (Exception ex)
        {
            bridge.SaveLog(); Save(Path.Combine(output, "bridge-error.json"), new { type = ex.GetType().FullName, ex.Message, stack = ex.StackTrace }); throw;
        }
        timer.Stop(); bridge.SaveLog(); Save(Path.Combine(output, "native-run-result.json"), run);
        Save(Path.Combine(output, "run-identity.json"), new
        {
            mode = "public-PdfHandler-ExtractAsync-clean-multimodal-SELECT-adapter",
            pdfPath, sourceSha256 = file.Sha256, handler = "PdfHandler.ExtractAsync",
            productionContracts = new { selectionInstruction = "PdfMembershipInstructionV12.Instruction (unchanged)", grouping = "existing production pairwise GROUP, text-only", placement = "existing production PLACE, text-only" },
            testAdapter = "before HTTP transport, converts only SELECT user message string into text + image_url blocks for each page represented by aliases in that request",
            chain = "PdfHandler -> PdfPipeline -> PdfLane -> LaneHarness -> SelectionAuthority -> StructureAssembler -> ParentResolver",
            selector = new { selector.SelectorId, membershipInstructionVersion = 12, sourceBound = true, rasterGeometry = true, separateGroupingInstruction = true, pairwiseGrouping = true,
                parallelism = AgentSelector.DefaultParallelism, retryPolicy = AgentModel.ProductionRateLimitRetries },
            placer = new { placer.Contract, batchSize = 16, repairParentChains = true }, goldInRequests = false,
            providerModel = "qwen/qwen3.7-flash (Luna-only fresh context; responses authored manually by isolated evaluator)",
            durationSeconds = timer.Elapsed.TotalSeconds, calls = bridge.Observations,
            headingProviderCalls = run.HeadingPipeline?.Audit?.HeadingProviderCalls, headings = run.Outline.Headings.Count, sourceCount = run.Outline.SourceCount
        });
        Save(Path.Combine(output, "selector-replies.json"), selectorReplies.ToArray());
        Console.WriteLine($"CLEAN_MULTIMODAL_PDF072_COMPLETE duration={timer.Elapsed.TotalSeconds:0.0}s calls={bridge.Observations.Count} headings={run.Outline.Headings.Count}");
    }

    static void Save(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Json));
    sealed class LunaRouteProvider : IModelProvider { public ModelRoute CreateRoute() => new("qwen/qwen3.7-flash", "LOCAL_LUNA_IN_SESSION_ONLY"); }

    sealed class LunaHttpHandler(LunaSseBridge bridge) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Post || request.RequestUri?.AbsoluteUri != "https://openrouter.ai/api/v1/chat/completions") throw new HttpRequestException("UNEXPECTED_MODEL_ENDPOINT");
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var turn = bridge.Turn(body, null, cancellationToken); var sse = bridge.ToSse(turn);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") };
        }
    }

    sealed class LunaSseBridge(string output, IReadOnlyDictionary<string, int> pageByAlias, IReadOnlyDictionary<int, RenderedPdfPage> rasterPages)
    {
        readonly object gate = new(); readonly List<CallObservation> calls = [];
        readonly string reqDir = Path.Combine(output, "luna-requests"), replyDir = Path.Combine(output, "luna-responses"), resDir = Path.Combine(output, "luna-sse-responses");
        public IReadOnlyList<CallObservation> Observations { get { lock (gate) return calls.ToArray(); } }
        public ModelTurn Turn(PreparedRequest request, string? phase) => Turn(request.Body, phase, CancellationToken.None);

        public ModelTurn Turn(string body, string? explicitPhase, CancellationToken ct)
        {
            Directory.CreateDirectory(reqDir); Directory.CreateDirectory(replyDir); Directory.CreateDirectory(resDir);
            var originalBytes = Encoding.UTF8.GetBytes(body); var originalSha = Convert.ToHexStringLower(SHA256.HashData(originalBytes));
            var parsed = JsonNode.Parse(body)!.AsObject(); var phase = explicitPhase ?? PhaseOf(parsed);
            var includedPages = phase == "SELECT" ? AddImages(parsed) : [];
            body = parsed.ToJsonString(); var bytes = Encoding.UTF8.GetBytes(body); var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var requestPath = Path.Combine(reqDir, sha + ".request.json"); var responsePath = Path.Combine(replyDir, sha + ".response.json");
            File.WriteAllBytes(requestPath, bytes);
            Save(Path.Combine(output, "wire-audit", sha + ".json"), new { phase, sha256OfOriginalTextOnlyRequest = originalSha, sha256OfFinalProviderWirePacket = sha,
                finalWirePacketPath = Path.GetRelativePath(output, requestPath), responsePath = Path.GetRelativePath(output, responsePath),
                requestBytes = bytes.Length, imagePages = includedPages.Select(p => new { page = p.Page, mimeType = "image/png", p.Sha256, byteLength = p.Bytes.Length,
                    wireReference = "messages[1].content[type=image_url]" }).ToArray(), imageBlocksInFinalWirePacket = includedPages.Count });
            var began = DateTimeOffset.UtcNow; Console.WriteLine($"LUNA_REQUEST_READY phase={phase} sha256={sha} images={includedPages.Count} response={responsePath}");
            var stable = 0; long prior = -1;
            while (stable < 2)
            {
                ct.ThrowIfCancellationRequested(); if (!File.Exists(responsePath)) { Thread.Sleep(200); continue; }
                var length = new FileInfo(responsePath).Length; stable = length > 0 && length == prior ? stable + 1 : 0; prior = length; Thread.Sleep(200);
            }
            using var doc = JsonDocument.Parse(File.ReadAllText(responsePath)); var root = doc.RootElement;
            if (root.GetProperty("requestSha256").GetString() != sha) throw new InvalidDataException("LUNA_REPLY_FINAL_WIRE_SHA_MISMATCH");
            var toolCalls = root.TryGetProperty("toolCalls", out var c) ? c.EnumerateArray().Select(call => new ToolCall(call.GetProperty("id").GetString(), call.GetProperty("name").GetString(), call.GetProperty("arguments").GetString() ?? "")).ToArray() : [];
            var content = root.TryGetProperty("content", out var text) ? (text.ValueKind == JsonValueKind.String ? text.GetString() : text.ToString()) : null;
            var turn = new ModelTurn(content, toolCalls, root.TryGetProperty("finishReason", out var fr) ? fr.GetString() : "stop");
            lock (gate) calls.Add(new(sha, phase, began, DateTimeOffset.UtcNow, requestPath, responsePath, originalSha, bytes.Length, includedPages.Select(p => p.Page).ToArray()));
            File.WriteAllText(Path.Combine(resDir, sha + ".sse"), ToSse(turn)); return turn;
        }

        List<RenderedPdfPage> AddImages(JsonObject request)
        {
            var pages = new SortedSet<int>();
            void Visit(JsonNode? n)
            {
                if (n is JsonObject obj) foreach (var kv in obj) Visit(kv.Value);
                else if (n is JsonArray arr) foreach (var item in arr) Visit(item);
                else if (n is JsonValue v && v.TryGetValue<string>(out var s) && pageByAlias.TryGetValue(s, out var page)) pages.Add(page);
            }
            var user = request["messages"]![1]!.AsObject();
            var content = user["content"] as JsonArray ?? throw new InvalidDataException("SELECT_USER_CONTENT_NOT_ARRAY");
            var originalText = content.OfType<JsonObject>().Select(b => b["text"]?.GetValue<string>()).FirstOrDefault(t => t is not null)
                ?? throw new InvalidDataException("SELECT_USER_TEXT_MISSING");
            // The production text block is the serialized unchanged payload. Resolve its aliases only to choose source page images.
            Visit(JsonNode.Parse(originalText));
            var attached = new List<RenderedPdfPage>();
            foreach (var page in pages)
            {
                if (!rasterPages.TryGetValue(page, out var raster)) throw new InvalidDataException("PAGE_IMAGE_MISSING:" + page);
                content.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64," + Convert.ToBase64String(raster.Bytes), ["detail"] = "high" } });
                attached.Add(raster);
            }
            return attached;
        }

        public string ToSse(ModelTurn turn)
        {
            var delta = new JsonObject(); if (turn.Content is not null) delta["content"] = turn.Content;
            if (turn.ToolCalls.Count > 0) delta["tool_calls"] = new JsonArray(turn.ToolCalls.Select((call, i) => (JsonNode)new JsonObject { ["index"] = i, ["id"] = call.Id, ["type"] = "function", ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = call.Arguments } }).ToArray());
            var finish = turn.ToolCalls.Count > 0 ? "tool_calls" : turn.FinishReason ?? "stop";
            return "data: " + new JsonObject { ["id"] = "luna-local-" + Guid.NewGuid().ToString("N"), ["provider"] = "Alibaba", ["model"] = "qwen/qwen3.7-flash",
                ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = delta, ["finish_reason"] = finish, ["native_finish_reason"] = finish }),
                ["usage"] = new JsonObject { ["prompt_tokens"] = 1, ["completion_tokens"] = 1, ["completion_tokens_details"] = new JsonObject { ["reasoning_tokens"] = 0 }, ["prompt_tokens_details"] = new JsonObject { ["cached_tokens"] = 0 }, ["cost"] = 0.0 } }.ToJsonString() + "\n\ndata: [DONE]\n\n";
        }

        static string PhaseOf(JsonObject request)
        {
            var messages = request["messages"]!.AsArray(); var system = messages[0]! ["content"]!.ToString(); var user = messages[1]! ["content"]!.ToString();
            var text = system + "\n" + user;
            if (text.Contains("HEADING_PARENT_PROPOSAL", StringComparison.Ordinal)) return "HIERARCHY_PLACE";
            if (text.Contains("pairwise", StringComparison.OrdinalIgnoreCase) || text.Contains("join", StringComparison.OrdinalIgnoreCase) && text.Contains("split", StringComparison.OrdinalIgnoreCase)) return "GROUP";
            return "SELECT";
        }

        public void SaveLog() => Save(Path.Combine(output, "bridge-calls.json"), new { calls = Observations });
        static void Save(string path, object data) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, JsonSerializer.Serialize(data, Json)); }
    }
    sealed record CallObservation(string RequestSha256, string Phase, DateTimeOffset StartedUtc, DateTimeOffset CompletedUtc, string RequestPath, string ResponsePath,
        string OriginalTextOnlyRequestSha256, int FinalWireBytes, int[] ImagePages);
}
