using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Features;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Policy;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>One clean text-only Flash measurement using the structure-preserving source IR.
/// Gold is loaded only after prediction, result, and freeze hashes have been verified.</summary>
public static class OpenRouterQwen37StructurePreservingRunner
{
    private const string Model = "qwen/qwen3.7-flash";
    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";
    private const string OutputRoot = "eval/a99-closed-loop/structure-preserving-ir";
    private const string InventoryPath = "eval/a99-dataset/document-inventory.v1.json";
    private const string DocumentId = "DOC-0205";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Runs only the conditional visual branch against the already frozen text result;
    /// it never reruns the text-only provider call.</summary>
    public static async Task<int> RunVisualOnlyAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        var docDir = Path.Combine(output, DocumentId);
        var item = ReadInventory(Path.Combine(repoRoot, InventoryPath)).Single(x => x.DocumentId == DocumentId);
        var sourcePath = Path.Combine(repoRoot, item.SourcePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(sourcePath) || !string.Equals(Sha256File(sourcePath), item.SourceSha256, StringComparison.OrdinalIgnoreCase))
            return await BlockedAsync(output, "SOURCE_HASH_MISMATCH", ct);
        var source = new OpenXmlDocumentSource().Read(sourcePath) with { DocumentId = DocumentId };
        var ir = StructurePreservingSourceIrBuilder.Build(source);
        var packet = StructurePreservingSourceIrBuilder.BuildPacket(ir);
        var features = NumberingStyleFeatures.FromSourceDocument(source);
        var derived = new DocumentFeatureDeriver().Derive(source);
        var policy = DocxPolicyStateBuilder.Build(source, features, derived, new PipelineOptions { DisableLlm = false }.Extraction);
        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) return await BlockedAsync(output, "OPENROUTER_API_KEY_MISSING", ct);
        using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = Timeout.InfiniteTimeSpan };
        using var lease = await A99OpenRouterLiveProviderLease.AcquireAsync(repoRoot, OutputRoot, DocumentId, ct);
        var options = new RemoteInferenceOptions
        {
            Endpoint = new Uri(Endpoint), Model = Model, ApiKey = key, ContextSize = 1_000_000,
            MaxOutputTokens = 48_000, RequestTimeoutSeconds = 600, TransientRequestRetries = 0,
            MaxParallelRequests = 1, SendChatTemplateKwargs = false, OpenRouterAllowNonZdrPublicBenchmark = true,
        };
        var capability = (await OpenRouterModelCapabilityResolver.ResolveAsync(options, http, ct)).Capability;
        if (capability is null || !string.Equals(capability.ModelId, Model, StringComparison.Ordinal) ||
            !capability.ReasoningSupported || !capability.StructuredOutputSupported)
            return await BlockedAsync(output, "MODEL_CAPABILITY_MISMATCH", ct);
        using var model = new OpenRouterCeilingReasoningModel(options, capability, http);
        var visual = await RunVisualDiagnosticAsync(repoRoot, docDir, source, ir, packet, item, policy, model, ct);
        using var textScore = JsonDocument.Parse(File.ReadAllText(Path.Combine(docDir, "text-ceiling.score.v1.json")));
        await WriteJsonAsync(Path.Combine(output, "comparison.v1.json"), new
        {
            schemaVersion = "a99-structure-preserving-ir-comparison-v1", documentId = DocumentId,
            control = new { model = Model, mode = "FLAT_TEXT_FROZEN", tp = 0, fp = 73, fn = 71, f1 = 0d },
            text = textScore.RootElement.Clone(), visual, classification = "STRUCTURE_PRESERVING_IR_NO_MATERIAL_GAIN",
            providerCalls = model.ProviderCalls, goldReadBeforeFreeze = false,
        }, ct);
        await WriteJsonAsync(Path.Combine(output, "summary.v1.json"), new
        {
            schemaVersion = "a99-structure-preserving-ir-summary-v1", documentId = DocumentId,
            text = textScore.RootElement.Clone(), visual, classification = "STRUCTURE_PRESERVING_IR_NO_MATERIAL_GAIN",
            providerCalls = model.ProviderCalls, goldReadBeforeFreeze = false,
        }, ct);
        return 0;
    }

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        var docDir = Path.Combine(output, DocumentId);
        Directory.CreateDirectory(docDir);
        var inventory = ReadInventory(Path.Combine(repoRoot, InventoryPath));
        var item = inventory.Single(x => x.DocumentId == DocumentId);
        var sourcePath = Path.Combine(repoRoot, item.SourcePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));
        if (!File.Exists(sourcePath) || !string.Equals(Sha256File(sourcePath), item.SourceSha256, StringComparison.OrdinalIgnoreCase))
            return await BlockedAsync(output, "SOURCE_HASH_MISMATCH", ct);

        var source = new OpenXmlDocumentSource().Read(sourcePath) with { DocumentId = DocumentId };
        var ir = StructurePreservingSourceIrBuilder.Build(source);
        var packet = StructurePreservingSourceIrBuilder.BuildPacket(ir);
        var manifest = new
        {
            schemaVersion = "a99-structure-preserving-ir-manifest-v1",
            documentId = DocumentId, model = Model, sourceSha256 = item.SourceSha256,
            canonicalTextSha256 = ir.CanonicalTextSha256, sourceOccurrenceCount = ir.Occurrences.Count,
            lineCount = ir.Lines.Count, atomCount = ir.Occurrences.Sum(x => x.Atoms.Count),
            atomKinds = ir.Occurrences.SelectMany(x => x.Atoms).GroupBy(x => x.Kind, StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal),
            sourceTextCharacters = packet.SourceTextCharacters, packetCharacters = packet.PacketCharacters,
            allSourceTextVisible = packet.SourceTextCharacters == ir.Occurrences.Sum(x => x.CanonicalText.Length),
            aliases = new { format = "L000001", semanticMeaning = "NONE", stable = true },
            goldReadBeforeFreeze = false, providerCalls = 0, createdUtc = DateTimeOffset.UtcNow,
        };
        await WriteJsonAsync(Path.Combine(docDir, "ir-manifest.v1.json"), manifest, ct);

        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) return await BlockedAsync(output, "OPENROUTER_API_KEY_MISSING", ct);
        using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = Timeout.InfiniteTimeSpan };
        using var lease = await A99OpenRouterLiveProviderLease.AcquireAsync(repoRoot, OutputRoot, DocumentId, ct);
        var options = new RemoteInferenceOptions
        {
            Endpoint = new Uri(Endpoint), Model = Model, ApiKey = key, ContextSize = 1_000_000,
            MaxOutputTokens = 48_000, RequestTimeoutSeconds = 600, TransientRequestRetries = 0,
            MaxParallelRequests = 1, SendChatTemplateKwargs = false, OpenRouterAllowNonZdrPublicBenchmark = true,
        };
        var capability = (await OpenRouterModelCapabilityResolver.ResolveAsync(options, http, ct)).Capability;
        if (capability is null || !string.Equals(capability.ModelId, Model, StringComparison.Ordinal) ||
            !capability.ReasoningSupported || !capability.StructuredOutputSupported)
            return await BlockedAsync(output, "MODEL_CAPABILITY_MISMATCH", ct);

        using var model = new OpenRouterCeilingReasoningModel(options, capability, http);
        var promptHash = Sha256Text(StructurePreservingSemanticPrompt.ProtocolVersion + "\n" + StructurePreservingSemanticPrompt.System);
        var schemaHash = Sha256Text(JsonSerializer.Serialize(StructurePreservingSemanticPrompt.Schema()));
        var packetHash = Sha256Text(packet.SerializedJson);
        var configurationSignature = Sha256Text($"A99_STRUCTURE_PRESERVING_IR|{Model}|{promptHash}|{schemaHash}|{packetHash}|temperature=0|max_completion={model.SemanticMaxCompletionTokens}");
        var requestId = $"STRUCTURE_PRESERVING_TEXT:{DocumentId}:{configurationSignature}";
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var (response, _) = await model.CompleteStructurePreservingSemanticAsync(DocumentId,
                ReasoningRoute.ModelCapabilityCeiling.ToString(), requestId, packet.SerializedJson,
                packet.SourceTextCharacters, ir.Occurrences.Count, ir.Occurrences.Count, ct);
            var bound = StructurePreservingLineBinder.Bind(response.Headings, packet);
            var proposals = bound.Select(item => new ReasoningHeadingProposal
            {
                SourceId = item.SourceId, HeadingSpan = new StructuralSpan(item.Start, item.End),
                Text = source.Paragraphs.Single(p => p.SourceId == item.SourceId).Text[item.Start..item.End],
                SemanticRole = item.Role, Confidence = 1,
            }).ToArray();
            var features = NumberingStyleFeatures.FromSourceDocument(source);
            var derived = new DocumentFeatureDeriver().Derive(source);
            var policy = DocxPolicyStateBuilder.Build(source, features, derived, new PipelineOptions { DisableLlm = false }.Extraction);
            var materialized = ReasoningProposalMaterializer.Materialize(source, policy, proposals);
            var finalElements = ReasoningTaskProjection.ProjectContentHeadings(materialized.Structure)
                .OrderBy(x => x.Sources.Single().SourceOrdinal).ThenBy(x => x.Sources.Single().Span.Start).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray();
            var prediction = new
            {
                schemaVersion = "a99-structure-preserving-ir-prediction-v1", documentId = DocumentId, model = Model,
                sourceSha256 = item.SourceSha256, canonicalTextSha256 = ir.CanonicalTextSha256,
                packetHash, promptHash, schemaHash, semanticProtocolVersion = StructurePreservingSemanticPrompt.ProtocolVersion,
                rawProposalCount = response.Headings.Count, boundProposalCount = bound.Count,
                invalidOrUnboundCount = response.Headings.Count - bound.Count, proposals, headings = finalElements,
                goldReadBeforeFreeze = false,
            };
            var result = new { schemaVersion = "a99-structure-preserving-ir-result-v1", documentId = DocumentId, model = Model, headings = finalElements, goldReadBeforeFreeze = false };
            var predictionPath = Path.Combine(docDir, "text-ceiling.prediction.v1.json");
            var resultPath = Path.Combine(docDir, "text-ceiling.result.v1.json");
            await WriteJsonAsync(predictionPath, prediction, ct);
            await WriteJsonAsync(resultPath, result, ct);
            var predictionHash = Sha256File(predictionPath);
            var resultHash = Sha256File(resultPath);
            var telemetry = model.Telemetry.Where(x => x.DocumentId == DocumentId).ToArray();
            var freeze = new
            {
                schemaVersion = "a99-structure-preserving-ir-freeze-v1", documentId = DocumentId, model = Model,
                actualProvider = telemetry.Select(x => x.ProviderRoute).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "NOT_EXPOSED",
                sourceSha256 = item.SourceSha256, canonicalTextSha256 = ir.CanonicalTextSha256, packetHash, promptHash, schemaHash,
                configurationSignature, predictionSha256 = predictionHash, resultSha256 = resultHash,
                finishReason = telemetry.LastOrDefault()?.FinishReason, providerAttempts = model.ProviderCalls,
                inputTokens = telemetry.Sum(x => x.ReportedInputTokens ?? 0), reasoningTokens = telemetry.Sum(x => x.ReportedReasoningTokens ?? 0),
                outputTokens = telemetry.Sum(x => x.ReportedOutputTokens ?? 0), goldReadBeforeFreeze = false, frozenUtc = DateTimeOffset.UtcNow,
            };
            await WriteJsonAsync(Path.Combine(docDir, "text-ceiling.freeze.v1.json"), freeze, ct);
            if (Sha256File(predictionPath) != predictionHash || Sha256File(resultPath) != resultHash)
                throw new InvalidDataException("STRUCTURE_IR_FREEZE_HASH_VERIFICATION_FAILED");

            // Gold firewall: this is the first point at which the committed evaluator is read.
            var goldPath = Path.Combine(repoRoot, "eval", "a99-closed-loop", "strict-gold-occurrence-v1", $"{DocumentId}.occurrence-gold-v1.json");
            var gold = ReasoningGoldArtifactLoader.LoadOccurrence(goldPath);
            var goldKeys = gold.Where(x => x.HeadingSpan is not null).Select(x => Key(x.SourceId, x.HeadingSpan!)).ToHashSet(StringComparer.Ordinal);
            var predictedKeys = finalElements.Select(x => Key(x.Sources.Single().SourceId, x.Sources.Single().Span)).ToHashSet(StringComparer.Ordinal);
            var tp = goldKeys.Intersect(predictedKeys).Count(); var fp = predictedKeys.Except(goldKeys).Count(); var fn = goldKeys.Except(predictedKeys).Count();
            var precision = tp + fp == 0 ? 0d : (double)tp / (tp + fp); var recall = tp + fn == 0 ? 0d : (double)tp / (tp + fn);
            var f1 = precision + recall == 0 ? 0d : 2 * precision * recall / (precision + recall);
            var rawKeys = proposals.Select(x => Key(x.SourceId, x.HeadingSpan)).ToHashSet(StringComparer.Ordinal);
            var modelOmission = goldKeys.Count(key => !predictedKeys.Contains(key) && !rawKeys.Contains(key));
            var addressLoss = goldKeys.Count(key => !predictedKeys.Contains(key) && rawKeys.Contains(key));
            var score = new { schemaVersion = "a99-structure-preserving-ir-score-v1", documentId = DocumentId, exactStatus = "EVALUABLE", goldCount = goldKeys.Count, tp, fp, fn, precision, recall, f1, modelOmission, addressLoss, systemLoss = 0, goldReadBeforeFreeze = false };
            await WriteJsonAsync(Path.Combine(docDir, "text-ceiling.score.v1.json"), score, ct);
            await WriteJsonAsync(Path.Combine(docDir, "text-ceiling.first-loss.v1.json"), new { documentId = DocumentId, modelOmission, addressLoss, systemLoss = 0, goldReadBeforeFreeze = false }, ct);
            stopwatch.Stop();
            var summary = new
            {
                schemaVersion = "a99-structure-preserving-ir-summary-v1", documentId = DocumentId,
                control = new { model = Model, mode = "FLAT_TEXT_FROZEN", tp = 0, fp = 73, fn = 71, f1 = 0d },
                text = new { mode = "STRUCTURE_PRESERVING_TEXT", tp, fp, fn, precision, recall, f1, modelOmission, addressLoss, systemLoss = 0, wallTimeMs = stopwatch.ElapsedMilliseconds },
                classification = f1 >= 0.20 ? "STRUCTURE_PRESERVING_IR_RECOVERS_TASK" : addressLoss > 0 ? "STRUCTURE_PRESERVING_IR_IMPROVES_BINDING_ONLY" : "STRUCTURE_PRESERVING_IR_NO_MATERIAL_GAIN",
                providerCalls = model.ProviderCalls, telemetry, goldReadBeforeFreeze = false,
            };
            object? visual = null;
            if (f1 < 0.20)
            {
                Console.WriteLine("TEXT_REMAINS_WEAK=TRUE; STARTING_SAME_IR_VLM_DIAGNOSTIC");
                visual = await RunVisualDiagnosticAsync(repoRoot, docDir, source, ir, packet, item, policy, model, ct);
            }
            var finalSummary = new
            {
                schemaVersion = "a99-structure-preserving-ir-summary-v1", documentId = DocumentId,
                control = new { model = Model, mode = "FLAT_TEXT_FROZEN", tp = 0, fp = 73, fn = 71, f1 = 0d },
                text = new { mode = "STRUCTURE_PRESERVING_TEXT", tp, fp, fn, precision, recall, f1, modelOmission, addressLoss, systemLoss = 0, wallTimeMs = stopwatch.ElapsedMilliseconds },
                finalTable = new[]
                {
                    new { mode = "FLAT_TEXT_FROZEN", raw = 0, bound = 0, final = 0, tp = 0, fp = 73, fn = 71, f1 = 0d },
                    new { mode = "STRUCTURE_PRESERVING_TEXT", raw = response.Headings.Count, bound = bound.Count, final = finalElements.Length, tp, fp, fn, f1 },
                },
                visual, classification = f1 >= 0.20 ? "STRUCTURE_PRESERVING_IR_RECOVERS_TASK" : "STRUCTURE_PRESERVING_IR_NO_MATERIAL_GAIN",
                providerCalls = model.ProviderCalls, telemetry = model.Telemetry.Where(x => x.DocumentId == DocumentId).ToArray(), goldReadBeforeFreeze = false,
            };
            await WriteJsonAsync(Path.Combine(output, "comparison.v1.json"), finalSummary, ct);
            await WriteJsonAsync(Path.Combine(output, "summary.v1.json"), finalSummary, ct);
            Console.WriteLine($"STRUCTURE_IR_DOC0205=TP:{tp} FP:{fp} FN:{fn} F1:{f1:0.######} raw:{response.Headings.Count} bound:{bound.Count}");
            Console.WriteLine($"FINAL_CLASSIFICATION={finalSummary.classification}");
            return 0;
        }
        catch (Exception ex) when (ex is ReasoningCompletionException or HttpRequestException or InvalidDataException or FormatException or JsonException)
        {
            stopwatch.Stop();
            var telemetry = model.Telemetry.Where(x => x.DocumentId == DocumentId).ToArray();
            await WriteJsonAsync(Path.Combine(docDir, "text-ceiling.execution.v1.json"), new { status = "EXECUTION_BLOCKED", error = ex.Message, providerCalls = model.ProviderCalls, telemetry, goldReadBeforeFreeze = false }, ct);
            await WriteJsonAsync(Path.Combine(output, "summary.v1.json"), new { status = "EXECUTION_BLOCKED", reason = ex.Message, model = Model, goldReadBeforeFreeze = false }, ct);
            Console.Error.WriteLine($"STRUCTURE_IR_FAILURE={ex.Message}");
            return 1;
        }
    }

    private static string Key(string sourceId, StructuralSpan span) => $"{sourceId}:{span.Start}:{span.End}";

    private static async Task<object> RunVisualDiagnosticAsync(string repoRoot, string docDir, SourceDocument source,
        StructurePreservingSourceIr ir, StructurePreservingPacketResult packet, InventoryItem item, DocxPolicyState policy,
        OpenRouterCeilingReasoningModel model, CancellationToken ct)
    {
        var visualRoot = Path.Combine(repoRoot, "eval", "a99-closed-loop", "qwen37-flash-visual-ceiling", "DOC-0205", "visual-v2");
        var manifestPath = Path.Combine(visualRoot, "page-manifest.v2.json");
        if (!File.Exists(manifestPath)) return new { status = "NOT_RUN", reason = "FROZEN_VISUAL_MANIFEST_MISSING" };
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var pages = new List<VisualPageEvidence>();
        foreach (var row in manifest.RootElement.GetProperty("pages").EnumerateArray())
        {
            var pageIndex = row.GetProperty("pageIndex").GetInt32();
            var fileName = row.GetProperty("fileName").GetString()!;
            var path = Path.Combine(visualRoot, "pages", fileName);
            if (!File.Exists(path)) return new { status = "BLOCKED", reason = "FROZEN_VISUAL_PAGE_MISSING", pageIndex };
            pages.Add(new VisualPageEvidence(pageIndex, row.GetProperty("imageHash").GetString()!, File.ReadAllBytes(path)));
        }
        var requestId = $"STRUCTURE_PRESERVING_VISUAL:{DocumentId}:{Sha256Text(packet.SerializedJson + string.Join('|', pages.Select(x => x.ImageHash)))}";
        var stopwatch = Stopwatch.StartNew();
        var (response, _) = await model.CompleteStructurePreservingVisualSemanticAsync(DocumentId,
            ReasoningRoute.ModelCapabilityCeiling.ToString(), requestId, packet.SerializedJson, pages,
            packet.SourceTextCharacters, ir.Occurrences.Count, ir.Occurrences.Count, ct);
        var bound = StructurePreservingLineBinder.Bind(response.Headings, packet);
        var sourceById = source.Paragraphs.ToDictionary(x => x.SourceId, StringComparer.Ordinal);
        var proposals = bound.Select(x => new ReasoningHeadingProposal
        {
            SourceId = x.SourceId, HeadingSpan = new StructuralSpan(x.Start, x.End),
            Text = sourceById[x.SourceId].Text[x.Start..x.End], SemanticRole = x.Role, Confidence = 1,
        }).ToArray();
        var materialized = ReasoningProposalMaterializer.Materialize(source, policy, proposals);
        var finalElements = ReasoningTaskProjection.ProjectContentHeadings(materialized.Structure)
            .OrderBy(x => x.Sources.Single().SourceOrdinal).ThenBy(x => x.Sources.Single().Span.Start).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray();
        var predictionPath = Path.Combine(docDir, "vlm-ceiling.prediction.v1.json");
        var resultPath = Path.Combine(docDir, "vlm-ceiling.result.v1.json");
        await WriteJsonAsync(predictionPath, new
        {
            schemaVersion = "a99-structure-preserving-ir-vlm-prediction-v1", documentId = DocumentId, model = Model,
            sourceSha256 = item.SourceSha256, canonicalTextSha256 = ir.CanonicalTextSha256, packetHash = Sha256Text(packet.SerializedJson),
            pageCount = pages.Count, rawProposalCount = response.Headings.Count, boundProposalCount = bound.Count,
            invalidOrUnboundCount = response.Headings.Count - bound.Count, proposals, headings = finalElements, goldReadBeforeFreeze = false,
        }, ct);
        await WriteJsonAsync(resultPath, new { schemaVersion = "a99-structure-preserving-ir-vlm-result-v1", documentId = DocumentId, model = Model, headings = finalElements, goldReadBeforeFreeze = false }, ct);
        var predictionHash = Sha256File(predictionPath); var resultHash = Sha256File(resultPath);
        var telemetry = model.Telemetry.Where(x => x.DocumentId == DocumentId).ToArray();
        await WriteJsonAsync(Path.Combine(docDir, "vlm-ceiling.freeze.v1.json"), new
        {
            schemaVersion = "a99-structure-preserving-ir-vlm-freeze-v1", documentId = DocumentId, model = Model,
            sourceSha256 = item.SourceSha256, canonicalTextSha256 = ir.CanonicalTextSha256, packetHash = Sha256Text(packet.SerializedJson),
            pageManifestSha256 = Sha256File(manifestPath), predictionSha256 = predictionHash, resultSha256 = resultHash,
            actualProvider = telemetry.LastOrDefault()?.ProviderRoute ?? "NOT_EXPOSED", finishReason = telemetry.LastOrDefault()?.FinishReason,
            providerAttempts = model.ProviderCalls, reasoningTokens = telemetry.Sum(x => x.ReportedReasoningTokens ?? 0),
            goldReadBeforeFreeze = false, frozenUtc = DateTimeOffset.UtcNow,
        }, ct);
        var goldPath = Path.Combine(repoRoot, "eval", "a99-closed-loop", "strict-gold-occurrence-v1", $"{DocumentId}.occurrence-gold-v1.json");
        var gold = ReasoningGoldArtifactLoader.LoadOccurrence(goldPath).Where(x => x.HeadingSpan is not null).Select(x => Key(x.SourceId, x.HeadingSpan!)).ToHashSet(StringComparer.Ordinal);
        var predicted = finalElements.Select(x => Key(x.Sources.Single().SourceId, x.Sources.Single().Span)).ToHashSet(StringComparer.Ordinal);
        var tp = gold.Intersect(predicted).Count(); var fp = predicted.Except(gold).Count(); var fn = gold.Except(predicted).Count();
        var precision = tp + fp == 0 ? 0d : (double)tp / (tp + fp); var recall = tp + fn == 0 ? 0d : (double)tp / (tp + fn);
        var f1 = precision + recall == 0 ? 0d : 2 * precision * recall / (precision + recall);
        stopwatch.Stop();
        var score = new { schemaVersion = "a99-structure-preserving-ir-vlm-score-v1", documentId = DocumentId, exactStatus = "EVALUABLE", tp, fp, fn, precision, recall, f1, systemLoss = 0, goldReadBeforeFreeze = false };
        await WriteJsonAsync(Path.Combine(docDir, "vlm-ceiling.score.v1.json"), score, ct);
        await WriteJsonAsync(Path.Combine(docDir, "vlm-ceiling.first-loss.v1.json"), new { documentId = DocumentId, systemLoss = 0, goldReadBeforeFreeze = false }, ct);
        Console.WriteLine($"STRUCTURE_IR_VLM_DOC0205=TP:{tp} FP:{fp} FN:{fn} F1:{f1:0.######} raw:{response.Headings.Count} bound:{bound.Count}");
        return new { mode = "STRUCTURE_PRESERVING_VLM", tp, fp, fn, precision, recall, f1, wallTimeMs = stopwatch.ElapsedMilliseconds, raw = response.Headings.Count, bound = bound.Count, providerCalls = model.ProviderCalls };
    }
    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Sha256Text(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static InventoryItem[] ReadInventory(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("documents").EnumerateArray().Select(x => new InventoryItem(
            x.GetProperty("documentId").GetString()!, x.GetProperty("sourcePath").GetString()!, x.GetProperty("sourceSha256").GetString()!)).ToArray();
    }
    private static async Task<int> BlockedAsync(string output, string reason, CancellationToken ct)
    {
        await WriteJsonAsync(Path.Combine(output, "summary.v1.json"), new { status = "EXECUTION_BLOCKED", reason, model = Model, goldReadBeforeFreeze = false }, ct);
        Console.Error.WriteLine($"STRUCTURE_IR_BLOCKED={reason}");
        return 1;
    }
    private static async Task WriteJsonAsync(string path, object value, CancellationToken ct) => await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, ct);
    private sealed record InventoryItem(string DocumentId, string SourcePath, string SourceSha256);
}
