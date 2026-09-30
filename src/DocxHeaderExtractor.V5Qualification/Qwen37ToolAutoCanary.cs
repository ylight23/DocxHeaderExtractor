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
/// P3: executes exactly one OpenRouter ToolAuto request for SRC-089 PACK_006. The canonical request,
/// token budget, model and Alibaba pin are rebuilt from P2's frozen preflight; only the carrier is
/// changed from json_object to one submit_semantic_claims tool with tool_choice="auto". No retry,
/// fallback, Gold access, semantic retry or response repair is possible in this entry point.
/// </summary>
internal static class Qwen37ToolAutoCanary
{
    public const string ConfirmSentinel = "yes-i-authorize-exactly-1-p3-qwen37-tool-auto-pack006-call";
    private const string ArtifactRoot = "artifacts/v5-openrouter-qwen37-tool-auto-canary";
    private const string PreflightPath = "artifacts/v5-openrouter-qwen37-carrier/tool-auto-preflight-pack006.v1.json";
    private const string Model = "qwen/qwen3.7-flash";
    private const string Provider = "alibaba";
    private const string DocumentId = "SRC-089";
    private const string PackId = "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_006";
    private const string Src089 = "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf";

    public static async Task<int> RunAsync(string root, string[] args)
    {
        var confirm = args.FirstOrDefault(a => a.StartsWith("--confirm-qwen37-tool-auto=", StringComparison.Ordinal))?["--confirm-qwen37-tool-auto=".Length..];
        var authorized = confirm == ConfirmSentinel;
        var preflightFile = Path.Combine(root, PreflightPath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(preflightFile)) return Fail($"p3-tool-auto: missing P2 preflight: {PreflightPath}");

        var contract = DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
        var envelope = new V5ProviderEnvelope(Model, Provider, "none", true, "json_object", 300) { UsageInclude = true };
        var pdfPath = Path.Combine(root, Src089.Replace('/', Path.DirectorySeparatorChar));
        var built = V5PdfPreflightBuilder.BuildV2_1(pdfPath, DocumentId, contract,
            V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId, envelope);
        var pack = built.Requests.SingleOrDefault(item => item.PackId == PackId)
            ?? throw new InvalidOperationException($"p3-tool-auto: missing rebuilt {DocumentId}:{PackId}");
        var body = OpenRouterQwen37ToolAutoCarrierV1.Build(
            pack.Request, contract, pack.MaxCompletionTokens, envelope, pack.OwnedAliases, pack.VisibleAliases);

        var preflight = JsonNode.Parse(File.ReadAllText(preflightFile))!;
        if (preflight["pack"]?.GetValue<string>() != PackId ||
            preflight["semanticRequestHash"]?.GetValue<string>() != pack.Request.RequestHash ||
            preflight["providerRequestHash"]?.GetValue<string>() != body.Hash ||
            preflight["toolName"]?.GetValue<string>() != OpenRouterQwen37ToolAutoCarrierV1.ToolName ||
            preflight["toolChoice"]?.GetValue<string>() != "auto")
            return Fail("p3-tool-auto: P2 parity mismatch; refusing to send a body that was not preflight-frozen");

        using (var document = JsonDocument.Parse(body.PayloadBytes))
        {
            var rootBody = document.RootElement;
            if (rootBody.TryGetProperty("response_format", out _) ||
                rootBody.GetProperty("tools").GetArrayLength() != 1 ||
                rootBody.GetProperty("tool_choice").GetString() != "auto" ||
                rootBody.GetProperty("model").GetString() != Model ||
                rootBody.GetProperty("provider").GetProperty("order")[0].GetString() != Provider ||
                rootBody.GetProperty("max_tokens").GetInt32() != pack.MaxCompletionTokens ||
                rootBody.GetProperty("temperature").GetInt32() != 0 ||
                rootBody.GetProperty("reasoning").GetProperty("effort").GetString() != "none")
                return Fail("p3-tool-auto: request-shape invariant failed; providerCalls=0");
        }

        Console.WriteLine($"p3-tool-auto parity PASS: {DocumentId} {PackId} semanticHash={pack.Request.RequestHash} providerHash={body.Hash} maxTokens={pack.MaxCompletionTokens}");
        var artifactDir = Path.Combine(root, ArtifactRoot.Replace('/', Path.DirectorySeparatorChar));
        var outPath = Path.Combine(artifactDir, "pack006-result.v1.json");
        if (File.Exists(outPath)) return Fail($"p3-tool-auto: result path already exists; refusing a second call: {outPath}");
        if (!authorized)
        {
            Console.WriteLine($"Not authorized (pass --confirm-qwen37-tool-auto={ConfirmSentinel}). providerCalls=0, goldRead=false.");
            return 0;
        }

        // Shell-managed secrets commonly retain the line ending from a copied value. Normalise only
        // outer whitespace before HttpClient validates the Authorization header; never log the key.
        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")?.Trim();
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Contains('\r') || apiKey.Contains('\n'))
            return Fail("p3-tool-auto: OPENROUTER_API_KEY missing or invalid; providerCalls=0");

        // This must remain zero: any retry would violate P3's single-call authorization.
        var options = RemoteInferenceOptions.FromEnvironment();
        options.ApiKey = apiKey;
        options.TransientRequestRetries = 0;
        if (options.TransientRequestRetries != 0) throw new InvalidOperationException("p3-tool-auto retry invariant failed");

        string? transportError = null;
        string content = "";
        string? finishReason = null;
        IReadOnlyList<V5ToolCallDeltaFragment> fragments = [];
        JsonElement? usage = null;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var client = OpenRouterHeaderExtractor.CreateOwned(options);
            (content, finishReason, fragments, usage) = await client.ExecuteToolCallAsync(
                body.PayloadBytes, pack.MaxCompletionTokens, V5SystemPromptV2_1.Text, pack.Request.Prompt);
        }
        catch (Exception ex)
        {
            transportError = ex.Message;
        }
        stopwatch.Stop();

        var calls = transportError is null ? V5ToolCallArgumentsReassembler.Reassemble(fragments) : [];
        var classification = "ROUTING_REJECTED";
        V5QuarantineQualification? quarantine = null;
        ClaimQuarantineResultV2_1? quarantinedClaims = null;
        if (transportError is null && calls.Count == 0)
            classification = "PLAIN_TEXT_NO_TOOL";
        else if (transportError is null &&
                 (calls.Count != 1 || !string.Equals(calls[0].FunctionName, OpenRouterQwen37ToolAutoCarrierV1.ToolName, StringComparison.Ordinal)))
            classification = "TOOL_CALL_MALFORMED";
        else if (transportError is null)
        {
            (quarantine, quarantinedClaims, _) = V5ClaimQuarantineQualifier.Qualify(
                calls[0].Arguments, finishReason, null, contract, PackId,
                V5PdfPreflightBuilder.LoadAtoms(pdfPath), ClaimBindingScope.Create(pack.OwnedAliases, pack.VisibleAliases));
            classification = !quarantine.ResponseUsable
                ? "TOOL_CALL_MALFORMED"
                : quarantine.ContractRefused > 0 || quarantine.BindingRefusedCount > 0
                    ? "TOOL_CALL_CONTRACT_PARTIAL"
                    : "TOOL_CALL_VALID";
        }

        var sourceStats = SourceSelectionStats(quarantinedClaims?.Eligible.Select(item => item.Proposal) ?? []);
        var result = new
        {
            classification,
            documentId = DocumentId,
            packId = PackId,
            canonicalSemanticRequestHash = pack.Request.RequestHash,
            providerRequestHash = body.Hash,
            maxTokens = pack.MaxCompletionTokens,
            transportError,
            finishReason,
            latencyMs = stopwatch.Elapsed.TotalMilliseconds,
            usage,
            plainAssistantText = content,
            toolCallCount = calls.Count,
            toolCallFragmentsRawCount = fragments.Count,
            toolCalls = calls.Select(call => new { call.Index, call.Id, call.FunctionName, argumentsSha256 = Sha256(call.Arguments), arguments = call.Arguments }).ToArray(),
            quarantine = quarantine?.ToReport(),
            sourceSelection = new { aliasOnly = sourceStats.AliasOnly, verbatimText = sourceStats.VerbatimText, multipart = sourceStats.Multipart },
            unsafeRepair = 0,
            outOfScopeAccepted = 0,
            baselineJsonObject = new { rawClaims = 35, contractRefused = 6, bound = 29, bindingRefused = 0 },
        };

        var artifact = new
        {
            schemaVersion = "v5-openrouter-qwen37-tool-auto-canary-result-v1",
            purpose = "P3_MEASUREMENT_NOT_PROMOTION",
            providerCalls = 1,
            goldRead = false,
            semanticRetries = 0,
            responseRepairApplied = false,
            fallbackProviderAllowed = false,
            transportRetries = 0,
            tool = OpenRouterQwen37ToolAutoCarrierV1.ToolName,
            toolChoice = "auto",
            responseFormatPresent = false,
            model = Model,
            provider = Provider,
            result,
        };
        Directory.CreateDirectory(artifactDir);
        File.WriteAllText(outPath, JsonSerializer.Serialize(artifact, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine, new UTF8Encoding(false));
        Console.WriteLine($"P3 {classification}; toolCalls={calls.Count}; wrote {outPath}");
        return 0;
    }

    private sealed record SourceStats(int AliasOnly, int VerbatimText, int Multipart);

    private static SourceStats SourceSelectionStats(IEnumerable<SemanticClaimProposalV2_1> claims)
    {
        var aliasOnly = 0;
        var verbatimText = 0;
        var multipart = 0;
        foreach (var endpoint in claims.SelectMany(claim => new[] { claim.Subject, claim.Object }).Where(endpoint => endpoint is not null))
        {
            if (endpoint!.SourceParts.Count > 1) multipart++;
            foreach (var part in endpoint.SourceParts)
                if (part.VerbatimText is null) aliasOnly++; else verbatimText++;
        }
        return new(aliasOnly, verbatimText, multipart);
    }

    private static string Sha256(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }
}
