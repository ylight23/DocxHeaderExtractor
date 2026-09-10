using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Features;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Policy;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>Live DEV benchmark for the semantic-text interface. It shares the Flash ceiling
/// transport and validator/projection with the numeric control, changing only model output
/// addressing. Gold is loaded after prediction/result/freeze, never in the request path.</summary>
public static class SemanticTextExactBindingRunner
{
    private const string Model = "qwen/qwen3.7-flash";
    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";
    private const string OutputRoot = "eval/a99-closed-loop/semantic-text-exact-binding";
    private const string InventoryPath = "eval/a99-dataset/document-inventory.v1.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly string[] Documents = ["DOC-0205", "DOC-0258"];

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(output);
        var startHead = GitSha(repoRoot);
        Console.WriteLine($"START_HEAD={startHead}");
        Console.WriteLine($"BRANCH={Git(repoRoot, "branch --show-current")}");
        Console.WriteLine("MODEL_CALLS=2_EXPECTED_SEMANTIC_TEXT_ONLY");

        var inventory = JsonDocument.Parse(File.ReadAllText(Path.Combine(repoRoot, InventoryPath))).RootElement.GetProperty("documents").EnumerateArray().ToArray();
        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey)) return await Blocked(output, "OPENROUTER_API_KEY_MISSING", startHead, ct);
        using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = Timeout.InfiniteTimeSpan };
        var options = new RemoteInferenceOptions
        {
            Endpoint = new Uri(Endpoint), Model = Model, ApiKey = apiKey, ContextSize = 1_000_000,
            MaxOutputTokens = 48_000, RequestTimeoutSeconds = 600, TransientRequestRetries = 0,
            MaxParallelRequests = 1, SendChatTemplateKwargs = false, OpenRouterAllowNonZdrPublicBenchmark = true,
        };
        var capabilityResult = await OpenRouterModelCapabilityResolver.ResolveAsync(options, http, ct);
        var capability = capabilityResult.Capability;
        if (capability is null || !string.Equals(capability.ModelId, Model, StringComparison.Ordinal) || !capability.ReasoningSupported || !capability.StructuredOutputSupported)
            return await Blocked(output, "MODEL_CAPABILITY_MISMATCH", startHead, ct);
        using var lease = await A99OpenRouterLiveProviderLease.AcquireAsync(repoRoot, OutputRoot, "DOC-0205", ct);
        using var model = new OpenRouterCeilingReasoningModel(options, capability, http);
        var results = new List<DocumentMetric>();
        foreach (var documentId in Documents)
        {
            var item = inventory.Single(x => x.GetProperty("documentId").GetString() == documentId);
            results.Add(await RunDocumentAsync(repoRoot, output, item, model, ct));
        }
        var comparison = new
        {
            schemaVersion = "a99-semantic-text-exact-binding-comparison-v1", generatedUtc = DateTimeOffset.UtcNow,
            model = Model, providerCalls = model.ProviderCalls, controlContract = "C0 numeric-span frozen", testContract = SemanticTextExactBindingContract.ProtocolVersion,
            controls = Documents.Select(id => LoadControl(repoRoot, id)).ToArray(), documents = results, goldReadBeforeFreeze = false,
            finalClassification = Classify(results),
            architecture = "SOURCE FACTS → LLM semantic discovery(sourceAlias+verbatimText+role) → deterministic exact-text binder → validator → projection → hierarchy",
        };
        await WriteJson(Path.Combine(output, "comparison.v1.json"), comparison, ct);
        await WriteJson(Path.Combine(output, "summary.v1.json"), new { comparison, startHead, endHead = GitSha(repoRoot), expectedStartHead = "0bd71f9", providerCalls = model.ProviderCalls, modelCalls = model.ProviderCalls, goldReadBeforeFreeze = false }, ct);
        Console.WriteLine($"END_HEAD={GitSha(repoRoot)}");
        Console.WriteLine($"FINAL_CLASSIFICATION={comparison.finalClassification}");
        return 0;
    }

    /// <summary>Rebuilds only the comparison envelope from already-frozen semantic-text runs.
    /// This path is strictly offline and exists so presentation/diagnostic changes never rerun
    /// the provider or replace a frozen prediction.</summary>
    public static async Task<int> RunOfflineComparisonAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        var comparisonPath = Path.Combine(output, "comparison.v1.json");
        var summaryPath = Path.Combine(output, "summary.v1.json");
        var comparison = JsonNode.Parse(await File.ReadAllTextAsync(comparisonPath, ct))?.AsObject() ?? throw new InvalidDataException("SEMANTIC_TEXT_COMPARISON_MISSING");
        comparison["controls"] = JsonSerializer.SerializeToNode(Documents.Select(id => LoadControl(repoRoot, id)).ToArray(), JsonOptions);
        comparison["providerCalls"] = 0;
        comparison["goldReadBeforeFreeze"] = false;
        await File.WriteAllTextAsync(comparisonPath, comparison.ToJsonString(JsonOptions) + Environment.NewLine, ct);
        var summary = JsonNode.Parse(await File.ReadAllTextAsync(summaryPath, ct))?.AsObject() ?? new JsonObject();
        summary["comparison"] = comparison.DeepClone();
        summary["providerCalls"] = 0;
        summary["modelCalls"] = 2;
        summary["goldReadBeforeFreeze"] = false;
        await File.WriteAllTextAsync(summaryPath, summary.ToJsonString(JsonOptions) + Environment.NewLine, ct);
        Console.WriteLine("OFFLINE_COMPARISON_REBUILT=true");
        return 0;
    }

    private static async Task<DocumentMetric> RunDocumentAsync(string repoRoot, string output, JsonElement item, OpenRouterCeilingReasoningModel model, CancellationToken ct)
    {
        var documentId = item.GetProperty("documentId").GetString()!;
        var docDir = Path.Combine(output, documentId);
        Directory.CreateDirectory(docDir);
        var sourcePath = Path.Combine(repoRoot, item.GetProperty("sourcePath").GetString()!.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));
        if (!File.Exists(sourcePath) || !string.Equals(Sha256(sourcePath), item.GetProperty("sourceSha256").GetString(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SOURCE_HASH_MISMATCH:" + documentId);
        var source = new OpenXmlDocumentSource().Read(sourcePath) with { DocumentId = documentId };
        var features = NumberingStyleFeatures.FromSourceDocument(source);
        var derived = new DocumentFeatureDeriver().Derive(source);
        var policy = DocxPolicyStateBuilder.Build(source, features, derived, new PipelineOptions { DisableLlm = false }.Extraction);
        var sourceRows = source.Paragraphs.Where(p => !string.IsNullOrWhiteSpace(p.Text)).Select((p, i) => new SemanticTextSourceAlias($"S{i + 1:0000}", p.SourceId, p.SourceOrdinal, p.Text)).ToArray();
        var packet = JsonSerializer.Serialize(new { sourceAliases = sourceRows.Select(x => new { alias = x.Alias, text = x.RawText, sourceOrdinal = x.SourceOrdinal }).ToArray() });
        var promptHash = Sha256Text(SemanticTextExactBindingContract.ProtocolVersion + "\n" + SemanticTextExactBindingContract.System);
        var schemaHash = Sha256Text(JsonSerializer.Serialize(SemanticTextExactBindingContract.Schema()));
        var packetHash = Sha256Text(packet);
        var requestId = $"{SemanticTextExactBindingContract.ProtocolVersion}:{documentId}:{packetHash}";
        var stopwatch = Stopwatch.StartNew();
        var (rawContent, telemetry) = await model.CompleteRawStructuredSemanticAsync(documentId, ReasoningRoute.ModelCapabilityCeiling.ToString(), requestId, packet, sourceRows.Sum(x => x.RawText.Length), sourceRows.Length, sourceRows.Length, SemanticTextExactBindingContract.System, SemanticTextExactBindingContract.BuildUser(packet, ReasoningRoute.ModelCapabilityCeiling.ToString()), SemanticTextExactBindingContract.Schema(), "semantic_text_exact_binding_v1", ct);
        var response = SemanticTextExactBindingContract.Parse(rawContent);
        var bound = SemanticTextExactBinder.Bind(response.Headings, sourceRows, out var observations);
        var proposals = bound.Select(x => new ReasoningHeadingProposal { SourceId = x.SourceId, HeadingSpan = new StructuralSpan(x.Start, x.End), Text = x.Text, SemanticRole = x.Role, Confidence = 1 }).ToArray();
        var materialized = ReasoningProposalMaterializer.Materialize(source, policy, proposals);
        var finalElements = ReasoningTaskProjection.ProjectContentHeadings(materialized.Structure).OrderBy(x => x.Sources.Single().SourceOrdinal).ThenBy(x => x.Sources.Single().Span.Start).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray();
        stopwatch.Stop();
        var finalRows = finalElements.Select(x => new { sourceId = x.Sources.Single().SourceId, start = x.Sources.Single().Span.Start, end = x.Sources.Single().Span.End, text = x.Text, role = x.Role }).ToArray();
        var prediction = new
        {
            schemaVersion = "a99-semantic-text-exact-binding-prediction-v1", documentId, model = Model, contract = SemanticTextExactBindingContract.ProtocolVersion,
            sourceSha256 = item.GetProperty("sourceSha256").GetString(), promptHash, schemaHash, packetHash, sourceAliasCount = sourceRows.Length,
            sourceCoverage = 1d, rawModelHeadings = response.Headings, bindingObservations = observations, boundHeadings = bound, finalHeadings = finalRows,
            validatorAccepted = materialized.Validated.Count(x => x.Accepted), validatorRejected = materialized.Validated.Count(x => !x.Accepted),
            goldReadBeforeFreeze = false,
        };
        var result = new { schemaVersion = "a99-semantic-text-exact-binding-result-v1", documentId, model = Model, contract = SemanticTextExactBindingContract.ProtocolVersion, headings = finalRows, goldReadBeforeFreeze = false };
        var predictionPath = Path.Combine(docDir, "prediction.v1.json");
        var resultPath = Path.Combine(docDir, "result.v1.json");
        await WriteJson(predictionPath, prediction, ct);
        await WriteJson(resultPath, result, ct);
        var freeze = new
        {
            schemaVersion = "a99-semantic-text-exact-binding-freeze-v1", documentId, model = Model, contract = SemanticTextExactBindingContract.ProtocolVersion,
            actualProvider = telemetry.ProviderRoute, sourceSha256 = item.GetProperty("sourceSha256").GetString(), promptHash, schemaHash, packetHash,
            predictionSha256 = Sha256(predictionPath), resultSha256 = Sha256(resultPath), finishReason = telemetry.FinishReason, reasoningTokens = telemetry.ReportedReasoningTokens,
            outputTokens = telemetry.ReportedOutputTokens, inputTokens = telemetry.ReportedInputTokens, elapsedMs = stopwatch.ElapsedMilliseconds,
            goldReadBeforeFreeze = false, frozenUtc = DateTimeOffset.UtcNow,
        };
        var freezePath = Path.Combine(docDir, "freeze.v1.json");
        await WriteJson(freezePath, freeze, ct);
        if (Sha256(predictionPath) != freeze.predictionSha256 || Sha256(resultPath) != freeze.resultSha256) throw new InvalidDataException("FREEZE_HASH_VERIFICATION_FAILED:" + documentId);

        // Gold firewall: first read occurs only after the frozen prediction/result authority exists.
        var goldPath = Path.Combine(repoRoot, "eval/a99-closed-loop/strict-gold-occurrence-v1", documentId + ".occurrence-gold-v1.json");
        var gold = ReasoningGoldArtifactLoader.LoadOccurrence(goldPath).Where(x => x.HeadingSpan is not null).ToArray();
        var goldKeys = gold.Select(x => Key(x.SourceId, x.HeadingSpan!)).ToHashSet(StringComparer.Ordinal);
        var finalKeys = finalElements.Select(x => Key(x.Sources.Single().SourceId, x.Sources.Single().Span)).ToHashSet(StringComparer.Ordinal);
        var tp = goldKeys.Intersect(finalKeys).Count(); var fp = finalKeys.Except(goldKeys).Count(); var fn = goldKeys.Except(finalKeys).Count();
        var precision = tp + fp == 0 ? 0d : (double)tp / (tp + fp); var recall = tp + fn == 0 ? 0d : (double)tp / (tp + fn); var f1 = precision + recall == 0 ? 0d : 2 * precision * recall / (precision + recall);
        var semanticPresence = gold.Count(g => bound.Any(p => p.SourceId == g.SourceId && p.Start < g.HeadingSpan!.End && g.HeadingSpan.Start < p.End));
        var exact = tp;
        var score = new { schemaVersion = "a99-semantic-text-exact-binding-score-v1", documentId, exactStatus = "EVALUABLE", goldCount = gold.Length, tp, fp, fn, precision, recall, f1, semanticCorrespondence = semanticPresence, trueOmission = gold.Length - semanticPresence, wrongBoundary = semanticPresence - exact, modelOmission = gold.Length - semanticPresence, modelWrongBoundaryText = semanticPresence - exact, modelFalsePositive = fp, textNotFound = observations.Count(x => x.Status == SemanticTextBindingStatus.TEXT_NOT_FOUND), ambiguousExactText = observations.Count(x => x.Status == SemanticTextBindingStatus.AMBIGUOUS_EXACT_TEXT), invalidSourceAlias = observations.Count(x => x.Status == SemanticTextBindingStatus.INVALID_SOURCE_ALIAS), systemBindingLoss = 0, systemValidatorLoss = materialized.Validated.Count(x => !x.Accepted), systemProjectionLoss = Math.Max(0, materialized.Validated.Count(x => x.Accepted) - finalElements.Length), goldReadBeforeFreeze = false };
        await WriteJson(Path.Combine(docDir, "score.v1.json"), score, ct);
        await WriteJson(Path.Combine(docDir, "first-loss.v1.json"), new { documentId, textNotFound = score.textNotFound, ambiguousExactText = score.ambiguousExactText, systemBindingLoss = score.systemBindingLoss, systemValidatorLoss = score.systemValidatorLoss, systemProjectionLoss = score.systemProjectionLoss, goldReadBeforeFreeze = false }, ct);
        return new DocumentMetric(documentId, score, new { raw = response.Headings.Count, bound = bound.Count, final = finalElements.Length, sourceAliases = sourceRows.Length, packetCharacters = packet.Length, outputTokens = telemetry.ReportedOutputTokens, reasoningTokens = telemetry.ReportedReasoningTokens, wallTimeMs = stopwatch.ElapsedMilliseconds, finishReason = telemetry.FinishReason, actualProvider = telemetry.ProviderRoute });
    }

    private static string Classify(IReadOnlyList<DocumentMetric> results)
    {
        var d = results.SingleOrDefault(x => x.DocumentId == "DOC-0205");
        if (d is null) return "SEMANTIC_TEXT_BINDING_NO_GAIN";
        var exactGain = d.Score.tp - 1;
        if (d.Score.systemBindingLoss > 0 || d.Score.systemValidatorLoss > 0 || d.Score.systemProjectionLoss > 0) return "SEMANTIC_TEXT_BINDING_SYSTEM_LOSS";
        if (exactGain >= 20 && d.Score.wrongBoundary < 10) return "SEMANTIC_TEXT_BINDING_RECOVERS_EXACT_ACCURACY";
        if (exactGain > 0) return "SEMANTIC_TEXT_BINDING_PARTIAL_GAIN";
        return "SEMANTIC_TEXT_BINDING_NO_GAIN";
    }

    private static object LoadControl(string repoRoot, string documentId)
    {
        var relative = documentId == "DOC-0205"
            ? "structure-preserving-ir/DOC-0205"
            : "qwen37-flash-reasoning-ceiling/DOC-0258/r1-ceiling";
        var predictionName = documentId == "DOC-0205" ? "text-ceiling.prediction.v1.json" : "prediction.v1.json";
        var scoreName = documentId == "DOC-0205" ? "text-ceiling.score.v1.json" : "score.v1.json";
        var dir = Path.Combine(repoRoot, "eval/a99-closed-loop", relative.Replace('/', Path.DirectorySeparatorChar));
        var score = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, scoreName))).RootElement;
        var prediction = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, predictionName))).RootElement;
        var sourcePath = Path.Combine(repoRoot, InventoryPath);
        var inventory = JsonDocument.Parse(File.ReadAllText(sourcePath)).RootElement.GetProperty("documents").EnumerateArray().Single(x => x.GetProperty("documentId").GetString() == documentId);
        var goldPath = Path.Combine(repoRoot, "eval/a99-closed-loop/strict-gold-occurrence-v1", documentId + ".occurrence-gold-v1.json");
        var gold = ReasoningGoldArtifactLoader.LoadOccurrence(goldPath).Where(x => x.HeadingSpan is not null).ToArray();
        var proposals = ReadNumericProposals(prediction);
        var presence = gold.Count(g => proposals.Any(p => p.SourceId == g.SourceId && p.Start < g.HeadingSpan!.End && g.HeadingSpan.Start < p.End));
        var tp = GetInt(score, "tp") ?? 0;
        return new
        {
            documentId, contract = "C0 numeric-span frozen", tp, fp = GetInt(score, "fp") ?? 0, fn = GetInt(score, "fn") ?? 0,
            precision = GetDouble(score, "precision") ?? 0, recall = GetDouble(score, "recall") ?? 0, f1 = GetDouble(score, "f1") ?? 0,
            semanticPresence = presence, trueOmission = gold.Length - presence, wrongBoundary = presence - tp, systemLoss = 0,
            sourceSha256 = inventory.GetProperty("sourceSha256").GetString(), goldReadBeforeFreeze = false,
        };
    }

    private static IReadOnlyList<NumericProposal> ReadNumericProposals(JsonElement root)
    {
        if (!root.TryGetProperty("proposals", out var array) || array.ValueKind != JsonValueKind.Array) return [];
        return array.EnumerateArray().Select(x =>
        {
            var span = x.GetProperty("headingSpan");
            return new NumericProposal(x.GetProperty("sourceId").GetString() ?? "", span.GetProperty("start").GetInt32(), span.GetProperty("end").GetInt32());
        }).ToArray();
    }

    private static int? GetInt(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : null;
    private static double? GetDouble(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.TryGetDouble(out var result) ? result : null;

    private static async Task<int> Blocked(string output, string reason, string head, CancellationToken ct) { await WriteJson(Path.Combine(output, "summary.v1.json"), new { status = "BLOCKED", reason, startHead = head, providerCalls = 0, modelCalls = 0, goldReadBeforeFreeze = false }, ct); return 1; }
    private static string Key(string sourceId, StructuralSpan span) => $"{sourceId}:{span.Start}:{span.End}";
    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Sha256Text(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string GitSha(string root) => Git(root, "rev-parse HEAD");
    private static string Git(string root, string args) { try { using var p = Process.Start(new ProcessStartInfo("git", args) { WorkingDirectory = root, RedirectStandardOutput = true, UseShellExecute = false }); return p?.StandardOutput.ReadToEnd().Trim() ?? "NOT_PERSISTED"; } catch { return "NOT_PERSISTED"; } }
    private static async Task WriteJson(string path, object value, CancellationToken ct) => await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, ct);
    private sealed record DocumentMetric(string DocumentId, dynamic Score, object Telemetry);
    private sealed record NumericProposal(string SourceId, int Start, int End);
}
