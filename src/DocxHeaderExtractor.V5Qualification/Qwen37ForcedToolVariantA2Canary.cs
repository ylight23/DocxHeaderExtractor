using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.V5Qualification;

/// <summary>
/// Executes exactly ONE frozen Variant A2 (<c>tool_choice: "required"</c>) request - SRC-089
/// PACK_006 only - against qwen/qwen3.7-flash via Alibaba. Deliberately narrower than
/// <see cref="Qwen37ForcedToolCanary"/>: PACK_001 and PACK_022 are frozen in
/// <c>preflight-variantA2.json</c> but this mode never touches them - they are considered only if
/// this one call completes, per the decision tree in that preflight. Hard-pinned to exactly the
/// PACK_006 entry; a caller cannot widen this to more than one call by any argument.
/// </summary>
internal static class Qwen37ForcedToolVariantA2Canary
{
    public const string ConfirmSentinel = "yes-i-authorize-exactly-1-frozen-qwen37-forced-tool-variantA2-pack006-call";
    private const string ArtifactRoot = "artifacts/v5-qwen37-forced-tool-canary";
    private const string Model = "qwen/qwen3.7-flash";
    private const string ProviderRoutingSlug = "alibaba";
    private const string ToolName = "submit_semantic_claims";
    private const string ToolDescription = "Submit the complete source-backed semantic claim response for the current task.";
    private const string TargetPackId = "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_006";
    private const string TargetDocumentId = "SRC-089";
    private const string Src089 = "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf";

    public static async Task<int> RunAsync(string root, string[] args)
    {
        var confirm = args.FirstOrDefault(a => a.StartsWith("--confirm-qwen37-forced-tool-a2=", StringComparison.Ordinal))?[("--confirm-qwen37-forced-tool-a2=".Length)..];
        var authorized = confirm == ConfirmSentinel;

        var artifactDir = Path.Combine(root, ArtifactRoot.Replace('/', Path.DirectorySeparatorChar));
        var preflightPath = Path.Combine(artifactDir, "preflight-variantA2.json");
        if (!File.Exists(preflightPath)) return Fail($"qwen37-forced-tool-a2: no frozen preflight at {ArtifactRoot}/preflight-variantA2.json");
        var preflight = JsonNode.Parse(File.ReadAllText(preflightPath))!;
        var frozenPacks = preflight["packs"]!.AsArray();
        var frozenTarget = frozenPacks.SingleOrDefault(p => p!["packId"]!.GetValue<string>() == TargetPackId && p["documentId"]!.GetValue<string>() == TargetDocumentId)
            ?? throw new InvalidOperationException($"{TargetPackId} not found in frozen Variant A2 preflight");
        if (preflight["model"]!.GetValue<string>() != Model)
            return Fail($"qwen37-forced-tool-a2: frozen preflight names model {preflight["model"]}, expected {Model}");

        var contract = DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
        var envelope = new V5ProviderEnvelope(Model, ProviderRoutingSlug, "none", true, "json_object", 300) { UsageInclude = true };
        var structuralSchema = V5StrictClaimSchemaCompilerV1.Compile(contract, V5ClaimSchemaCarrier.ToolParameters);

        var pdfPath = Path.Combine(root, Src089.Replace('/', Path.DirectorySeparatorChar));
        var built = V5PdfPreflightBuilder.BuildV2_1(pdfPath, TargetDocumentId, contract, V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId, envelope);
        var pack = built.Requests.SingleOrDefault(request => request.PackId == TargetPackId)
            ?? throw new InvalidOperationException($"pack-not-found-on-rebuild:{TargetDocumentId}:{TargetPackId}");

        var body = V5ForcedToolProviderRequestBodyV1.BuildWithRequiredToolChoice(
            V5SystemPromptV2_1.Text, pack.Request.Prompt, pack.MaxCompletionTokens, Model, ProviderRoutingSlug, ToolName, ToolDescription, structuralSchema, "none");

        var frozenHash = frozenTarget["variantA2_providerBodyHash"]!.GetValue<string>();
        if (body.Hash != frozenHash)
            return Fail(
                $"qwen37-forced-tool-a2: rebuilt {TargetDocumentId}:{TargetPackId} Variant A2 does not reproduce the frozen preflight - " +
                $"environment parity FAILED, no call made. frozenHash={frozenHash} rebuiltHash={body.Hash}");

        var bodyFilePath = Path.Combine(artifactDir, "bodies", "SRC-089-PACK_006-variantA2-required.json");
        var frozenBodyBytes = File.ReadAllBytes(bodyFilePath);
        if (!frozenBodyBytes.AsSpan().SequenceEqual(body.PayloadBytes))
            return Fail("qwen37-forced-tool-a2: frozen body file does not match the rebuilt bytes - no call made");

        Console.WriteLine("qwen37-forced-tool-a2: environment parity PASS (the PACK_006 Variant A2 request reproduces the frozen preflight hash byte for byte)");
        Console.WriteLine($"  SRC-089 PACK_006 providerRequestHash={frozenHash}");

        if (!authorized)
        {
            Console.WriteLine();
            Console.WriteLine($"Not authorized (pass --confirm-qwen37-forced-tool-a2={ConfirmSentinel}). providerCalls=0, goldRead=false.");
            return 0;
        }
        // Hard-pinned to exactly 1: no gate exists for "exactly 1" the way V5CanaryGate pins 3, so this
        // is asserted directly rather than borrowing a gate built for a different count.
        const int exactlyOneCall = 1;
        if (exactlyOneCall != 1) throw new InvalidOperationException("qwen37-forced-tool-a2 must execute exactly one call");

        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey)) return Fail("OPENROUTER_API_KEY is not set. Refusing to run.");

        var outPath = Path.Combine(artifactDir, "canary-result-variantA2-pack006.v1.json");
        var retryNumber = 0;
        while (File.Exists(outPath))
        {
            retryNumber++;
            outPath = Path.Combine(artifactDir, $"canary-result-variantA2-pack006-retry{retryNumber}.v1.json");
        }
        if (retryNumber > 0)
            Console.WriteLine($"qwen37-forced-tool-a2: prior attempt(s) exist; this is retry {retryNumber}, same frozen body, writing to canary-result-variantA2-pack006-retry{retryNumber}.v1.json");

        var atoms = V5PdfPreflightBuilder.LoadAtoms(pdfPath);
        var scope = ClaimBindingScope.Create(pack.OwnedAliases, pack.VisibleAliases);

        string? transportError = null;
        string content = "";
        string? finishReason = null;
        IReadOnlyList<V5ToolCallDeltaFragment> fragments = [];
        JsonElement? usage = null;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var client = OpenRouterQualificationTransport.CreateOwned(RemoteInferenceOptions.FromEnvironment());
            (content, finishReason, fragments, usage) = await client.ExecuteToolCallAsync(
                body.PayloadBytes, pack.MaxCompletionTokens, V5SystemPromptV2_1.Text, pack.Request.Prompt);
        }
        catch (Exception ex)
        {
            transportError = ex.Message;
        }
        stopwatch.Stop();

        object? qualificationReport = null;
        IReadOnlyList<V5ReassembledToolCall> reassembled = [];
        if (transportError is null)
        {
            reassembled = V5ToolCallArgumentsReassembler.Reassemble(fragments);
            var qualification = V5ForcedToolQualifierV1.Qualify(reassembled, ToolName, TargetPackId, contract, atoms, scope);
            var stats = SourceSelectionStats(qualification.Response);
            qualificationReport = new
            {
                outcome = qualification.Outcome.ToString(),
                detail = qualification.Detail,
                proposalCount = qualification.Response?.Claims.Count ?? 0,
                boundCount = qualification.Binding?.Bound.Count ?? 0,
                refusalCount = qualification.Binding?.Refusals.Count ?? 0,
                refusalReasons = qualification.Binding?.Refusals.Values.Distinct().ToArray() ?? [],
                relationHasValueOrUnaryHasObjectDetected = qualification.Outcome == V5ForcedToolOutcome.TASK_CONTRACT_INVALID &&
                    (qualification.Detail?.Contains("relation-has-value", StringComparison.Ordinal) == true ||
                     qualification.Detail?.Contains("unary-claim-has-object", StringComparison.Ordinal) == true),
                aliasOnlyParts = stats.AliasOnly,
                verbatimTextParts = stats.VerbatimText,
                multiPartEndpoints = stats.MultiPartEndpoints,
                predicateDistribution = stats.PredicateDistribution,
                stateDistribution = stats.StateDistribution,
            };
        }

        var result = new
        {
            role = "same-atom-normalization-heavy",
            documentId = TargetDocumentId,
            packId = TargetPackId,
            providerRequestHash = frozenHash,
            transportError,
            finishReason,
            latencyMs = stopwatch.Elapsed.TotalMilliseconds,
            usage,
            toolCallCount = reassembled.Count,
            toolCallFragmentsRawCount = fragments.Count,
            reassembledArguments = reassembled.Count > 0 ? reassembled[0].Arguments : null,
            reassembledArgumentsSha256 = reassembled.Count > 0 ? Sha256(reassembled[0].Arguments) : null,
            qualification = qualificationReport,
        };

        var q = qualificationReport as dynamic;
        Console.WriteLine(
            $"  -> SRC-089 PACK_006: transportError={(transportError is null ? "none" : "YES")} toolCalls={reassembled.Count} " +
            $"outcome={(q?.outcome ?? "N/A")} latencyMs={stopwatch.Elapsed.TotalMilliseconds:0}");

        var artifact = new
        {
            schemaVersion = "v5-qwen37-forced-tool-canary-variantA2-pack006-result-v1",
            purpose = "MEASUREMENT_NOT_PROMOTION",
            variant = "A2_required_tool_choice",
            model = Model,
            provider = ProviderRoutingSlug,
            tool = ToolName,
            toolChoice = "required",
            retryAttempt = retryNumber,
            sameFrozenBodyAsAttempt0 = true,
            providerCalls = 1,
            goldRead = false,
            semanticRetries = 0,
            responseRepairApplied = false,
            providerExecutionAuthorized = true,
            result,
        };
        File.WriteAllText(outPath,
            JsonSerializer.Serialize(artifact, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine,
            new UTF8Encoding(false));

        Console.WriteLine();
        Console.WriteLine($"wrote {outPath}");
        return 0;
    }

    private sealed record SourceSelectionStatsResult(
        int AliasOnly, int VerbatimText, int MultiPartEndpoints,
        IReadOnlyDictionary<string, int> PredicateDistribution, IReadOnlyDictionary<string, int> StateDistribution);

    private static SourceSelectionStatsResult SourceSelectionStats(SemanticClaimResponseV2_1? response)
    {
        if (response is null) return new(0, 0, 0, new Dictionary<string, int>(), new Dictionary<string, int>());
        int aliasOnly = 0, verbatimText = 0, multiPart = 0;
        var predicates = new Dictionary<string, int>(StringComparer.Ordinal);
        var states = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var claim in response.Claims)
        {
            predicates[claim.Predicate] = predicates.GetValueOrDefault(claim.Predicate) + 1;
            states[claim.State.ToString()] = states.GetValueOrDefault(claim.State.ToString()) + 1;
            foreach (var endpoint in new[] { claim.Subject, claim.Object })
            {
                if (endpoint is null) continue;
                if (endpoint.SourceParts.Count > 1) multiPart++;
                foreach (var part in endpoint.SourceParts)
                {
                    if (part.VerbatimText is null) aliasOnly++;
                    else verbatimText++;
                }
            }
        }
        return new(aliasOnly, verbatimText, multiPart, predicates, states);
    }

    private static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }
}
