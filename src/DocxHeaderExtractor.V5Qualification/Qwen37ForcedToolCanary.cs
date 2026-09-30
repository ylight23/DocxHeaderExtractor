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
/// Executes exactly the 3 Variant A (structural-only) forced-tool-call requests frozen by
/// <c>artifacts/v5-qwen37-forced-tool-canary/preflight.json</c> against qwen/qwen3.7-flash via
/// Alibaba. Rebuilds all three bodies in-process first and refuses to call a provider unless every
/// rebuilt hash matches what preflight froze, and unless the frozen body file on disk matches too.
/// Measures, per pack: forced tool-call compliance, relation-has-value/unary-has-object,
/// alias-only-vs-verbatimText, multipart usage, proposal/bound/refusal counts, latency and token
/// usage. Never picks its own packs, never opens Gold, never retries semantically, never repairs a
/// response - only <see cref="RemoteInferenceOptions.TransientRequestRetries"/>'s ordinary transport
/// retry applies, and if that budget is exhausted the failure is frozen as-is.
/// <list type="bullet">
/// <item><c>--qwen37-forced-tool</c> alone: provider-free plan/parity check only.</item>
/// <item><c>--qwen37-forced-tool --confirm-qwen37-forced-tool=...</c>: the only path that can call a
/// provider, and only after the parity check passes.</item>
/// </list>
/// </summary>
internal static class Qwen37ForcedToolCanary
{
    public const string ConfirmSentinel = "yes-i-authorize-exactly-3-frozen-qwen37-forced-tool-variantA-calls";
    private const string ArtifactRoot = "artifacts/v5-qwen37-forced-tool-canary";
    private const string Model = "qwen/qwen3.7-flash";
    private const string ProviderRoutingSlug = "alibaba";
    private const string ToolName = "submit_semantic_claims";
    private const string ToolDescription = "Submit the complete source-backed semantic claim response for the current task.";
    private const string Src089 = "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf";
    private const string Src095 = "todo10_8/heading_corpus_100/07_system_generated/095_RFC9114_HTTP_3.pdf";

    public static async Task<int> RunAsync(string root, string[] args)
    {
        var confirm = args.FirstOrDefault(a => a.StartsWith("--confirm-qwen37-forced-tool=", StringComparison.Ordinal))?[("--confirm-qwen37-forced-tool=".Length)..];
        var authorized = confirm == ConfirmSentinel;

        var artifactDir = Path.Combine(root, ArtifactRoot.Replace('/', Path.DirectorySeparatorChar));
        var preflightPath = Path.Combine(artifactDir, "preflight.json");
        if (!File.Exists(preflightPath)) return Fail($"qwen37-forced-tool: no frozen preflight at {ArtifactRoot}/preflight.json");
        var preflight = JsonNode.Parse(File.ReadAllText(preflightPath))!;
        var frozenPacks = preflight["packs"]!.AsArray();
        if (frozenPacks.Count != 3) return Fail($"qwen37-forced-tool: frozen preflight must have exactly 3 packs, found {frozenPacks.Count}");
        if (preflight["model"]!.GetValue<string>() != Model)
            return Fail($"qwen37-forced-tool: frozen preflight names model {preflight["model"]}, expected {Model}");

        var contract = DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
        var envelope = new V5ProviderEnvelope(Model, ProviderRoutingSlug, "none", true, "json_object", 300) { UsageInclude = true };
        var structuralSchema = V5StrictClaimSchemaCompilerV1.Compile(contract, V5ClaimSchemaCarrier.ToolParameters);

        var builtByDoc = new Dictionary<string, (V5ProviderPreflight Preflight, IReadOnlyList<V5PackedSourceRequest> Requests)>(StringComparer.Ordinal);
        (V5ProviderPreflight Preflight, IReadOnlyList<V5PackedSourceRequest> Requests) BuiltFor(string documentId)
        {
            if (!builtByDoc.TryGetValue(documentId, out var built))
            {
                var pdf = Path.Combine(root, (documentId == "SRC-089" ? Src089 : Src095).Replace('/', Path.DirectorySeparatorChar));
                built = V5PdfPreflightBuilder.BuildV2_1(pdf, documentId, contract, V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId, envelope);
                builtByDoc[documentId] = built;
            }
            return built;
        }

        var resolved = new List<(string Role, string DocumentId, string PdfPath, V5PackedSourceRequest Pack, byte[] FrozenBody, string FrozenHash)>();
        foreach (var frozen in frozenPacks)
        {
            var role = frozen!["role"]!.GetValue<string>();
            var documentId = frozen["documentId"]!.GetValue<string>();
            var packId = frozen["packId"]!.GetValue<string>();
            var built = BuiltFor(documentId);
            var pack = built.Requests.SingleOrDefault(request => request.PackId == packId)
                ?? throw new InvalidOperationException($"pack-not-found-on-rebuild:{documentId}:{packId}");

            var body = V5ForcedToolProviderRequestBodyV1.Build(
                V5SystemPromptV2_1.Text, pack.Request.Prompt, pack.MaxCompletionTokens, Model, ProviderRoutingSlug, ToolName, ToolDescription, structuralSchema, "none");

            var frozenHash = frozen["variantA_structuralOnly"]!["providerBodyHash"]!.GetValue<string>();
            if (body.Hash != frozenHash)
                return Fail(
                    $"qwen37-forced-tool: rebuilt {documentId}:{packId} Variant A does not reproduce the frozen preflight - " +
                    $"environment parity FAILED, no call made. frozenHash={frozenHash} rebuiltHash={body.Hash}");

            var suffix = packId[(packId.LastIndexOf(':') + 1)..];
            var bodyFilePath = Path.Combine(artifactDir, "bodies", $"{documentId}-{suffix}-variantA-structural.json");
            var frozenBodyBytes = File.ReadAllBytes(bodyFilePath);
            if (!frozenBodyBytes.AsSpan().SequenceEqual(body.PayloadBytes))
                return Fail($"qwen37-forced-tool: frozen body file for {documentId}:{packId} does not match the rebuilt bytes - no call made");

            var pdfPath = Path.Combine(root, (documentId == "SRC-089" ? Src089 : Src095).Replace('/', Path.DirectorySeparatorChar));
            resolved.Add((role, documentId, pdfPath, pack, frozenBodyBytes, frozenHash));
        }

        Console.WriteLine("qwen37-forced-tool: environment parity PASS (all 3 Variant A requests reproduce the frozen preflight hashes byte for byte)");
        foreach (var item in resolved)
            Console.WriteLine($"  [{item.Role}] {item.DocumentId} {item.Pack.PackId} providerRequestHash={item.FrozenHash}");

        if (!authorized)
        {
            Console.WriteLine();
            Console.WriteLine($"Not authorized (pass --confirm-qwen37-forced-tool={ConfirmSentinel}). providerCalls=0, goldRead=false.");
            return 0;
        }
        V5CanaryGate.Authorize(resolved.Count, authorized);

        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey)) return Fail("OPENROUTER_API_KEY is not set. Refusing to run.");

        var outPath = Path.Combine(artifactDir, "canary-result-variantA.v1.json");
        var retryNumber = 0;
        while (File.Exists(outPath))
        {
            retryNumber++;
            outPath = Path.Combine(artifactDir, $"canary-result-variantA-retry{retryNumber}.v1.json");
        }
        if (retryNumber > 0)
            Console.WriteLine($"qwen37-forced-tool: prior attempt(s) exist; this is retry {retryNumber}, same frozen bodies, writing to canary-result-variantA-retry{retryNumber}.v1.json");

        var results = new List<object>();
        foreach (var item in resolved)
        {
            var atoms = V5PdfPreflightBuilder.LoadAtoms(item.PdfPath);
            var scope = ClaimBindingScope.Create(item.Pack.OwnedAliases, item.Pack.VisibleAliases);

            string? transportError = null;
            string content = "";
            string? finishReason = null;
            IReadOnlyList<V5ToolCallDeltaFragment> fragments = [];
            JsonElement? usage = null;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                using var client = OpenRouterHeaderExtractor.CreateOwned(RemoteInferenceOptions.FromEnvironment());
                (content, finishReason, fragments, usage) = await client.ExecuteToolCallAsync(
                    item.FrozenBody, item.Pack.MaxCompletionTokens, V5SystemPromptV2_1.Text, item.Pack.Request.Prompt);
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
                var qualification = V5ForcedToolQualifierV1.Qualify(reassembled, ToolName, item.Pack.PackId, contract, atoms, scope);
                var sourceStats = SourceSelectionStats(qualification.Response);
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
                    aliasOnlyParts = sourceStats.AliasOnly,
                    verbatimTextParts = sourceStats.VerbatimText,
                    multiPartEndpoints = sourceStats.MultiPartEndpoints,
                    predicateDistribution = sourceStats.PredicateDistribution,
                    stateDistribution = sourceStats.StateDistribution,
                };
            }

            results.Add(new
            {
                role = item.Role,
                documentId = item.DocumentId,
                packId = item.Pack.PackId,
                providerRequestHash = item.FrozenHash,
                transportError,
                finishReason,
                latencyMs = stopwatch.Elapsed.TotalMilliseconds,
                usage,
                toolCallCount = reassembled.Count,
                toolCallFragmentsRawCount = fragments.Count,
                reassembledArguments = reassembled.Count > 0 ? reassembled[0].Arguments : null,
                reassembledArgumentsSha256 = reassembled.Count > 0 ? Sha256(reassembled[0].Arguments) : null,
                qualification = qualificationReport,
            });

            var q = qualificationReport as dynamic;
            Console.WriteLine(
                $"  -> [{item.Role}] {item.DocumentId} {item.Pack.PackId}: transportError={(transportError is null ? "none" : "YES")} " +
                $"toolCalls={reassembled.Count} outcome={(q?.outcome ?? "N/A")} latencyMs={stopwatch.Elapsed.TotalMilliseconds:0}");
        }

        var artifact = new
        {
            schemaVersion = "v5-qwen37-forced-tool-canary-result-v1",
            purpose = "MEASUREMENT_NOT_PROMOTION",
            variant = "A_structural_only",
            model = Model,
            provider = ProviderRoutingSlug,
            tool = ToolName,
            retryAttempt = retryNumber,
            sameFrozenBodiesAsAttempt0 = true,
            providerCalls = resolved.Count,
            goldRead = false,
            semanticRetries = 0,
            responseRepairApplied = false,
            providerExecutionAuthorized = true,
            results,
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
