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
using DocxHeaderExtractor.HeadingAgent.Placement;
using DocxHeaderExtractor.HeadingAgent.Selectors;

/// <summary>Full native PdfHandler run with an offline, SHA-bound Luna response bridge.</summary>
internal static class NativePdf072Luna
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    internal static async Task Run(string pdfPath, string output, string? replayDir = null, bool adaptiveVisual = false,
        int membershipVersion = 12, int placementBatchSize = HeadingPlacer.DefaultBatchSize,
        bool optimizedRequests = false, int? parallelism = null, bool shapeReview = false, bool visualGrayscale = false)
    {
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new InvalidOperationException("REFUSE_OVERWRITING_TRIAL");
        Directory.CreateDirectory(output);
        var bridge = new LunaSseBridge(output, replayDir);
        var selectorReplies = new ConcurrentQueue<ModelReply>();
        var selector = new PdfSelector(handlerFactory: () => new LunaHttpHandler(bridge), onModelReply: selectorReplies.Enqueue, parallelism: parallelism,
            sourceBound: true, clarifyMembership: true, membershipInstructionVersion: membershipVersion,
            separateGroupingInstruction: true, pairwiseGrouping: true, generalBoundaryPrompt: true, rasterGeometry: true,
            adaptiveVisualSelection: adaptiveVisual,
            resourcePolicy: adaptiveVisual ? new RequestPolicy { MaxRequestBytes = 786432 } : null,
            compactGeometry: optimizedRequests, visualMaxWidth: optimizedRequests ? 960 : 1200,
            visualQuality: optimizedRequests ? 50 : 58, visualGrayscale: visualGrayscale,
            dynamicCompletionBudget: optimizedRequests, shapeReview: shapeReview);
        var placer = new HeadingPlacer(request => bridge.Turn(request, "HIERARCHY_PLACE"), wholeDocument: false,
            repairParentChains: true, batchSize: placementBatchSize);
        var agents = new HeadingAgents(Pdf: selector, Placer: placer);
        var options = new PipelineOptions
        {
            SourceCacheRoot = Path.Combine(output, "source-cache"),
            PdfSourceBoundSelection = true,
            PdfRasterGeometryEvidence = true,
            PdfAdaptiveVisualSelection = adaptiveVisual,
            PdfVisualSelectionMaxRequestBytes = adaptiveVisual ? 786432 : null,
            PdfCompactGeometry = optimizedRequests,
            PdfVisualMaxWidth = optimizedRequests ? 960 : 1200,
            PdfVisualQuality = optimizedRequests ? 50 : 58,
            PdfVisualGrayscale = visualGrayscale,
            PdfDynamicCompletionBudget = optimizedRequests,
            PdfJobs = parallelism,
            PdfShapeReview = shapeReview,
            PdfClarifyMembership = true,
            PdfMembershipInstructionVersion = membershipVersion,
            PdfSeparateGroupingInstruction = true,
            PdfPairwiseGrouping = true,
            PdfRepairParentChains = true,
            WholeDocumentPlacement = false,
            RepairParentChains = false,
        };
        var provider = new LunaRouteProvider();
        var file = UploadedFile.FromLocalPath(pdfPath);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        RunResult run;
        try
        {
            run = await new PdfHandler(options, provider, agents).ExtractAsync(file);
        }
        catch (Exception ex)
        {
            bridge.SaveLog();
            Save(Path.Combine(output, "selector-replies.json"), selectorReplies.ToArray());
            Save(Path.Combine(output, "bridge-error.json"), new { type = ex.GetType().FullName, ex.Message, stack = ex.StackTrace });
            throw;
        }
        timer.Stop();
        bridge.SaveLog();
        Save(Path.Combine(output, "native-run-result.json"), run);
        Save(Path.Combine(output, "run-identity.json"), new
        {
            mode = $"public-PdfHandler-ExtractAsync-integrated-adaptive-visual-v{membershipVersion}-luna",
            pdfPath, sourceSha256 = file.Sha256,
            handler = "PdfHandler.ExtractAsync",
            chain = "PdfHandler -> PdfPipeline -> PdfLane -> LaneHarness -> SelectionAuthority -> StructureAssembler -> ParentResolver",
            selector = new { selector.SelectorId, membershipInstructionVersion = membershipVersion, selectionInstructionVersion = 12, sourceBound = true, rasterGeometry = true,
                adaptiveVisualSelection = adaptiveVisual, visualSelectionMaxRequestBytes = adaptiveVisual ? 786432 : (int?)null,
                separateGroupingInstruction = true, pairwiseGrouping = true, generalBoundaryPrompt = true,
                parallelism = parallelism ?? AgentSelector.DefaultParallelism,
                providerConcurrencyEnvironment = Environment.GetEnvironmentVariable("DHX_OPENROUTER_MAX_CONCURRENCY"),
                compactGeometry = optimizedRequests, imageWidth = optimizedRequests ? 960 : 1200,
                imageQuality = optimizedRequests ? 50 : 58, grayscale = visualGrayscale, dynamicCompletionBudget = optimizedRequests,
                retryPolicy = AgentModel.ProductionRateLimitRetries },
            placer = new { placer.Contract, batchSize = placementBatchSize, repairParentChains = true },
            goldInRequests = false,
            durationSeconds = timer.Elapsed.TotalSeconds,
            reusedResponseCalls = bridge.Observations.Count(c => c.Replayed),
            bridgeCalls = bridge.Observations,
            headingProviderCalls = run.HeadingPipeline?.Audit?.HeadingProviderCalls,
            headings = run.Outline.Headings.Count,
            sourceCount = run.Outline.SourceCount,
            note = "Luna authored every response body. The bridge translated only the request/response transport envelope; it did not choose phase, alter scheduling, retry, or repair model content."
        });
        Save(Path.Combine(output, "selector-replies.json"), selectorReplies.ToArray());
        Console.WriteLine($"NATIVE_PDF072_COMPLETE duration={timer.Elapsed.TotalSeconds:0.0}s calls={bridge.Observations.Count} headings={run.Outline.Headings.Count}");
    }

    /// <summary>The production PDF profile (PipelineOptions defaults; optionally cropped page images) behind the response bridge. The selector
    /// and placer are built from the options exactly as HeadingAgentSetup builds them; only the HTTP handler is the bridge.</summary>
    internal static async Task RunProduction(string pdfPath, string output, bool crop, string? replayDir = null, bool bilevel = false)
    {
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new InvalidOperationException("REFUSE_OVERWRITING_TRIAL");
        Directory.CreateDirectory(output);
        var options = new PipelineOptions { SourceCacheRoot = Path.Combine(output, "source-cache"), PdfVisualCropMargins = crop, PdfVisualBilevel = bilevel };
        var bridge = new LunaSseBridge(output, replayDir);
        var policy = DocxHeaderExtractor.App.HeadingAgentSetup.ResourcePolicyFor(options);
        if (options.PdfAdaptiveVisualSelection && options.PdfVisualSelectionMaxRequestBytes is { } cap) policy = policy with { MaxRequestBytes = cap };
        var selector = new PdfSelector(handlerFactory: () => new LunaHttpHandler(bridge), parallelism: options.PdfJobs, resourcePolicy: policy,
            inlineGrouping: options.PdfInlineGrouping, sourceBound: options.PdfSourceBoundSelection, clarifyMembership: options.PdfClarifyMembership,
            membershipInstructionVersion: options.PdfMembershipInstructionVersion, boundaryReview: options.PdfBoundaryReview,
            separateGroupingInstruction: options.PdfSeparateGroupingInstruction, pairwiseGrouping: options.PdfPairwiseGrouping,
            generalBoundaryPrompt: options.PdfMembershipInstructionVersion >= 7, rasterGeometry: options.PdfRasterGeometryEvidence,
            adaptiveVisualSelection: options.PdfAdaptiveVisualSelection, compactGeometry: options.PdfCompactGeometry,
            visualMaxWidth: options.PdfVisualMaxWidth, visualQuality: options.PdfVisualQuality, visualGrayscale: options.PdfVisualGrayscale,
            visualCropMargins: options.PdfVisualCropMargins, visualBilevel: options.PdfVisualBilevel,
            dynamicCompletionBudget: options.PdfDynamicCompletionBudget && options.PdfMembershipInstructionVersion >= 14, shapeReview: options.PdfShapeReview);
        var placer = new HeadingPlacer(request => bridge.Turn(request, "HIERARCHY_PLACE"), wholeDocument: options.WholeDocumentPlacement,
            repairParentChains: options.PdfRepairParentChains || options.RepairParentChains);
        var file = UploadedFile.FromLocalPath(pdfPath);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        RunResult run;
        try { run = await new PdfHandler(options, new LunaRouteProvider(), new HeadingAgents(Pdf: selector, Placer: placer)).ExtractAsync(file); }
        catch (Exception ex)
        {
            bridge.SaveLog();
            Save(Path.Combine(output, "bridge-error.json"), new { type = ex.GetType().FullName, ex.Message, stack = ex.StackTrace });
            throw;
        }
        timer.Stop();
        bridge.SaveLog();
        Save(Path.Combine(output, "native-run-result.json"), run);
        Save(Path.Combine(output, "run-identity.json"), new
        {
            mode = "production-bridge", pdfPath, sourceSha256 = file.Sha256, crop, selector = new { selector.SelectorId },
            placer = new { placer.Contract }, goldInRequests = false, durationSeconds = timer.Elapsed.TotalSeconds,
            bridgeCalls = bridge.Observations, headings = run.Outline.Headings.Count,
            note = "Every response body was authored by the bridge responder; the bridge only translated the transport envelope.",
        });
        Console.WriteLine($"NATIVE_PDF072_COMPLETE duration={timer.Elapsed.TotalSeconds:0.0}s calls={bridge.Observations.Count} headings={run.Outline.Headings.Count}");
    }

    static void Save(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Json));

    sealed class LunaRouteProvider : IModelProvider
    {
        public ModelRoute CreateRoute() => new("qwen/qwen3.7-flash", "LOCAL_LUNA_IN_SESSION_ONLY");
    }

    sealed class LunaHttpHandler(LunaSseBridge bridge) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Post || request.RequestUri?.AbsoluteUri != "https://openrouter.ai/api/v1/chat/completions")
                throw new HttpRequestException("UNEXPECTED_MODEL_ENDPOINT");
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var turn = bridge.Turn(body, null, cancellationToken);
            var sse = bridge.ToSse(turn);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
            };
        }
    }

    sealed class LunaSseBridge(string output, string? replayDir)
    {
        readonly object gate = new();
        readonly List<CallObservation> calls = [];
        readonly string reqDir = Path.Combine(output, "luna-requests");
        readonly string replyDir = Path.Combine(output, "luna-responses");
        readonly string resDir = Path.Combine(output, "luna-sse-responses");
        int active, peak;
        public IReadOnlyList<CallObservation> Observations { get { lock (gate) return calls.ToArray(); } }

        public ModelTurn Turn(PreparedRequest request, string? phase) => Turn(request.Body, phase, CancellationToken.None);

        public ModelTurn Turn(string body, string? explicitPhase, CancellationToken ct)
        {
            Directory.CreateDirectory(reqDir); Directory.CreateDirectory(replyDir); Directory.CreateDirectory(resDir);
            var bytes = Encoding.UTF8.GetBytes(body);
            var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var parsed = JsonNode.Parse(body)!.AsObject();
            var phase = explicitPhase ?? PhaseOf(parsed);
            var requestPath = Path.Combine(reqDir, sha + ".request.json");
            var responsePath = Path.Combine(replyDir, sha + ".response.json");
            File.WriteAllText(requestPath, body);
            var replayed = false;
            if (!File.Exists(responsePath) && replayDir is not null)
            {
                var replayPath = Path.Combine(replayDir, "luna-responses", sha + ".response.json");
                if (File.Exists(replayPath)) { File.Copy(replayPath, responsePath); replayed = true; }
            }
            var began = DateTimeOffset.UtcNow;
            var nowActive = Interlocked.Increment(ref active);
            int observedPeak;
            do { observedPeak = peak; if (nowActive <= observedPeak) break; } while (Interlocked.CompareExchange(ref peak, nowActive, observedPeak) != observedPeak);
            Console.WriteLine($"LUNA_REQUEST_READY phase={phase} sha256={sha} active={nowActive} response={responsePath}");
            try
            {
                var stable = 0; long prior = -1;
                while (stable < 2)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!File.Exists(responsePath)) { Thread.Sleep(200); continue; }
                    var length = new FileInfo(responsePath).Length;
                    stable = length > 0 && length == prior ? stable + 1 : 0;
                    prior = length;
                    Thread.Sleep(200);
                }
                using var doc = JsonDocument.Parse(File.ReadAllText(responsePath));
                var root = doc.RootElement;
                if (root.GetProperty("requestSha256").GetString() != sha) throw new InvalidDataException("LUNA_REPLY_REQUEST_SHA_MISMATCH");
                var toolCalls = root.TryGetProperty("toolCalls", out var c) ? c.EnumerateArray().Select(call => new ToolCall(
                    call.GetProperty("id").GetString(), call.GetProperty("name").GetString(), call.GetProperty("arguments").GetString() ?? "")).ToArray() : [];
                var content = root.TryGetProperty("content", out var text) ? (text.ValueKind == JsonValueKind.String ? text.GetString() : text.ToString()) : null;
                var finish = root.TryGetProperty("finishReason", out var fr) ? fr.GetString() : "stop";
                var turn = new ModelTurn(content, toolCalls, finish);
                lock (gate) calls.Add(new(sha, phase, began, DateTimeOffset.UtcNow, nowActive, observedPeak, requestPath, responsePath, replayed));
                File.WriteAllText(Path.Combine(resDir, sha + ".sse"), ToSse(turn));
                return turn;
            }
            catch (Exception ex)
            {
                Save(Path.Combine(output, sha + ".bridge-call-error.json"), new { type = ex.GetType().FullName, ex.Message, stack = ex.StackTrace });
                throw;
            }
            finally { Interlocked.Decrement(ref active); }
        }

        public string ToSse(ModelTurn turn)
        {
            var delta = new JsonObject();
            if (turn.Content is not null) delta["content"] = turn.Content;
            if (turn.ToolCalls.Count > 0) delta["tool_calls"] = new JsonArray(turn.ToolCalls.Select((call, i) => (JsonNode)new JsonObject
            {
                ["index"] = i, ["id"] = call.Id, ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = call.Arguments }
            }).ToArray());
            var finish = turn.ToolCalls.Count > 0 ? "tool_calls" : turn.FinishReason ?? "stop";
            var evt = new JsonObject
            {
                ["id"] = "luna-local-" + Guid.NewGuid().ToString("N"), ["provider"] = "Alibaba", ["model"] = "qwen/qwen3.7-flash",
                ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = delta, ["finish_reason"] = finish,
                    ["native_finish_reason"] = finish }),
                ["usage"] = new JsonObject { ["prompt_tokens"] = 1, ["completion_tokens"] = 1,
                    ["completion_tokens_details"] = new JsonObject { ["reasoning_tokens"] = 0 },
                    ["prompt_tokens_details"] = new JsonObject { ["cached_tokens"] = 0 }, ["cost"] = 0.0 }
            };
            return "data: " + evt.ToJsonString() + "\n\ndata: [DONE]\n\n";
        }

        public void SaveLog() => File.WriteAllText(Path.Combine(output, "bridge-calls.json"), JsonSerializer.Serialize(new
        {
            maxConcurrentCalls = peak,
            calls = Observations.Select(c => new { c.RequestSha256, c.Phase, c.StartedUtc, c.CompletedUtc, elapsedSeconds = (c.CompletedUtc - c.StartedUtc).TotalSeconds,
                c.ActiveCallsAtStart, c.PeakActiveCallsAtStart, c.RequestPath, c.ResponsePath, c.Replayed })
        }, Json));

        static string PhaseOf(JsonObject request)
        {
            var messages = request["messages"]!.AsArray();
            var user = messages[1]!["content"];
            var text = user is JsonArray parts ? parts.First(p => p?["type"]?.GetValue<string>() == "text")!["text"]!.GetValue<string>() : user!.GetValue<string>();
            var packet = JsonNode.Parse(text)!;
            if (packet["stage"]?.GetValue<string>() == "GROUP") return "GROUP";
            if (packet["stage"]?.GetValue<string>() == "UNIT_SEGMENT") return "SELECT";
            if (packet["protocolVersion"]?.GetValue<string>().Contains("HEADING_PARENT_PROPOSAL", StringComparison.Ordinal) == true) return "HIERARCHY_PLACE";
            return "SELECT_OR_CONVERSATION";
        }
    }

    sealed record CallObservation(string RequestSha256, string Phase, DateTimeOffset StartedUtc, DateTimeOffset CompletedUtc,
        int ActiveCallsAtStart, int PeakActiveCallsAtStart, string RequestPath, string ResponsePath, bool Replayed);
}
