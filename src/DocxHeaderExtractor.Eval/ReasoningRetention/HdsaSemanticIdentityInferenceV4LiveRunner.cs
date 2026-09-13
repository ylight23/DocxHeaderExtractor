using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>
/// One deliberately narrow live diagnostic for the historical S0014/S0015 identity split.
/// It freezes a Gold-free source/request manifest before the single identity call and never
/// opens structural Gold. This is a diagnostic, not a tuning benchmark.
/// </summary>
public static class HdsaSemanticIdentityInferenceV4LiveRunner
{
    private const string Model = "qwen/qwen3.7-flash";
    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";
    private const string DocumentId = "DOC-0205";
    private const string SourcePath = "eval/a99-closed-loop/source-fidelity-audit/DOC-0205/converted-docx/025_ND_47-2020_Chia_se_du_lieu_so.docx";
    private const string OutputRoot = "eval/a99-closed-loop/hdsa-semantic-identity-inference-v4-live/DOC-0205/S0014-S0015";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private const string SystemPrompt = """
You are an A99 semantic identity relation reasoner. Decide only the relation between the two
source-owned occurrences in the request. Use their exact source text, normalized text, order,
local context, parser-owned style/layout evidence, and source boundaries as evidence. Do not
infer a parent, hierarchy, level, or semantic node ID. Do not merge occurrences yourself.
Return exactly the requested JSON object. Choose SAME_SEMANTIC_REPEAT when both occurrences are
the same semantic heading repeated, CONTINUATION_OF when the right occurrence continues the left
logical heading, DISTINCT when they are separate semantic headings, and UNRESOLVED when evidence
is insufficient. Never invent an occurrence ID or add fields.
""";

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(output);
        var sourcePath = Path.Combine(repoRoot, SourcePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("HDSA_IDENTITY_SOURCE_MISSING", sourcePath);

        var input = LoadInput(sourcePath);
        var request = HdsaSemanticNodeResolverV4.CreateRequest(input, "S0014", "S0015");
        var requestJson = HdsaSemanticNodeResolverV4.SerializeRequest(request);
        var requestHash = HdsaSemanticNodeResolverV4.RequestHash(request);
        var manifest = HdsaLiveRunManifestGuard.Prepare(
            "hdsa-identity-v4-doc0205-s0014-s0015",
            input.SourceSha256,
            "IDENTITY_PAIR_SCOPE:" + request.Pair.PairId,
            HdsaSemanticNodeResolverV4.Version,
            "hdsa_semantic_identity_v4",
            new Dictionary<string, string>(StringComparer.Ordinal) { [request.Pair.PairId] = requestJson });
        await File.WriteAllTextAsync(Path.Combine(output, "manifest.v1.json"), HdsaLiveRunManifestGuard.Serialize(manifest), ct);
        await WriteJsonAsync(Path.Combine(output, "request.v1.json"), new
        {
            schemaVersion = "a99-hdsa-semantic-identity-v4-live-request-v1",
            documentId = DocumentId,
            sourceSha256 = input.SourceSha256,
            preprocessingSnapshotHash = input.PreprocessingSnapshotHash,
            pairId = request.Pair.PairId,
            request = request,
            requestSha256 = requestHash,
            goldReadBeforeFreeze = false,
            goldDerivedInput = false,
            legacyHierarchyConsumed = false,
        }, ct);

        var verified = HdsaLiveRunManifestGuard.Verify(
            manifest,
            input.SourceSha256,
            "IDENTITY_PAIR_SCOPE:" + request.Pair.PairId,
            HdsaSemanticNodeResolverV4.Version,
            "hdsa_semantic_identity_v4",
            new Dictionary<string, string>(StringComparer.Ordinal) { [request.Pair.PairId] = requestJson });
        if (!verified.Accepted)
        {
            await WriteJsonAsync(Path.Combine(output, "summary.v1.json"), new
            {
                schemaVersion = "a99-hdsa-semantic-identity-v4-live-summary-v1",
                status = "ABORTED_BEFORE_INFERENCE",
                reason = verified.RejectionReason,
                modelCalls = 0,
                providerCalls = 0,
                runAbortedBeforeInference = true,
                goldReadBeforeFreeze = false,
            }, ct);
            Console.WriteLine("HDSA_IDENTITY_V4_STATUS=ABORTED_BEFORE_INFERENCE");
            Console.WriteLine("MODEL_CALLS=0");
            Console.WriteLine("PROVIDER_CALLS=0");
            return 1;
        }

        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            await WriteJsonAsync(Path.Combine(output, "summary.v1.json"), new
            {
                schemaVersion = "a99-hdsa-semantic-identity-v4-live-summary-v1",
                status = "BLOCKED",
                reason = "OPENROUTER_API_KEY_MISSING",
                modelCalls = 0,
                providerCalls = 0,
                goldReadBeforeFreeze = false,
            }, ct);
            return 1;
        }

        using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        using var lease = await A99OpenRouterLiveProviderLease.AcquireAsync(repoRoot, "hdsa-identity-v4", DocumentId, ct);
        var options = new RemoteInferenceOptions
        {
            Endpoint = new Uri(Endpoint),
            Model = Model,
            ApiKey = key,
            ContextSize = 1_000_000,
            MaxOutputTokens = 8_000,
            RequestTimeoutSeconds = 600,
            TransientRequestRetries = 0,
            MaxParallelRequests = 1,
            SendChatTemplateKwargs = false,
            OpenRouterAllowNonZdrPublicBenchmark = true,
        };
        var capability = (await OpenRouterModelCapabilityResolver.ResolveAsync(options, http, ct)).Capability;
        if (capability is null || !string.Equals(capability.ModelId, Model, StringComparison.Ordinal) ||
            !capability.ReasoningSupported || !capability.StructuredOutputSupported)
        {
            await WriteJsonAsync(Path.Combine(output, "summary.v1.json"), new
            {
                schemaVersion = "a99-hdsa-semantic-identity-v4-live-summary-v1",
                status = "BLOCKED",
                reason = "MODEL_CAPABILITY_MISMATCH",
                capability,
                modelCalls = 0,
                providerCalls = 0,
                goldReadBeforeFreeze = false,
            }, ct);
            return 1;
        }

        using var model = new OpenRouterCeilingReasoningModel(options, capability, http);
        var userPrompt = $"TASK=A99_SEMANTIC_IDENTITY_INFERENCE_V4\n{requestJson}\nReturn exactly the requested JSON object.";
        var stopwatch = Stopwatch.StartNew();
        string content;
        RequestPacketTelemetry telemetry;
        try
        {
            (content, telemetry) = await model.CompleteRawStructuredSemanticAsync(
                DocumentId, "HDSA_SEMANTIC_IDENTITY_V4", "hdsa-identity-v4:S0014-S0015",
                requestJson, requestJson.Length, 2, 2, SystemPrompt, userPrompt,
                HdsaSemanticNodeResolverV4.Schema(), "hdsa_semantic_identity_v4", ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or ReasoningCompletionException)
        {
            await WriteJsonAsync(Path.Combine(output, "summary.v1.json"), new
            {
                schemaVersion = "a99-hdsa-semantic-identity-v4-live-summary-v1",
                status = "BLOCKED",
                reason = "PROVIDER_FAILURE:" + ex.GetType().Name,
                providerFailure = ex.Message,
                modelCalls = model.ProviderCalls,
                providerCalls = model.ProviderCalls,
                goldReadBeforeFreeze = false,
            }, ct);
            Console.WriteLine("HDSA_IDENTITY_V4_STATUS=BLOCKED");
            Console.WriteLine($"MODEL_CALLS={model.ProviderCalls}");
            Console.WriteLine($"PROVIDER_CALLS={model.ProviderCalls}");
            return 1;
        }

        var response = HdsaSemanticNodeResolverV4.Parse(content);
        var observation = new HdsaSemanticIdentityInferenceObservation(
            request,
            response,
            requestHash,
            Sha256Text(content),
            "MODEL",
            Model,
            telemetry.ProviderRoute,
            false,
            false);
        var validation = HdsaSemanticNodeResolverV4.Validate(request, observation);
        if (!validation.Accepted)
            throw new InvalidDataException("HDSA_IDENTITY_V4_RESPONSE_REJECTED:" + validation.RejectionReason);
        var resolution = HdsaSemanticNodeResolverV4.Resolve(input, [observation]);

        var predictionPath = Path.Combine(output, "prediction.v1.json");
        await WriteJsonAsync(predictionPath, new
        {
            schemaVersion = "a99-hdsa-semantic-identity-v4-live-prediction-v1",
            documentId = DocumentId,
            requestSha256 = requestHash,
            responseSha256 = observation.ResponseHash,
            rawResponse = content,
            response,
            validation,
            resolution,
            goldReadBeforeFreeze = false,
            goldDerivedInput = false,
            legacyHierarchyConsumed = false,
        }, ct);
        var predictionSha = Sha256File(predictionPath);
        await WriteJsonAsync(Path.Combine(output, "freeze.v1.json"), new
        {
            schemaVersion = "a99-hdsa-semantic-identity-v4-live-freeze-v1",
            documentId = DocumentId,
            sourceSha256 = input.SourceSha256,
            preprocessingSnapshotHash = input.PreprocessingSnapshotHash,
            pairId = request.Pair.PairId,
            requestSha256 = requestHash,
            predictionSha256 = predictionSha,
            responseSha256 = observation.ResponseHash,
            provider = telemetry.ProviderRoute,
            model = Model,
            finishReason = telemetry.FinishReason,
            inputTokens = telemetry.ReportedInputTokens,
            reasoningTokens = telemetry.ReportedReasoningTokens,
            outputTokens = telemetry.ReportedOutputTokens,
            elapsedMs = stopwatch.ElapsedMilliseconds,
            modelCalls = model.ProviderCalls,
            providerCalls = model.ProviderCalls,
            goldReadBeforeFreeze = false,
            frozenBeforeGold = true,
        }, ct);

        await WriteJsonAsync(Path.Combine(output, "summary.v1.json"), new
        {
            schemaVersion = "a99-hdsa-semantic-identity-v4-live-summary-v1",
            status = "COMPLETE",
            documentId = DocumentId,
            pair = new { left = "S0014", right = "S0015", pairId = request.Pair.PairId },
            decision = response.Decision.ToString(),
            decisionJson = content,
            resolution.Predictions,
            acceptedRelations = resolution.AcceptedRelations,
            relationRecords = resolution.RelationRecords,
            modelCalls = model.ProviderCalls,
            providerCalls = model.ProviderCalls,
            goldReadBeforeFreeze = false,
            structuralGoldRead = false,
            diagnosticOnly = true,
        }, ct);
        Console.WriteLine("HDSA_IDENTITY_V4_STATUS=COMPLETE");
        Console.WriteLine($"DECISION={response.Decision}");
        Console.WriteLine($"MODEL_CALLS={model.ProviderCalls}");
        Console.WriteLine($"PROVIDER_CALLS={model.ProviderCalls}");
        Console.WriteLine("GOLD_READ_BEFORE_FREEZE=0");
        Console.WriteLine($"ARTIFACT={Path.Combine(OutputRoot, "summary.v1.json")}");
        return 0;
    }

    private static HdsaSemanticNodeResolutionInput LoadInput(string sourcePath)
    {
        var source = new OpenXmlDocumentSource().Read(sourcePath) with { DocumentId = DocumentId };
        var aliases = source.Paragraphs.Where(item => !string.IsNullOrWhiteSpace(item.Text))
            .Select((item, index) => new
            {
                Alias = $"S{index + 1:0000}",
                Item = item,
            })
            .ToDictionary(item => item.Alias, item => item.Item, StringComparer.Ordinal);
        var selected = new[] { "S0014", "S0015" }.Select(alias =>
        {
            if (!aliases.TryGetValue(alias, out var item)) throw new InvalidDataException("IDENTITY_ALIAS_MISSING:" + alias);
            return new HdsaSemanticNodeSourceOccurrence(
                alias,
                item.SourceOrdinal,
                item.Text,
                $"{item.Style.StyleName ?? item.Style.StyleId};font={item.Style.FontSizePt};bold={item.Style.Bold};italic={item.Style.Italic};alignment={item.Style.Alignment}",
                $"section={item.Layout.SectionIndex};tableDepth={item.Layout.TableDepth};keepNext={item.Layout.KeepNext};pageBreakBefore={item.Layout.PageBreakBefore};lineBreaks={item.LineBreakOffsets.Count}",
                null);
        }).ToArray();
        var sourceSha = Sha256File(sourcePath);
        var snapshotPayload = JsonSerializer.Serialize(selected, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var snapshotHash = Sha256Text(snapshotPayload);
        return new HdsaSemanticNodeResolutionInput(sourceSha, snapshotHash, selected, false);
    }

    private static async Task WriteJsonAsync(string path, object value, CancellationToken ct) =>
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions), ct);

    private static string Sha256Text(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string Sha256File(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}
